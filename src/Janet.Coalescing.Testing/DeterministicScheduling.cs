using Janet.Coalescing;

namespace Janet.Coalescing.Testing;

/// <summary>
/// A coalescer with its consumer loop already parked at the empty-queue
/// await, on a deterministic pump over a fake clock — the standing setup for
/// any test that drives deliveries.
/// </summary>
public sealed class CoalescerHarness<TKey> where TKey : notnull
{
    /// <summary>The context the loop resumes on. Pump it rather than yielding.</summary>
    public DeterministicPump Pump { get; } = new();

    /// <summary>The clock the coalescer sees. Advance it rather than sleeping.</summary>
    public FakeTime Time { get; } = new();

    /// <summary>Cancels the loop. Dispose or cancel at the end of a test.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The window length this harness was built with.</summary>
    public TimeSpan Frame { get; }

    /// <summary>The coalescer under test, already running its loop.</summary>
    public ChangeCoalescer<TKey> Coalescer { get; }

    /// <summary>The consumer loop, parked at the empty-queue await.</summary>
    public Task Loop { get; }

    /// <summary>Builds a coalescer whose loop is already parked and ready to pump.</summary>
    /// <param name="frameLength">Window length; the coalescer's default when null.</param>
    public CoalescerHarness(TimeSpan? frameLength = null)
    {
        this.Frame = frameLength ?? ChangeCoalescer<TKey>.DefaultFrameLength;
        this.Coalescer = new ChangeCoalescer<TKey>(this.Frame, this.Time);
        this.Loop = this.Pump.Run(() => this.Coalescer.RunAsync(this.Cancellation.Token));
        this.Pump.RunUntilIdle();
    }

    /// <summary>One settle: pump to the window delay, let the frame elapse,
    /// pump the drain and delivery.</summary>
    public void Settle()
    {
        this.Pump.RunUntilIdle();
        this.Time.Advance(this.Frame);
        this.Pump.RunUntilIdle();
    }
}

/// <summary>
/// A single-threaded SynchronizationContext with an explicit pump: posted
/// work queues up and runs only inside <see cref="RunUntilIdle"/>, on the
/// test's own thread. Starting the consumer loop under this context is the
/// test-side twin of starting it on the UI thread — every await resumes
/// here, so a test interleaves postings, time, and deliveries
/// deterministically instead of sleeping and hoping.
/// </summary>
public sealed class DeterministicPump : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> work = new();

    /// <summary>Queues work instead of scheduling it; nothing runs until pumped.</summary>
    public override void Post(SendOrPostCallback d, object? state)
        => this.work.Enqueue((d, state));

    /// <summary>Runs work inline, matching the synchronous contract callers expect.</summary>
    public override void Send(SendOrPostCallback d, object? state)
        => d(state);

    /// <summary>Starts an async operation with this context current, so its
    /// awaits capture it and every continuation lands in the pump.</summary>
    public T Run<T>(Func<T> start)
    {
        var previous = Current;
        SetSynchronizationContext(this);

        try
        {
            return start();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>Runs queued work until nothing is left, on the calling thread.</summary>
    public void RunUntilIdle()
    {
        var previous = Current;
        SetSynchronizationContext(this);

        try
        {
            while (this.work.TryDequeue(out var item))
                item.Callback(item.State);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}

/// <summary>
/// A deterministic <see cref="TimeProvider"/>: time moves only when a test
/// advances it, and due timers fire inside <see cref="Advance"/> on the
/// test's thread. The injected clock the pattern note demands — time-based
/// code tested with sleeps is flaky by construction. Single-threaded on
/// purpose: postings, pumping, and advancing all happen on the test thread
/// (the pump serializes every continuation), so there is deliberately no
/// locking to reason about.
/// </summary>
public sealed class FakeTime : TimeProvider
{
    private readonly List<FakeTimer> timers = [];
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Timers currently scheduled to fire — the observable for
    /// "idle schedules nothing".</summary>
    public int ScheduledTimerCount => this.timers.Count(t => t.Due is not null);

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => this.now;

    /// <inheritdoc/>
    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        this.timers.Add(timer);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward and fires every timer that becomes due, on the
    /// calling thread. This is what replaces sleeping in a test.
    /// </summary>
    /// <param name="by">How far forward to move.</param>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        var target = this.now + by;

        // Re-scan per iteration rather than iterating the list: a firing
        // callback completes its Task.Delay, which disposes the timer and
        // mutates the list mid-advance.
        while (this.timers
                   .Where(t => t.Due is { } due && due <= target)
                   .OrderBy(t => t.Due)
                   .FirstOrDefault() is { } next)
        {
            this.now = next.Due!.Value;
            next.Fire();
        }

        this.now = target;
    }

    private void Remove(FakeTimer timer) => this.timers.Remove(timer);

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTime owner;
        private readonly TimerCallback callback;
        private readonly object? state;

        public FakeTimer(FakeTime owner, TimerCallback callback, object? state)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
        }

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                throw new NotSupportedException(
                    "Only one-shot timers are modelled; Task.Delay never needs more.");

            this.Due = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : this.owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            this.Due = null; // one-shot: deactivate before the callback runs
            this.callback(this.state);
        }

        public void Dispose()
        {
            this.Due = null;
            this.owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            this.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
