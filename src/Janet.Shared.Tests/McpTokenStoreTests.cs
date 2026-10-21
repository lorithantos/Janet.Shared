using System.Buffers.Text;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Janet.Mcp.Security;
using Microsoft.Win32;
using Xunit;

namespace Janet.Shared.Tests;

/// <summary>
/// The DPAPI-protected token in the registry. Every test works under its own
/// HKCU\Software\Janet.Tests\&lt;guid&gt; and deletes it afterwards; nothing here opens the real
/// HKCU\Software\Janet, which <see cref="McpTokenStore.Default"/> names but these tests never use.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class McpTokenStoreTests : IDisposable
{
    private const string TestRoot = @"Software\Janet.Tests";

    private readonly string subKey = TestRoot + @"\" + Guid.NewGuid().ToString("n");
    private readonly McpTokenStore store;

    public McpTokenStoreTests()
    {
        Assert.StartsWith(TestRoot + @"\", subKey, StringComparison.Ordinal);
        store = new McpTokenStore(subKey);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(TestRoot);
        if (root is { SubKeyCount: 0, ValueCount: 0 })
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void An_absent_key_reads_as_no_token() =>
        Assert.Null(store.Read());

    [Fact]
    public void A_key_without_the_value_reads_as_no_token()
    {
        using (Registry.CurrentUser.CreateSubKey(subKey))
        {
        }

        Assert.Null(store.Read());
    }

    [Fact]
    public void Ensure_creates_a_token_once_and_then_returns_the_same_one()
    {
        string first = store.Ensure();
        string second = store.Ensure();

        Assert.Equal(first, second);
        Assert.Equal(first, store.Read());
        Assert.Equal(43, first.Length);
        Assert.Equal(McpTokenStore.TokenBytes, Base64Url.DecodeFromChars(first).Length);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void Rotate_stores_and_returns_a_different_token()
    {
        string before = store.Ensure();

        string rotated = store.Rotate();

        Assert.NotEqual(before, rotated);
        Assert.Equal(rotated, store.Read());
        Assert.Equal(rotated, store.Ensure());
    }

    [Fact]
    public void The_registry_holds_a_dpapi_blob_that_round_trips_to_the_token()
    {
        string token = store.Ensure();
        byte[] tokenBytes = Base64Url.DecodeFromChars(token);

        using RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey)!;
        Assert.Equal(RegistryValueKind.Binary, key.GetValueKind(McpTokenStore.ValueName));
        byte[] blob = (byte[])key.GetValue(McpTokenStore.ValueName)!;

        Assert.False(blob.AsSpan().IndexOf(tokenBytes) >= 0, "The token's bytes are stored in the clear.");
        byte[] decrypted = ProtectedData.Unprotect(
            blob, Encoding.UTF8.GetBytes(McpTokenStore.EntropyText), DataProtectionScope.CurrentUser);
        Assert.Equal(tokenBytes, decrypted);
    }

    [Fact]
    public void A_corrupt_blob_fails_loudly_naming_the_key_and_is_not_replaced()
    {
        byte[] corrupt = [1, 2, 3, 4, 5, 6, 7, 8];
        WriteRaw(corrupt, RegistryValueKind.Binary);

        InvalidOperationException read = Assert.Throws<InvalidOperationException>(() => store.Read());
        InvalidOperationException ensure = Assert.Throws<InvalidOperationException>(() => store.Ensure());

        Assert.Contains($@"HKCU\{subKey}\McpToken", read.Message);
        Assert.Contains("Rotate", read.Message);
        Assert.Equal(read.Message, ensure.Message);
        Assert.Equal(corrupt, ReadRaw());
    }

    [Fact]
    public void A_blob_protected_with_different_entropy_fails_loudly()
    {
        byte[] blob = ProtectedData.Protect(
            RandomNumberGenerator.GetBytes(McpTokenStore.TokenBytes),
            Encoding.UTF8.GetBytes("some other purpose"),
            DataProtectionScope.CurrentUser);
        WriteRaw(blob, RegistryValueKind.Binary);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => store.Read());

        Assert.Contains($@"HKCU\{subKey}\McpToken", ex.Message);
        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void A_blob_that_decrypts_to_the_wrong_length_fails_loudly()
    {
        byte[] blob = ProtectedData.Protect(
            RandomNumberGenerator.GetBytes(16),
            Encoding.UTF8.GetBytes(McpTokenStore.EntropyText),
            DataProtectionScope.CurrentUser);
        WriteRaw(blob, RegistryValueKind.Binary);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => store.Read());

        Assert.Contains("16 bytes", ex.Message);
    }

    [Fact]
    public void A_value_of_the_wrong_kind_fails_loudly()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey))
        {
            key.SetValue(McpTokenStore.ValueName, "not a blob", RegistryValueKind.String);
        }

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => store.Read());

        Assert.Contains("REG_BINARY", ex.Message);
    }

    [Fact]
    public void Rotate_is_the_fix_for_an_unreadable_value()
    {
        WriteRaw([9, 9, 9], RegistryValueKind.Binary);

        string rotated = store.Rotate();

        Assert.Equal(rotated, store.Read());
    }

    [Fact]
    public void The_default_store_names_hkcu_software_janet_without_touching_it()
    {
        McpTokenStore real = McpTokenStore.Default;

        Assert.Equal(@"Software\Janet", real.SubKey);
        Assert.Equal(@"HKCU\Software\Janet\McpToken", real.Location);
    }

    private void WriteRaw(byte[] value, RegistryValueKind kind)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue(McpTokenStore.ValueName, value, kind);
    }

    private byte[] ReadRaw()
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey)!;
        return (byte[])key.GetValue(McpTokenStore.ValueName)!;
    }
}
