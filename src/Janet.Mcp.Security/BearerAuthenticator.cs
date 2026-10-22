using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Janet.Mcp.Security;

/// <summary>How a server treats the bearer token.</summary>
public enum BearerMode
{
    /// <summary>No check and no counting: every request goes through untouched.</summary>
    Off,

    /// <summary>
    /// Every request goes through, and each one's outcome is counted. The staged step before
    /// <see cref="Require"/>: it shows whether clients are sending the header before anything is
    /// refused.
    /// </summary>
    Report,

    /// <summary>A request without the right token is answered 401.</summary>
    Require,
}

/// <summary>What one request's Authorization header amounted to.</summary>
public enum BearerOutcome
{
    /// <summary>A bearer token matching the server's.</summary>
    Ok,

    /// <summary>No Authorization header at all.</summary>
    Missing,

    /// <summary>An Authorization header that is not the server's bearer token: another scheme,
    /// an empty token, more than one header, or a token that does not match.</summary>
    Wrong,

    /// <summary>The server has no token to compare against: its provider returned none, or
    /// failed. Judged before the header, because no header can be right.</summary>
    Unconfigured,
}

/// <summary>
/// The bearer check's state for a server's /health: the mode, whether a token is configured, and
/// how many requests came out each way since the server started. Carries no token, expected or
/// received. Mode is a string so it serializes readably with default JSON options.
/// </summary>
/// <param name="Mode">The <see cref="BearerMode"/>, by name.</param>
/// <param name="TokenConfigured">Whether the provider last returned a token.</param>
/// <param name="TokenReadAt">When the provider was last asked, or null in <see cref="BearerMode.Off"/>.</param>
/// <param name="ProviderError">Why the provider last failed, or null when it did not.</param>
/// <param name="Ok">Requests that carried the right token.</param>
/// <param name="Missing">Requests that carried no Authorization header.</param>
/// <param name="Wrong">Requests whose Authorization header was not the right bearer token.</param>
/// <param name="Unconfigured">Requests that arrived while the server had no token.</param>
public sealed record BearerAuthSnapshot(
    string Mode,
    bool TokenConfigured,
    DateTimeOffset? TokenReadAt,
    string? ProviderError,
    long Ok,
    long Missing,
    long Wrong,
    long Unconfigured);

/// <summary>
/// Decides whether a request carries the server's bearer token, and counts the outcomes.
/// </summary>
/// <remarks>
/// The expected token comes from a provider the server supplies, and is held only as its SHA-256.
/// A presented token is hashed the same way and the two 32-byte hashes are compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/>, so neither the comparison nor its length
/// check says anything about how much of a guess was right.
///
/// The provider is asked once at construction and the answer cached. The common requests -- the
/// right token, or no header -- judge against that cache and touch nothing else. A request whose
/// token does not match, or that arrives while there is no token, asks for a refresh and waits for
/// the answer. Refreshes are served by ONE consumer (pattern.single-consumer-queue): a loop
/// reading a single-reader channel, which takes every request waiting as one batch, asks the
/// provider at most once for it, and completes every request in the batch with the result. It
/// asks only when <see cref="RefreshInterval"/> has passed since it last asked; otherwise the batch
/// gets the cached answer. So a rotated token is picked up without a restart, a stream of wrong
/// guesses cannot become a stream of registry reads or command runs, and there is no election of
/// who refreshes: the consumer is the only code that calls the provider after construction and
/// the only writer of the cached answer. Interlocked is used only for the counters, which
/// coordinate nothing.
///
/// A provider that throws is treated as having no token, and its error is kept for
/// <see cref="Snapshot"/>; the consumer never throws it. <see cref="Dispose"/> closes the channel,
/// and every request still waiting for a refresh is faulted with
/// <see cref="ObjectDisposedException"/> rather than left hanging.
///
/// The provider runs on the consumer, so a provider that called back into this authenticator and
/// waited would wait on itself. That cannot happen here: the provider is the server's token reader
/// (a registry read, or a command run once) and has no path to the authenticator, so there is no
/// [ThreadStatic] re-entrancy refusal. Add one if a provider ever could.
///
/// Nothing here logs or echoes a token. Bodies and the snapshot name outcomes, never values.
/// </remarks>
public sealed class BearerAuthenticator : IDisposable
{
    /// <summary>The shortest gap between two provider calls.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The one path the check never refuses or counts. It carries no secret, and it is where the
    /// counters are read; counting the servers' own liveness probes would bury the clients.
    /// </summary>
    public static readonly PathString ExemptPath = new("/health");

    /// <summary>
    /// The sentence that ends every 401 when the server supplies no hint of its own. It names no
    /// tool: this library does not know how a given server's clients get their token.
    /// </summary>
    public const string DefaultHeaderHint =
        "Send the token this server is configured with.";

    private readonly Func<string?> provider;
    private readonly TimeProvider time;
    private readonly string headerHint;

    private readonly Channel<TaskCompletionSource<Expected>> refreshes =
        Channel.CreateUnbounded<TaskCompletionSource<Expected>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource stopping = new();

