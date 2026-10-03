namespace Janet.Azure.Storage;

/// <summary>
/// Retry and timeout budgets for the three storage services.
/// </summary>
/// <remarks>
/// Deliberately tighter than the SDK defaults, whose per-attempt network timeout is 100 seconds:
/// a call stuck that long holds its memory and its caller, and retrying sooner is the better bet.
/// Settable throughout so it binds from configuration, and a host can tune it from app settings
/// without a redeploy.
/// </remarks>
public sealed class StorageRetryOptions
{
    /// <summary>Gets or sets the budget for blob operations.</summary>
    /// <remarks>Blobs carry payloads, so they get the longer budget.</remarks>
    public StorageRetryBudget Blob { get; set; } = new()
    {
        MaxRetries = 4,
        DelayMilliseconds = 400,
        MaxDelayMilliseconds = 8_000,
        NetworkTimeoutSeconds = 30,
    };

    /// <summary>Gets or sets the budget for table operations.</summary>
    /// <remarks>Table calls are small metadata round trips and should fail fast.</remarks>
    public StorageRetryBudget Table { get; set; } = new()
    {
        MaxRetries = 4,
        DelayMilliseconds = 200,
        MaxDelayMilliseconds = 4_000,
        NetworkTimeoutSeconds = 15,
    };

    /// <summary>Gets or sets the budget for queue operations.</summary>
    public StorageRetryBudget Queue { get; set; } = new()
    {
        MaxRetries = 4,
        DelayMilliseconds = 200,
        MaxDelayMilliseconds = 4_000,
        NetworkTimeoutSeconds = 15,
    };
}

/// <summary>One service's retry budget.</summary>
public sealed class StorageRetryBudget
{
    /// <summary>Gets or sets the number of retries after the first attempt.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Gets or sets the delay before the first retry, in milliseconds.</summary>
    public int DelayMilliseconds { get; set; } = 400;

    /// <summary>Gets or sets the ceiling on the exponentially growing delay, in milliseconds.</summary>
    public int MaxDelayMilliseconds { get; set; } = 8_000;

    /// <summary>Gets or sets the per-attempt network timeout, in seconds.</summary>
    public int NetworkTimeoutSeconds { get; set; } = 30;
}
