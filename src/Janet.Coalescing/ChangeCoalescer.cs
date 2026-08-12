namespace Janet.Coalescing;

using System.Threading.Channels;

/// <summary>
/// One buffered input-side change: the partition it belongs to and the payload
/// it carried. Postings survive to the drain untouched — the queue buffers, it
/// never merges — so a destination folding its batch owes nothing to
/// associativity: average, first-and-last, or Max over the whole list are all
/// legal collapses.
/// </summary>
public readonly record struct Posting<TKey>(TKey Key, object? Value) where TKey : notnull;

/// <summary>
/// Collapses bursts of input-side changes into output passes at a bounded
/// cadence. The first posting into an empty queue opens a window; one frame
/// later everything drains, and each registered destination receives the
/// postings under its keys as one batch. Delivery therefore happens at most once
/// per frame however fast producers run — input rate changes batch size, never
/// cadence. The window is fixed, not a debounce: a mid-window posting joins the
/// batch without pushing the deadline, so sustained input drains every frame
/// instead of deferring forever, and worst-case latency is one window.
/// </summary>
/// <typeparam name="TKey">
/// The caller's partition vocabulary, normally an enum. A key answers only "may
/// these be combined at all" — it never carries a merge function, because
/// folding is each destination's business and two destinations may fold the same
/// key differently.
/// </typeparam>
/// <remarks>
/// Threading: concurrent ingress, single-threaded egress. <see cref="Post"/> may
/// be called from any thread and is one lock-free enqueue with no scheduling
/// work. <see cref="Register"/> and delivery belong to the thread that runs
/// <see cref="RunAsync"/> — in a desktop app, the UI thread.
/// </remarks>
public sealed class ChangeCoalescer<TKey> where TKey : notnull
{
    /// <summary>
    /// One hundred milliseconds: a fast typing burst (~24 ms per character)
    /// collapses about four postings into each delivery, output passes are capped
    /// at ten per second, and the added lag stays imperceptible. The policy lives
    /// here so an app and any test that builds one without naming a frame agree
    /// on the number.
    /// </summary>
    public static readonly TimeSpan DefaultFrameLength = TimeSpan.FromMilliseconds(100);

    private readonly Channel<Posting<TKey>> queue = Channel.CreateUnbounded<Posting<TKey>>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly List<Registration> registrations = [];
    private readonly TimeSpan frameLength;
    private readonly TimeProvider time;
    private int running;

    /// <param name="frameLength">
    /// How long a window stays open. <see cref="DefaultFrameLength"/> unless you
    /// have a reason; shorter costs output passes, longer costs perceived lag.
    /// </param>
    /// <param name="time">
    /// Injected so tests can advance time by hand rather than sleeping. Leave null
    /// outside tests.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The frame length is not positive.</exception>
    public ChangeCoalescer(TimeSpan frameLength, TimeProvider? time = null)
    {
        if (frameLength <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(frameLength), frameLength, "The frame length must be positive.");

        this.frameLength = frameLength;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Posts one change. Safe from any thread; never blocks, never schedules. A
    /// posting with no registered destination is drained and dropped.
    /// </summary>
    public void Post(TKey key, object? value = null)
        => this.queue.Writer.TryWrite(new Posting<TKey>(key, value));

    /// <summary>
    /// Registers a destination for exactly the keys that concern it. Per drain it
    /// is invoked at most once, with its keys' postings in arrival order —
    /// collapsing them is its business, not the machine's. Call from the delivery
    /// thread; there is deliberately no unregister, because destinations are
    /// expected to live as long as the shell that owns them.
    /// </summary>
    public void Register(IReadOnlyList<TKey> keys, Action<IReadOnlyList<Posting<TKey>>> destination)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(destination);

        if (keys.Count == 0)
            throw new ArgumentException("A destination must register at least one key.", nameof(keys));

        this.registrations.Add(new Registration([.. keys], destination));
    }

    /// <summary>
    /// The consumer loop. Start it exactly once, from the thread deliveries
    /// belong on: every await resumes on the caller's context, so starting it on
    /// the UI thread is the whole thread-affinity story — no explicit marshalling
    /// exists anywhere. Never add ConfigureAwait(false) here; it would silently
    /// move delivery onto a pool thread.
    /// <para>
    /// "A window is open" is this method sitting at the delay — deliberately not
    /// a flag, which is what dissolves the drain/post races a flagged design has
    /// to argue benign. Drains never overlap because the next window cannot open
    /// before delivery returns; a slow destination stretches cadence rather than
    /// re-entering, and the queue absorbs the difference. A destination that
    /// throws faults this task: delivery is invalidation, invalidation must not
    /// throw, and swallowing here would turn broken wiring into a silently dead
    /// UI.
    /// </para>
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested. Buffered postings are dropped, which only
    /// happens at shutdown.
    /// </exception>
    public async Task RunAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref this.running, 1) != 0)
            throw new InvalidOperationException("The consumer loop is already running.");

        // Waiting for the first element costs nothing while idle; a heartbeat
        // timer would burn wakeups, and idle is most of the time.
        while (await this.queue.Reader.WaitToReadAsync(cancellation))
        {
            await Task.Delay(this.frameLength, this.time, cancellation);

            // Greedy drain, pinned by test: read until the queue reports empty, so
            // a posting that lands while the drain runs catches this frame. A
            // snapshot drain would defer it a full window — same amount of code,
            // materially different behaviour, and easy to lose to a refactor.
            var batch = new List<Posting<TKey>>();

            while (this.queue.Reader.TryRead(out var posting))
                batch.Add(posting);

            this.Deliver(batch);
        }
    }

    // Fan-out lives at drain, not at post: the hot path stays one enqueue, the
    // bookkeeping runs on the already-single-threaded side, and registration
    // cannot race the buffers.
    private void Deliver(List<Posting<TKey>> batch)
    {
        foreach (var registration in this.registrations)
        {
            List<Posting<TKey>>? matched = null;

            foreach (var posting in batch)
            {
                if (Array.IndexOf(registration.Keys, posting.Key) >= 0)
                    (matched ??= []).Add(posting);
            }

            if (matched is not null)
                registration.Destination(matched);
        }
    }

    private readonly record struct Registration(
        TKey[] Keys, Action<IReadOnlyList<Posting<TKey>>> Destination);
}
