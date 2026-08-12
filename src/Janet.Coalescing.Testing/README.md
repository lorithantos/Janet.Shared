# Janet.Shared

Two small libraries extracted from apps that had already built them twice.

| Package | What |
|---|---|
| **Janet.Coalescing** | Collapse bursts of change notifications into output passes at a bounded cadence, plus lazy derived cells that compute only when read. No UI dependency. |
| **Janet.Wpf** | Three-layer theming with runtime skin swapping, a DataGrid that remembers the user's sort, crash logging, and the startup hardening that stops WPF exiting silently. |

Both are MIT. Both ship XML docs, because the reasoning is the valuable part —
*why* the frame is fixed rather than a debounce, *why* two counters instead of
one — and a consumer who cannot see that will reimplement the bug the comment
exists to prevent.

## Janet.Coalescing

### The problem

A viewmodel adopts a document and writes nine property notifications. Three
subscribers each rebuild everything on each one. Twenty-seven rebuilds per
keystroke, each allocating fresh brushes. The app is not slow because any one
piece is slow; it is slow because *n* changes fan out into *n × m* work.

Debouncing is the usual answer and the wrong one: under sustained input a
debounce defers forever, so the display freezes exactly when the user is most
actively looking at it.

### The shape

```csharp
var changes = new ChangeCoalescer<MyKey>(ChangeCoalescer<MyKey>.DefaultFrameLength);
_ = changes.RunAsync(CancellationToken.None);   // start on the UI thread

changes.Register([MyKey.Progress], batch =>
{
    // The whole batch, in arrival order. Folding is YOUR business:
    // Max here, because parallel workers post out of order and "latest"
    // can run backwards.
    this.Done = batch.Max(p => (int)p.Value!);
});

changes.Post(MyKey.Progress, 4200);   // any thread, lock-free, never blocks
```

**The window is fixed, not a debounce.** The first posting into an empty queue
opens it; one frame later everything drains. A posting arriving mid-window joins
that batch without pushing the deadline — so sustained input drains every frame
and worst-case latency is one window.

**A key answers only "may these be combined at all."** It never carries a merge
function, because two destinations may legitimately fold the same key
differently — one taking the max, another the last value.

**Concurrent ingress, single-threaded egress.** `Post` is safe from any thread.
`Register` and delivery belong to the thread running `RunAsync`; start that loop
on the UI thread and thread affinity is handled with no marshalling anywhere.
Never add `ConfigureAwait(false)` to it.

### Do not use `Progress<T>` for the hot path

`Progress<T>` exists to marshal *every* report to the captured context. A job
reporting ten times a second across four workers is four thousand dispatcher
operations a minute, and the render pump starves. Post to the bus instead and
fold once per frame.

### `Derived<T>` and `AsyncDerived<T>`

Lazy cells. `Derived<T>` recomputes on read only when stale, so an off-screen
page computes nothing. `AsyncDerived<T>` does the same for work that takes long
enough to need a status, keeping the last good value through staleness, failure
and cancellation — failure is a *state*, not a throw.

`AsyncDerived<T>` keeps **two** counters, and the distinction is the subtle part:
one decides whether a flight may *publish*, the other who may *resolve status*.
With one counter, an old flight landing late clobbers state a newer flight owns.

## Janet.Wpf

### Theming, in three layers

1. **Primitives** — raw colours (`Neutral0`…`Neutral900`, `Accent500`)
2. **Semantic** — the only vocabulary controls may use (`SurfaceBrush`, `TextPrimaryBrush`, `DividerBrush`)
3. **Controls** — named styles referencing semantic tokens **exclusively via `DynamicResource`**

Slot 0 of the app's merged dictionaries is the skin; swapping it re-themes
everything live, because `DynamicResource` re-resolves. A control that reaches
past semantic tokens to a primitive is the one thing that breaks this.

### `GridSortMemory`

A `DataGrid` loses the user's sort whenever `ItemsSource` is replaced wholesale —
which is exactly what a list republished from a lazy cell does every time.
Attached property, snapshots the sort, reapplies it after the swap. Needs
`NotifyOnTargetUpdated=True` on the `ItemsSource` binding.

### `CrashHardening.Install(...)`

WPF's default for an unhandled dispatcher exception is **silent process exit** —
the failure users describe as "it just closed". One call installs handlers for
dispatcher exceptions, unobserved task exceptions, and non-dispatcher throws.

`continueOnDispatcherException` has no correct default: continuing is right when
a render-pass throw should cost one interaction, and wrong when the app holds
unsaved state it cannot trust afterwards. The caller decides.

`CrashHardening.WatchLoop(loop, "Live updates")` guards a long-lived background
loop, because a faulted coalescer loop otherwise means the window silently stops
refreshing while everything looks fine.

## Testing

`Janet.Shared.Tests` ships a deterministic harness — a `SynchronizationContext`
you pump explicitly and a `TimeProvider` you advance by hand. There is no
`Thread.Sleep` anywhere in the suite, and no test depends on wall-clock timing.

```csharp
var harness = new CoalescerHarness<MyKey>();
harness.Coalescer.Post(MyKey.Thing, 1);
harness.Settle();          // pump, advance one frame, pump the drain
```

## Building

```powershell
dotnet build Janet.Shared.slnx -c Release
dotnet pack  Janet.Shared.slnx -c Release -o ..\.packages
```

Consumers reference the packages from a local feed (`nuget.config` pointing at
`..\.packages`), which means a shared-API change requires a deliberate pack and
version bump rather than silently rippling into every consumer on the next build.
