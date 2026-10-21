using System.Buffers.Text;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Janet.Mcp.Security;

/// <summary>
/// The per-machine bearer secret the local MCP servers expect, kept in the current user's
/// registry hive encrypted with DPAPI.
/// </summary>
/// <remarks>
/// The token is 32 bytes from <see cref="RandomNumberGenerator"/>, handed out as unpadded base64url
/// (43 characters). What the registry holds is not the token but the DPAPI blob of its bytes,
/// protected with <see cref="DataProtectionScope.CurrentUser"/> and <see cref="Entropy"/>, as
/// REG_BINARY in <see cref="ValueName"/> under the store's key (HKCU\Software\Janet for
/// <see cref="Default"/>). So a copied hive, a backup, or a read from another account gets bytes
/// that cannot be decrypted without this user's logon.
///
/// A value that is there but cannot be read back -- not REG_BINARY, not decryptable by this user
/// with this entropy, or not 32 bytes once decrypted -- throws an
/// <see cref="InvalidOperationException"/> naming the key and saying to rotate. It is never
/// silently replaced: a fresh token would leave every configured client holding a header that no
/// longer matches, with nothing to say why.
///
/// <see cref="Ensure"/> is check-then-write. Two processes creating the first token at the same
/// moment can both write; each then returns what it reads back, so the loser of the race may
/// return the winner's token or its own, and a client that fetched in between would need to fetch
/// again. Nothing here starts two servers at once on a machine with no token, so this is left
/// unlocked.
///
/// Windows only, because DPAPI and the registry are. On any other OS the constructor throws
/// <see cref="PlatformNotSupportedException"/>; there is deliberately no fallback store.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class McpTokenStore
{
    /// <summary>The key <see cref="Default"/> uses, relative to HKEY_CURRENT_USER.</summary>
    public const string DefaultSubKey = @"Software\Janet";

    /// <summary>The registry value that holds the DPAPI blob.</summary>
    public const string ValueName = "McpToken";

    /// <summary>
    /// The optional entropy passed to DPAPI, as UTF-8. Not a secret: it ties the blob to this
    /// purpose, so a blob some other program protected for the same user does not decrypt as a
    /// token. Changing it makes every stored token unreadable, so it carries a version.
    /// </summary>
    public const string EntropyText = "Janet.Mcp.Security/McpToken/v1";

    /// <summary>How many random bytes a token is made of.</summary>
    public const int TokenBytes = 32;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(EntropyText);

    /// <summary>
    /// Creates a store over a key under HKEY_CURRENT_USER. Tests pass a throwaway key here so the
    /// real one is never touched.
    /// </summary>
    /// <param name="subKey">The key, relative to HKEY_CURRENT_USER, e.g. <c>Software\Janet</c>.</param>
    /// <exception cref="PlatformNotSupportedException">When not running on Windows.</exception>
    public McpTokenStore(string subKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "McpTokenStore keeps the MCP bearer token in the Windows registry under DPAPI, and " +
                "there is no store for this operating system.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);
        SubKey = subKey.Trim('\\');
    }

    /// <summary>The store over HKCU\Software\Janet, which is what the servers and clients use.</summary>
    public static McpTokenStore Default => new(DefaultSubKey);

    /// <summary>The key, relative to HKEY_CURRENT_USER.</summary>
    public string SubKey { get; }

    /// <summary>The value's full name, for messages: <c>HKCU\&lt;key&gt;\McpToken</c>.</summary>
    public string Location => $@"HKCU\{SubKey}\{ValueName}";

    /// <summary>The stored token, or null when there is none.</summary>
    /// <exception cref="InvalidOperationException">
    /// When a value is stored but cannot be read back as a token. The message names the value
    /// and says to rotate.
    /// </exception>
    public string? Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SubKey, writable: false);
        if (key is null)
        {
            return null;
        }

        object? value = key.GetValue(ValueName);
        if (value is null)
        {
            return null;
        }

        if (key.GetValueKind(ValueName) != RegistryValueKind.Binary || value is not byte[] blob)
        {
            throw Unreadable($"it is {key.GetValueKind(ValueName)}, not REG_BINARY");
        }

        byte[] token;
        try
        {
            token = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw Unreadable(
                "DPAPI could not decrypt it for this Windows user (" + ex.Message.Trim() + "). It was " +
                "written by another account or machine, with different entropy, or it is corrupt",
                ex);
        }

        try
        {
            if (token.Length != TokenBytes)
            {
                throw Unreadable($"it decrypts to {token.Length} bytes, not {TokenBytes}");
            }

            return Base64Url.EncodeToString(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    /// <summary>
    /// The stored token, creating and storing one first when there is none. Calling it again
    /// returns the same token.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When a value is stored but cannot be read back; it is not replaced.
    /// </exception>
    public string Ensure()
    {
        string? existing = Read();
        if (existing is not null)
        {
            return existing;
        }

        Write();
        return Read() ?? throw new InvalidOperationException(
            $"{Location} was written but reads back as absent. Something is deleting it.");
    }

    /// <summary>
    /// Replaces the stored token, readable or not, with a new one and returns it. Every client
    /// holding the old header must fetch the new one.
    /// </summary>
    public string Rotate()
    {
        Write();
        return Read() ?? throw new InvalidOperationException(
            $"{Location} was written but reads back as absent. Something is deleting it.");
    }

    private void Write()
    {
        byte[] token = RandomNumberGenerator.GetBytes(TokenBytes);
        try
        {
            byte[] blob = ProtectedData.Protect(token, Entropy, DataProtectionScope.CurrentUser);
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
            key.SetValue(ValueName, blob, RegistryValueKind.Binary);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private InvalidOperationException Unreadable(string why, Exception? inner = null) =>
        new(
            $"{Location} holds an MCP token that cannot be read: {why}. It has not been replaced. " +
            "Rotate it to write a fresh one (McpTokenStore.Rotate), then give every client the new " +
            "header.",
            inner);
}