    // Written by the constructor before the consumer starts, then only by the consumer.
    private Expected expected;

    // Consumer-only after construction.
    private DateTimeOffset lastAsked;

    private long ok;
    private long missing;
    private long wrong;
    private long unconfigured;

    /// <summary>
    /// Creates the check. Unless <paramref name="mode"/> is Off, the provider is asked now and the
    /// refresh consumer is started.
    /// </summary>
    /// <param name="mode">Off, Report or Require.</param>
    /// <param name="tokenProvider">
    /// Returns the token requests must carry, or null when there is none. Surrounding whitespace is
    /// ignored, so a command's trailing newline is harmless. Called on the refresh consumer, one
    /// call at a time.
    /// </param>
    /// <param name="timeProvider">The clock the refresh throttle runs on; the system clock when null.</param>
    /// <param name="headerHint">
    /// The sentence that ends every 401, telling a client where to get the header to send -- for
    /// example the command that prints it. <see cref="DefaultHeaderHint"/> when null. In the
    /// no-token refusal it follows "Once the server has its token, ", so its first letter is
    /// lowercased there. It must name no token: it is sent to anyone refused.
    /// </param>
    /// <exception cref="ArgumentException">When <paramref name="headerHint"/> is empty or whitespace.</exception>
    public BearerAuthenticator(
        BearerMode mode,
        Func<string?> tokenProvider,
        TimeProvider? timeProvider = null,
        string? headerHint = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Off, Report or Require.");
        }

        ArgumentNullException.ThrowIfNull(tokenProvider);
        if (headerHint is not null && string.IsNullOrWhiteSpace(headerHint))
        {
            throw new ArgumentException("A header hint is a sentence; pass null for the default.", nameof(headerHint));
        }

        Mode = mode;
        provider = tokenProvider;
        time = timeProvider ?? TimeProvider.System;
        this.headerHint = headerHint?.Trim() ?? DefaultHeaderHint;

        if (mode == BearerMode.Off)
        {
            expected = new Expected(null, null, null);
            refreshes.Writer.TryComplete();
            return;
        }

