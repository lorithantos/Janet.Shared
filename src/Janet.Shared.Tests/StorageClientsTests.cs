using Azure.Core;
using Janet.Azure.Storage;
using Xunit;

namespace Janet.Shared.Tests;

public class StorageClientsTests
{
    private static readonly Uri Blob = new("https://acct.blob.core.windows.net/");
    private static readonly Uri Table = new("https://acct.table.core.windows.net/");

    [Fact]
    public void Blob_options_carry_the_blob_budget()
    {
        RetryOptions retry = StorageClients.BlobOptions(new StorageRetryOptions().Blob).Retry;

        Assert.Equal(RetryMode.Exponential, retry.Mode);
        Assert.Equal(4, retry.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(400), retry.Delay);
        Assert.Equal(TimeSpan.FromSeconds(8), retry.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), retry.NetworkTimeout);
    }

    [Fact]
    public void Table_and_queue_options_carry_the_shorter_budget()
    {
        var defaults = new StorageRetryOptions();

        foreach (RetryOptions retry in new[]
                 {
                     StorageClients.TableOptions(defaults.Table).Retry,
                     StorageClients.QueueOptions(defaults.Queue).Retry,
                 })
        {
            Assert.Equal(RetryMode.Exponential, retry.Mode);
            Assert.Equal(TimeSpan.FromMilliseconds(200), retry.Delay);
            Assert.Equal(TimeSpan.FromSeconds(4), retry.MaxDelay);
            Assert.Equal(TimeSpan.FromSeconds(15), retry.NetworkTimeout);
        }
    }

    [Fact]
    public void A_configured_budget_is_the_one_applied()
    {
        var budget = new StorageRetryBudget { MaxRetries = 7, DelayMilliseconds = 250, MaxDelayMilliseconds = 9_000, NetworkTimeoutSeconds = 11 };

        RetryOptions retry = StorageClients.TableOptions(budget).Retry;

        Assert.Equal(7, retry.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(250), retry.Delay);
        Assert.Equal(TimeSpan.FromSeconds(9), retry.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(11), retry.NetworkTimeout);
    }

    // The built client must carry the caller's budget, not merely be buildable: the budget only
    // matters if the client the app actually uses was given it.
    [Fact]
    public void The_clients_are_built_with_the_callers_retry_budget()
    {
        var retry = new StorageRetryOptions { Blob = { NetworkTimeoutSeconds = 5 }, Table = { MaxRetries = 2 } };
        var clients = new StorageClients(new StorageEndpoints(Blob, Table), new NoCredential(), retry);

        Assert.Equal(Blob, clients.Blobs.Uri);
        Assert.Equal("acct", clients.Tables.AccountName);
        Assert.Equal(TimeSpan.FromSeconds(5), clients.BlobOptionsUsed.Retry.NetworkTimeout);
        Assert.Equal(2, clients.TableOptionsUsed.Retry.MaxRetries);
    }

    [Theory]
    [InlineData("http://acct.blob.core.windows.net/")]
    [InlineData("https://acct.blob.core.windows.net/?sv=2024-01-01&sig=abc")]
    [InlineData("https://acct.blob.core.windows.net/?anything")]
    public void A_plain_https_address_is_the_only_kind_accepted(string address) =>
        Assert.Throws<ArgumentException>(() =>
            new StorageClients(new StorageEndpoints(Blob: new Uri(address)), new NoCredential()));

    [Fact]
    public void A_service_left_out_has_no_client()
    {
        var clients = new StorageClients(new StorageEndpoints(Blob: Blob), new NoCredential());

        var ex = Assert.Throws<InvalidOperationException>(() => clients.Tables);
        Assert.Contains("table", ex.Message);
    }

    [Fact]
    public void An_account_name_gives_the_public_cloud_endpoints()
    {
        var endpoints = StorageEndpoints.ForAccount("acct");

        Assert.Equal(new Uri("https://acct.blob.core.windows.net"), endpoints.Blob);
        Assert.Equal(new Uri("https://acct.table.core.windows.net"), endpoints.Table);
        Assert.Equal(new Uri("https://acct.queue.core.windows.net"), endpoints.Queue);
    }

    private sealed class NoCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
