namespace Janet.Coalescing;

/// <summary>
/// The asynchronous sibling of <see cref="Derived{T}"/>, for a derived value
/// too expensive to compute on read: recomputation is an explicitly triggered
/// flight (<see cref="ComputeAsync"/>), and a generation counter decides at
/// publish time whether the flight still describes current inputs. The
/// counter generalizes the ReferenceEquals supersession check it replaces —
/// it works for inputs that are not reference-comparable, and it makes
/// "invalidated mid-flight" and "a newer flight started" the same mechanism.
/// <para>
/// The held value survives everything except <see cref="Clear"/>: an
/// invalidation marks it stale rather than discarding it, a failed or
/// cancelled flight leaves it in place, and a superseded flight publishes
/// nothing — grey out, don't vanish, as data rather than as a hand-rolled
/// flag per consumer.
/// </para>
/// </summary>
/// <remarks>
/// Single-threaded by contract, like every cell: all members belong to the
/// delivery thread — in the app, the UI thread — and the compute callback is
/// expected to move its heavy work off-thread itself (the session wraps the
/// engine in Task.Run) and resume back here.
/// </remarks>
public sealed class AsyncDerived<T> where T : class
{
    private readonly Action? changed;

    /// <summary>Advanced by anything that changes what "current" means:
    /// Invalidate, Clear, and each new flight. A flight publishes only if
    /// nothing advanced it since takeoff.</summary>
    private int generation;

    /// <summary>The most recent flight. Only the latest flight may resolve
    /// <see cref="Status"/> — an older one landing late touches nothing,
    /// because a newer one owns the cell.</summary>
    private int latestFlight;

    /// <param name="changed">
    /// Raised when the cell's observable state moves - a publish, or a transition
    /// into stale, computing or failed. Deliberately a bare callback rather than an
    /// event: the cell has exactly one owner, and an event invites the unsubscribe
    /// bug that outlives the owner.
    /// </param>
    public AsyncDerived(Action? changed = null) => this.changed = changed;

    /// <summary>The last published value; null before the first publish and
    /// after <see cref="Clear"/>. Stale, computing, and failed states all
    /// keep serving it.</summary>
    public T? Value { get; private set; }

    /// <summary>
    /// Where the cell stands. Independent of whether <see cref="Value"/> is
    /// populated: a Failed or Stale cell still serves the last good value, which is
    /// usually what a UI should show rather than blanking.
    /// </summary>
    public DerivedStatus Status { get; private set; } = DerivedStatus.Never;

    /// <summary>The exception from the last resolved flight, exactly while
    /// <see cref="Status"/> is <see cref="DerivedStatus.Failed"/>.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>True while no invalidation has landed since the last publish
    /// — the held value still describes current inputs. Distinct from
    /// <see cref="Status"/> because a flight in progress says nothing about
    /// whether the value it will replace had already gone stale.</summary>
    public bool ValueIsCurrent { get; private set; }

    /// <summary>
    /// Inputs changed: whatever is held no longer describes them, and any
    /// flight in progress must not publish. Notifies only when something
    /// observable moved — repeated invalidation while already stale is
    /// silent, which is what keeps consumers from rebuilding per edit.
    /// </summary>
    public void Invalidate()
    {
        this.generation++;

        if (this.ValueIsCurrent)
        {
            this.ValueIsCurrent = false;

            if (this.Status is DerivedStatus.Valid)
                this.Status = DerivedStatus.Stale;

            this.changed?.Invoke();
        }
        else if (this.Status is DerivedStatus.Failed)
        {
            // New inputs supersede the failure; the next flight decides.
            this.Failure = null;
            this.Status = this.Value is null ? DerivedStatus.Never : DerivedStatus.Stale;
            this.changed?.Invoke();
        }
    }

    /// <summary>Discards the held value entirely — for the document-level
    /// resets (open, restore) where even a stale value would lie.</summary>
    public void Clear()
    {
        this.generation++;

        if (this.Value is null && this.Status is DerivedStatus.Never)
            return;

        this.Value = null;
        this.Failure = null;
        this.ValueIsCurrent = false;
        this.Status = DerivedStatus.Never;
        this.changed?.Invoke();
    }

    /// <summary>
    /// Runs one flight and publishes its result if the cell's inputs did not
    /// move underneath it. Returns the published value, or null when the
    /// flight was superseded — by an invalidation, a clear, or a newer
    /// flight. Exceptions propagate to the caller after the cell's state is
    /// resolved: failure becomes <see cref="DerivedStatus.Failed"/>,
    /// cancellation restores the state the flight found.
    /// </summary>
    public async Task<T?> ComputeAsync(Func<Task<T>> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        int flight = ++this.generation;
        this.latestFlight = flight;
        this.Status = DerivedStatus.Computing;
        this.changed?.Invoke();

        T computed;

        try
        {
            computed = await compute();
        }
        catch (OperationCanceledException)
        {
            this.ResolveAbandoned(flight, failure: null);
            throw;
        }
        catch (Exception failure)
        {
            this.ResolveAbandoned(flight, this.generation == flight ? failure : null);
            throw;
        }

        if (this.latestFlight != flight)
            return null; // a newer flight owns the cell now

        if (this.generation != flight)
        {
            // Superseded mid-flight: the result describes inputs that no
            // longer exist. The held value (already marked stale by the
            // invalidation) stays the last truth.
            this.Status = this.Value is null ? DerivedStatus.Never : DerivedStatus.Stale;
            this.changed?.Invoke();
            return null;
        }

        this.Value = computed;
        this.Failure = null;
        this.ValueIsCurrent = true;
        this.Status = DerivedStatus.Valid;
        this.changed?.Invoke();
        return computed;
    }

    // A flight that ends without publishing — cancelled, failed, or
    // superseded — resolves the cell back to what the held value supports,
    // unless a newer flight has taken ownership meanwhile.
    private void ResolveAbandoned(int flight, Exception? failure)
    {
        if (this.latestFlight != flight)
            return;

        if (failure is not null)
        {
            this.Failure = failure;
            this.Status = DerivedStatus.Failed;
        }
        else
        {
            this.Status = this.Value is null
                ? DerivedStatus.Never
                : this.ValueIsCurrent ? DerivedStatus.Valid : DerivedStatus.Stale;
        }

        this.changed?.Invoke();
    }
}