        lastAsked = time.GetUtcNow();
        expected = Ask(lastAsked);
        _ = Task.Run(ConsumeAsync);
    }

    /// <summary>The mode this check was created with.</summary>
    public BearerMode Mode { get; }

    /// <summary>
    /// Judges one request's Authorization header and counts the outcome. The caller decides what
    /// to do with it; <see cref="McpSecurityExtensions.UseMcpSecurity"/> refuses anything but
    /// <see cref="BearerOutcome.Ok"/> in <see cref="BearerMode.Require"/>.
    /// </summary>
    /// <param name="authorization">Every Authorization header value the request carried.</param>
    /// <exception cref="InvalidOperationException">In <see cref="BearerMode.Off"/>, which checks nothing.</exception>
    /// <exception cref="ObjectDisposedException">When a refresh is needed after or during disposal.</exception>
    public async ValueTask<BearerOutcome> AuthenticateAsync(StringValues authorization)
    {
        if (Mode == BearerMode.Off)
        {
            throw new InvalidOperationException("The bearer check is Off: it judges nothing and asks no provider.");
        }

        BearerOutcome outcome = await ClassifyAsync(authorization).ConfigureAwait(false);
        switch (outcome)
        {
            case BearerOutcome.Ok:
                Interlocked.Increment(ref ok);
                break;
            case BearerOutcome.Missing:
                Interlocked.Increment(ref missing);
                break;
            case BearerOutcome.Wrong:
                Interlocked.Increment(ref wrong);
                break;
            default:
                Interlocked.Increment(ref unconfigured);
                break;
        }

        return outcome;
    }

    /// <summary>The counters and token state, for a server's /health.</summary>
    public BearerAuthSnapshot Snapshot()
    {
        Expected current = Volatile.Read(ref expected);
        return new BearerAuthSnapshot(
            Mode.ToString(),
            current.Hash is not null,
            current.AskedAt,
            current.Error,
            Interlocked.Read(ref ok),
            Interlocked.Read(ref missing),
            Interlocked.Read(ref wrong),
            Interlocked.Read(ref unconfigured));
    }

    /// <summary>The plain-text 401 body for an outcome other than Ok. Names no token.</summary>
    /// <param name="outcome">The refused outcome.</param>
    public string RefusalMessage(BearerOutcome outcome) => outcome switch
    {
        BearerOutcome.Missing =>
            "Refused: this server requires a bearer token (Authorization: Bearer <token>) and the " +
            "request carried no Authorization header. " + headerHint,
        BearerOutcome.Wrong =>
            "Refused: this server requires a bearer token (Authorization: Bearer <token>), and the " +
            "Authorization header sent is not it -- another scheme, or a token that is not current " +
            "(it may have been rotated). " + headerHint,
        BearerOutcome.Unconfigured =>
            "Refused: this server requires a bearer token but has no token configured, so it can " +
            "accept no request." +
            (Volatile.Read(ref expected).Error is null
                ? string.Empty
                : " Reading the token failed; GET /health says why.") +
            " Once the server has its token, " + char.ToLowerInvariant(headerHint[0]) + headerHint[1..],
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Ok is not refused."),
    };

    /// <summary>
    /// Stops the refresh consumer. Requests still waiting for a refresh fail with
    /// <see cref="ObjectDisposedException"/>, and later ones that need a refresh fail the same way.
    /// </summary>
    public void Dispose()
    {
        refreshes.Writer.TryComplete();
        stopping.Cancel();
    }

    private async ValueTask<BearerOutcome> ClassifyAsync(StringValues authorization)
    {
        Expected current = Volatile.Read(ref expected);
        if (current.Hash is null)
        {
            current = await RefreshAsync().ConfigureAwait(false);
            if (current.Hash is null)
            {
                return BearerOutcome.Unconfigured;
            }
        }

        if (StringValues.IsNullOrEmpty(authorization))
        {
            return BearerOutcome.Missing;
        }

        if (authorization.Count != 1 || !TryReadBearer(authorization[0], out string token))
        {
            return BearerOutcome.Wrong;
        }

        byte[] presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        if (CryptographicOperations.FixedTimeEquals(presented, current.Hash))
        {
            return BearerOutcome.Ok;
        }

        // A mismatch may mean the token was rotated under a running server. The consumer decides
        // whether that is worth asking the provider about; either way it answers with the newest
        // token it has.
        Expected fresh = await RefreshAsync().ConfigureAwait(false);
        if (fresh.Hash is null)
        {
            return BearerOutcome.Unconfigured;
        }

        return CryptographicOperations.FixedTimeEquals(presented, fresh.Hash)
            ? BearerOutcome.Ok
            : BearerOutcome.Wrong;
    }

    private static bool TryReadBearer(string? header, out string token)
    {
        token = string.Empty;
        if (header is null)
        {
            return false;
        }

        string trimmed = header.Trim();
        int space = trimmed.IndexOf(' ');
        if (space < 0 || !string.Equals(trimmed[..space], "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = trimmed[(space + 1)..].Trim();
        return token.Length > 0;
    }

    /// <summary>Queues a refresh request and returns the caller's wait for its answer.</summary>
    private Task<Expected> RefreshAsync()
    {
        TaskCompletionSource<Expected> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!refreshes.Writer.TryWrite(waiter))
        {
            throw new ObjectDisposedException(nameof(BearerAuthenticator), "The bearer check has been disposed.");
        }

        return waiter.Task;
    }

    /// <summary>
    /// The one reader of the refresh channel. Waits for a request, takes everything queued as one
    /// batch, answers the batch with one throttled provider call, and waits again.
    /// </summary>
    private async Task ConsumeAsync()
    {
        ChannelReader<TaskCompletionSource<Expected>> reader = refreshes.Reader;
        List<TaskCompletionSource<Expected>> batch = [];
        Exception? failure = null;

        try
        {
            while (await reader.WaitToReadAsync(stopping.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out TaskCompletionSource<Expected>? waiter))
                {
                    batch.Add(waiter);
                }

                if (stopping.IsCancellationRequested)
                {
                    break;
                }

                Expected answer = RefreshIfDue();
                foreach (TaskCompletionSource<Expected> waiter in batch)
                {
                    waiter.TrySetResult(answer);
                }

                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Disposed, or the loop itself broke: nobody is left to answer, so close the channel to
        // new requests and fail everything in hand or still queued rather than leave it waiting.
        // TryComplete comes first, so nothing can be written after the drain below.
        refreshes.Writer.TryComplete();
        while (reader.TryRead(out TaskCompletionSource<Expected>? waiter))
        {
            batch.Add(waiter);
        }

        Exception reason = failure ?? new ObjectDisposedException(
            nameof(BearerAuthenticator), "The bearer check was disposed while this request waited for a token refresh.");
        foreach (TaskCompletionSource<Expected> waiter in batch)
        {
            waiter.TrySetException(reason);
        }
    }

    /// <summary>Consumer only. Asks the provider when the throttle allows; the newest answer either way.</summary>
    private Expected RefreshIfDue()
    {
        DateTimeOffset now = time.GetUtcNow();
        if (now - lastAsked < RefreshInterval)
        {
            return expected;
        }

        lastAsked = now;
        Expected fresh = Ask(now);
        Volatile.Write(ref expected, fresh);
        return fresh;
    }

    private Expected Ask(DateTimeOffset now)
    {
        try
        {
            string? token = provider()?.Trim();
            return string.IsNullOrEmpty(token)
                ? new Expected(null, null, now)
                : new Expected(SHA256.HashData(Encoding.UTF8.GetBytes(token)), null, now);
        }
        catch (Exception ex)
        {
            // Kept for /health rather than thrown: in Report mode a failing provider must not take
            // the server down, and in Require mode the 401 already says no token is configured.
            return new Expected(null, ex.GetType().Name + ": " + ex.Message, now);
        }
    }

    private sealed record Expected(byte[]? Hash, string? Error, DateTimeOffset? AskedAt);
}
