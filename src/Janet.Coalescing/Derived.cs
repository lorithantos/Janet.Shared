namespace Janet.Coalescing;

/// <summary>
/// Where a derived value stands relative to its inputs — one explicit status
/// instead of scattered booleans (<c>isStale</c>, <c>isRunning</c>), for the
/// same reason validation is data rather than prose: the states are a model,
/// not an accumulation of flags.
/// </summary>
public enum DerivedStatus
{
    /// <summary>Not computed yet: nothing has ever read the cell.</summary>
    Never,

    /// <summary>The held value describes previous inputs; the next read
    /// recomputes.</summary>
    Stale,

    /// <summary>An asynchronous recompute is in flight
    /// (<see cref="AsyncDerived{T}.ComputeAsync"/>). Synchronous cells are
    /// the degenerate case — they move Stale to Valid inside the getter and
    /// never occupy this state.</summary>
    Computing,

    /// <summary>The held value is current.</summary>
    Valid,

    /// <summary>The last recompute threw. <see cref="Derived{T}.Failure"/>
    /// carries the exception and the cell keeps serving the last good
    /// value — grey out, don't vanish.</summary>
    Failed,
}

/// <summary>
/// One bindable output as a lazy cell: a dirty flag, the cached value, and
/// the compute that refreshes it. <see cref="Invalidate"/> is the
/// coalescer-facing half — it marks the cell stale and raises the owner's
/// change notification, a no-op when no live binding exists. The
/// <see cref="Value"/> getter is the binding-facing half and rebuilds only
/// when something actually reads it, so an off-screen page computes nothing
/// at all, with no activation plumbing to maintain.
/// <para>
/// Compose one cell per output, each registered for exactly the keys that
/// dirty it — deliberately not a shared chart-page base class, which would
/// carry one dirty flag and one rebuild and thereby relocate today's
/// Recompute() rather than fix it (DESIGN-COALESCING.md section 3).
/// </para>
/// </summary>
/// <remarks>
/// Single-threaded by contract: reads and invalidation both belong to the
/// delivery thread — in the app, the UI thread. The getter is idempotent and
/// cheap when clean. Failure is a state, not a throw: a compute exception
/// lands in <see cref="Failure"/> instead of unwinding into whatever binding
/// or event cascade happened to trigger the read.
/// </remarks>
public sealed class Derived<T>
{
    private readonly Func<T> compute;
    private readonly Action? invalidated;
    private T value;

    /// <param name="initial">Served until the first successful compute — in
    /// particular when the first compute itself fails.</param>
    /// <param name="compute">Rebuilds the value from current input state.</param>
    /// <param name="invalidated">The owner's change notification, typically
    /// raising PropertyChanged for the property wrapping this cell.</param>
    public Derived(T initial, Func<T> compute, Action? invalidated = null)
    {
        ArgumentNullException.ThrowIfNull(compute);

        this.value = initial;
        this.compute = compute;
        this.invalidated = invalidated;
    }

    /// <summary>
    /// Where the cell stands. Failed is a STATE rather than a throw, so a
    /// computation that blows up leaves the last good value readable instead of
    /// propagating into whatever touched the property.
    /// </summary>
    public DerivedStatus Status { get; private set; } = DerivedStatus.Never;

    /// <summary>The exception from the last recompute, exactly while
    /// <see cref="Status"/> is <see cref="DerivedStatus.Failed"/>.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// The current value, recomputed on read exactly when stale. While
    /// Failed, the last good value keeps serving and reads do not retry —
    /// a failed compute is expensive to repeat per binding read, and only
    /// new input (another <see cref="Invalidate"/>) can change the outcome.
    /// </summary>
    public T Value
    {
        get
        {
            if (this.Status is DerivedStatus.Never or DerivedStatus.Stale)
            {
                try
                {
                    this.value = this.compute();
                    this.Status = DerivedStatus.Valid;
                    this.Failure = null;
                }
                catch (Exception failure)
                {
                    this.Status = DerivedStatus.Failed;
                    this.Failure = failure;
                }
            }

            return this.value;
        }
    }

    /// <summary>
    /// Marks the cell stale and notifies the owner. Notification fires only
    /// on the transition: while already Stale one is pending and unread, and
    /// while Never nothing has observed the cell — a binding that
    /// materializes later reads fresh anyway. Both skips are what keep an
    /// invisible page at exactly zero work.
    /// </summary>
    public void Invalidate()
    {
        if (this.Status is DerivedStatus.Never or DerivedStatus.Stale)
            return;

        this.Status = DerivedStatus.Stale;
        this.invalidated?.Invoke();
    }
}
