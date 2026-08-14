namespace Janet.Shared.Tests;

using Janet.Coalescing;
using Janet.Coalescing.Testing;
using Xunit;

/// <summary>
/// The coalescer's contract, pinned.
///
/// <para>Several comments in <c>ChangeCoalescer</c> say a behaviour is "pinned by
/// test" — the greedy drain, the fixed window. Until now none of them were: the
/// test project contained no tests at all. These are those tests, written from
/// what the source promises, so the promises fail loudly if someone refactors
/// past them.</para>
///
/// <para>No sleeping anywhere. Time is a <see cref="FakeTime"/> the test advances
/// by hand and delivery runs on a <see cref="DeterministicPump"/>, so "did it
/// deliver yet" is a question with an exact answer rather than a race.</para>
/// </summary>
public sealed class ChangeCoalescerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(100);

    /// <summary>A burst becomes one delivery carrying the whole burst.</summary>
    [Fact]
    public void ABurstOfPostingsBecomesOneDelivery()
    {
        var harness = Harness(out var deliveries, "rows");

        harness.Coalescer.Post("rows", 1);
        harness.Coalescer.Post("rows", 2);
        harness.Coalescer.Post("rows", 3);
        harness.Settle();

        var batch = Assert.Single(deliveries);
        Assert.Equal([1, 2, 3], batch.Select(p => p.Value));
    }

    /// <summary>
    /// Nothing is delivered before the window closes. Delivering eagerly and then
    /// again at the deadline would double every output pass.
    /// </summary>
    [Fact]
    public void NothingIsDeliveredBeforeTheFrameElapses()
    {
        var harness = Harness(out var deliveries, "rows");

        harness.Coalescer.Post("rows", 1);
        harness.Pump.RunUntilIdle();
        Assert.Empty(deliveries);

        harness.Time.Advance(Frame - TimeSpan.FromMilliseconds(1));
        harness.Pump.RunUntilIdle();
        Assert.Empty(deliveries);

        harness.Time.Advance(TimeSpan.FromMilliseconds(1));
        harness.Pump.RunUntilIdle();
        Assert.Single(deliveries);
    }

    /// <summary>
    /// THE FIXED WINDOW, which is the design decision most easily refactored away.
    ///
    /// <para>A posting mid-window joins the batch WITHOUT moving the deadline. A
    /// debounce would push it, and sustained input would then defer delivery
    /// forever — the failure this shape exists to avoid. Worst-case latency stays
    /// one window.</para>
    /// </summary>
    [Fact]
    public void AMidWindowPostingJoinsTheBatchWithoutPushingTheDeadline()
    {
        var harness = Harness(out var deliveries, "rows");

        harness.Coalescer.Post("rows", 1);
        harness.Pump.RunUntilIdle();

        harness.Time.Advance(TimeSpan.FromMilliseconds(60));
        harness.Coalescer.Post("rows", 2);
        harness.Pump.RunUntilIdle();

        // 100ms total from the FIRST posting: a debounce would still be waiting.
        harness.Time.Advance(TimeSpan.FromMilliseconds(40));
        harness.Pump.RunUntilIdle();

        var batch = Assert.Single(deliveries);
        Assert.Equal([1, 2], batch.Select(p => p.Value));
    }

    /// <summary>
    /// Sustained input drains every frame rather than deferring. The cadence is
    /// set by the window, not by how fast anyone posts.
    /// </summary>
    [Fact]
    public void SustainedInputDeliversOncePerFrame()
    {
        var harness = Harness(out var deliveries, "rows");

        for (var round = 0; round < 3; round++)
        {
            harness.Coalescer.Post("rows", round);
            harness.Settle();
        }

        Assert.Equal(3, deliveries.Count);
        Assert.All(deliveries, batch => Assert.Single(batch));
    }

    /// <summary>
    /// A posting made from inside a delivery lands in the NEXT frame, not this
    /// one. Delivery is not re-entered, so a destination that posts cannot drive
    /// the loop round again without the window reopening.
    /// </summary>
    [Fact]
    public void APostingMadeDuringDeliveryLandsInTheNextFrame()
    {
        var harness = new CoalescerHarness<string>(Frame);
        var deliveries = new List<int>();
        var posted = false;

        harness.Coalescer.Register(["rows"], batch =>
        {
            deliveries.Add(batch.Count);

            if (posted) return;
            posted = true;
            harness.Coalescer.Post("rows", 99);
        });

        harness.Coalescer.Post("rows", 1);
        harness.Settle();
        Assert.Equal([1], deliveries);

        harness.Settle();
        Assert.Equal([1, 1], deliveries);
    }

    /// <summary>
    /// The queue buffers, it never merges. A destination folding its batch is
    /// entitled to every posting in arrival order — averaging, first-and-last and
    /// max are all legal collapses, and none of them work on a pre-merged list.
    /// </summary>
    [Fact]
    public void PostingsArriveIntactAndInOrder()
    {
        var harness = Harness(out var deliveries, "rows");

        harness.Coalescer.Post("rows", "a");
        harness.Coalescer.Post("rows", "b");
        harness.Coalescer.Post("rows", "a");
        harness.Settle();

        Assert.Equal(["a", "b", "a"], Assert.Single(deliveries).Select(p => p.Value));
    }

    /// <summary>A destination sees its own keys and nothing else.</summary>
    [Fact]
    public void EachDestinationReceivesOnlyItsOwnKeys()
    {
        var harness = new CoalescerHarness<string>(Frame);
        List<Posting<string>> rows = [], jobs = [];

        harness.Coalescer.Register(["rows"], batch => rows.AddRange(batch));
        harness.Coalescer.Register(["jobs"], batch => jobs.AddRange(batch));

        harness.Coalescer.Post("rows", 1);
        harness.Coalescer.Post("jobs", 2);
        harness.Coalescer.Post("theme", 3);
        harness.Settle();

        Assert.Equal([1], rows.Select(p => p.Value));
        Assert.Equal([2], jobs.Select(p => p.Value));
    }

    /// <summary>
    /// A destination with nothing under its keys is not called at all — not called
    /// with an empty list. An invalidation handler that runs on every frame
    /// regardless is a repaint nobody asked for.
    /// </summary>
    [Fact]
    public void ADestinationWithNothingToSayIsNotCalled()
    {
        var harness = new CoalescerHarness<string>(Frame);
        var calls = 0;

        harness.Coalescer.Register(["rows"], _ => calls++);

        harness.Coalescer.Post("jobs", 1);
        harness.Settle();

        Assert.Equal(0, calls);
    }

    /// <summary>A posting nobody registered for is drained and dropped, quietly.</summary>
    [Fact]
    public void APostingWithNoDestinationIsDropped()
    {
        var harness = new CoalescerHarness<string>(Frame);

        harness.Coalescer.Post("nobody-listens", 1);
        harness.Settle();

        Assert.False(harness.Loop.IsFaulted);
    }

    /// <summary>
    /// Idle costs nothing. The loop waits on the queue rather than ticking, so an
    /// app sitting still schedules no timers at all — the reason there is no
    /// heartbeat.
    /// </summary>
    [Fact]
    public void AnIdleCoalescerSchedulesNothing()
    {
        var harness = new CoalescerHarness<string>(Frame);

        Assert.Equal(0, harness.Time.ScheduledTimerCount);

        harness.Coalescer.Post("rows", 1);
        harness.Settle();

        Assert.Equal(0, harness.Time.ScheduledTimerCount);
    }

    /// <summary>
    /// A destination that throws faults the loop rather than being swallowed.
    /// Delivery IS invalidation: swallowing here turns broken wiring into a UI
    /// that quietly stops updating, which is the hardest kind of bug to notice.
    /// </summary>
    [Fact]
    public void ADestinationThatThrowsFaultsTheLoop()
    {
        var harness = new CoalescerHarness<string>(Frame);
        harness.Coalescer.Register(["rows"], _ => throw new InvalidOperationException("wiring"));

        harness.Coalescer.Post("rows", 1);

        try { harness.Settle(); }
        catch (InvalidOperationException) { /* may surface through the pump instead */ }

        Assert.True(
            harness.Loop.IsFaulted || harness.Loop.IsCompleted,
            "the loop kept running after a destination threw");
    }

    /// <summary>
    /// One loop per coalescer. A second would give two threads' worth of delivery
    /// against a design whose whole thread-affinity story is "egress is single
    /// threaded".
    ///
    /// <para>Awaited, not just called: <c>RunAsync</c> is an async method, so even
    /// its guard clause — which runs before the first await — arrives as a faulted
    /// Task rather than a synchronous throw.</para>
    /// </summary>
    [Fact]
    public async Task TheLoopRefusesToRunTwice()
    {
        var harness = new CoalescerHarness<string>(Frame);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Coalescer.RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AFrameLengthThatIsNotPositiveIsRefused(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChangeCoalescer<string>(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void ADestinationMustRegisterAtLeastOneKey()
    {
        var harness = new CoalescerHarness<string>(Frame);

        Assert.Throws<ArgumentException>(() => harness.Coalescer.Register([], _ => { }));
    }

    // ---- fixtures -----------------------------------------------------------

    private static CoalescerHarness<string> Harness(
        out List<IReadOnlyList<Posting<string>>> deliveries, params string[] keys)
    {
        var harness = new CoalescerHarness<string>(Frame);
        var captured = new List<IReadOnlyList<Posting<string>>>();

        harness.Coalescer.Register(keys, batch => captured.Add([.. batch]));
        deliveries = captured;
        return harness;
    }
}

