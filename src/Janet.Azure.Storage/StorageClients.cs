using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace Janet.Azure.Storage;

/// <summary>
/// The service addresses of one storage account. A service left null is one the app does not use;
/// asking for its client throws rather than guessing an address.
/// </summary>
/// <param name="Blob">The blob endpoint, e.g. https://account.blob.core.windows.net/.</param>
/// <param name="Table">The table endpoint, e.g. https://account.table.core.windows.net/.</param>
/// <param name="Queue">The queue endpoint, e.g. https://account.queue.core.windows.net/.</param>
public sealed record StorageEndpoints(Uri? Blob = null, Uri? Table = null, Uri? Queue = null)
{
    /// <summary>The public-cloud endpoints of an account, derived from its name.</summary>
    /// <param name="accountName">The storage account name.</param>
    public static StorageEndpoints ForAccount(string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        return new(
            new Uri($"https://{accountName}.blob.core.windows.net"),
            new Uri($"https://{accountName}.table.core.windows.net"),
            new Uri($"https://{accountName}.queue.core.windows.net"));
    }
}

/// <summary>
/// One storage account's service clients, each built once, from the caller's credential and with
/// the caller's retry budget.
/// </summary>
/// <remarks>
/// It never chooses a credential. Which identity an app signs in as (a managed identity, a
/// developer's own sign-in, which credential types to allow, which tenant) is that app's decision,
/// so it is passed in. There is no constructor for a connection string, account key or SAS, and
/// every endpoint is checked by <see cref="EnsureServiceAddress"/>, so none can arrive on a URI.
/// </remarks>
public sealed class StorageClients
{
    private readonly Lazy<BlobServiceClient> _blobs;
    private readonly Lazy<TableServiceClient> _tables;
    private readonly Lazy<QueueServiceClient> _queues;

    /// <summary>Checks every given endpoint now, and builds each client on first use.</summary>
    /// <param name="endpoints">The account's service addresses.</param>
    /// <param name="credential">The identity to reach storage as, chosen by the app.</param>
    /// <param name="retry">The retry budgets; the defaults when omitted.</param>
    public StorageClients(StorageEndpoints endpoints, TokenCredential credential, StorageRetryOptions? retry = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(credential);
        retry ??= new StorageRetryOptions();

        // Checked here, at startup, rather than on whichever request first needs the service.
        foreach (var address in new[] { endpoints.Blob, endpoints.Table, endpoints.Queue })
            if (address is not null) EnsureServiceAddress(address);

        BlobOptionsUsed = BlobOptions(retry.Blob);
        TableOptionsUsed = TableOptions(retry.Table);
        QueueOptionsUsed = QueueOptions(retry.Queue);
        _blobs = new(() => new BlobServiceClient(Required(endpoints.Blob, "blob"), credential, BlobOptionsUsed));
        _tables = new(() => new TableServiceClient(Required(endpoints.Table, "table"), credential, TableOptionsUsed));
        _queues = new(() => new QueueServiceClient(Required(endpoints.Queue, "queue"), credential, QueueOptionsUsed));
    }

    // The options each client is built with. The SDK clients do not expose their options, so these
    // let the tests pin that the clients an app uses carry its budget, not merely that a budget
    // can be turned into options.
    internal BlobClientOptions BlobOptionsUsed { get; }

    internal TableClientOptions TableOptionsUsed { get; }

    internal QueueClientOptions QueueOptionsUsed { get; }

    /// <summary>Gets the blob service client.</summary>
    /// <exception cref="InvalidOperationException">No blob endpoint was given.</exception>
    public BlobServiceClient Blobs => _blobs.Value;

    /// <summary>Gets the table service client.</summary>
    /// <exception cref="InvalidOperationException">No table endpoint was given.</exception>
    public TableServiceClient Tables => _tables.Value;

    /// <summary>Gets the queue service client.</summary>
    /// <exception cref="InvalidOperationException">No queue endpoint was given.</exception>
    public QueueServiceClient Queues => _queues.Value;

    /// <summary>
    /// Refuses anything but an absolute https address with no query string. A query string on a
    /// storage address is how a SAS token travels, so none is accepted, whatever it holds.
    /// </summary>
    /// <param name="serviceUri">The address to check.</param>
    /// <exception cref="ArgumentException">The address is not https, not absolute, or carries a query.</exception>
    public static void EnsureServiceAddress(Uri serviceUri)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        if (!serviceUri.IsAbsoluteUri || serviceUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A storage service address must be an absolute https:// URI.", nameof(serviceUri));
        if (!string.IsNullOrEmpty(serviceUri.Query))
            throw new ArgumentException(
                "A storage service address must not carry a query string; SAS tokens are not accepted.",
                nameof(serviceUri));
    }

    /// <summary>Blob client options carrying a retry budget.</summary>
    /// <param name="budget">The budget to apply.</param>
    public static BlobClientOptions BlobOptions(StorageRetryBudget budget)
    {
        BlobClientOptions options = new();
        Apply(options.Retry, budget);
        return options;
    }

    /// <summary>Table client options carrying a retry budget.</summary>
    /// <param name="budget">The budget to apply.</param>
    public static TableClientOptions TableOptions(StorageRetryBudget budget)
    {
        TableClientOptions options = new();
        Apply(options.Retry, budget);
        return options;
    }

    /// <summary>Queue client options carrying a retry budget.</summary>
    /// <param name="budget">The budget to apply.</param>
    public static QueueClientOptions QueueOptions(StorageRetryBudget budget)
    {
        QueueClientOptions options = new();
        Apply(options.Retry, budget);
        return options;
    }

    private static void Apply(RetryOptions retry, StorageRetryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        // Exponential rather than fixed: throttling wants backing off, not a steady drumbeat. The
        // SDK honours Retry-After on 429 and 503 whatever the mode.
        retry.Mode = RetryMode.Exponential;
        retry.MaxRetries = budget.MaxRetries;
        retry.Delay = TimeSpan.FromMilliseconds(budget.DelayMilliseconds);
        retry.MaxDelay = TimeSpan.FromMilliseconds(budget.MaxDelayMilliseconds);
        retry.NetworkTimeout = TimeSpan.FromSeconds(budget.NetworkTimeoutSeconds);
    }

    private static Uri Required(Uri? endpoint, string service) =>
        endpoint ?? throw new InvalidOperationException(
            $"No {service} endpoint was given, so there is no {service} client. Pass it in StorageEndpoints.");
}
