# Janet.Shared

Small libraries extracted from apps that had already built them twice, or were about to.

| Package | What |
|---|---|
| **Janet.Coalescing** | Collapse bursts of change notifications into output passes at a bounded cadence, plus lazy derived cells that compute only when read. No UI dependency. |
| **Janet.Wpf** | Three-layer theming with runtime skin swapping, a DataGrid that remembers the user's sort, crash logging, and the startup hardening that stops WPF exiting silently. |
| **Janet.DependencyInjection** | Declare registration on the class itself: what to register, as what, with which lifetime. One assembly scan turns the attributes into descriptors, and a declaration that cannot be honoured throws at startup. |
| **Janet.Azure.Storage** | Blob, table and queue clients built from a credential the app chooses, with retry budgets tighter than the SDK's, and no way in for a key or a SAS token. |
| **Janet.Entra** | Reads who a request is from out of an Entra ID sign-in (object ID, tenant, groups, app roles) and fails closed when the sign-in does not say enough. |

All MIT. All ship XML docs, because the reasoning is the valuable part —
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

## Janet.DependencyInjection

### The problem

A composition root is a list of registrations kept somewhere other than the
classes it registers. Adding a service means editing two files, and forgetting
the second one fails at the first resolve rather than at the build — so the
error arrives far from the edit that caused it, wearing the name of whoever
asked for the service.

### The shape

```csharp
[RegisterDependency<IThumbnailer>(Lifetime = ServiceLifetime.Singleton)]
class Thumbnailer : IThumbnailer { }

services.AddDependenciesFromAttributes(typeof(Thumbnailer).Assembly);
```

Call the scan once per assembly that carries the attributes — an application
whose services live in a library of their own calls it twice.

### One class, several interfaces, one instance

`[RegisterDependency<T>]` is repeatable, and that is how one implementation
serves several interfaces. The first attribute registers the class; every later
one registers a descriptor that **forwards** to it. All of them resolve to the
*same* object, which is the point — registering the implementation once per
interface gives you one instance each, and the difference only shows up when
state written through one face fails to appear through another.

Every attribute on a class must name the same lifetime. Registration throws
rather than pick one, because forwarding descriptors only make sense when all of
them share an instance.

### `[RegisterFactory<TFactory, TCreates>]`

For the product the container cannot build on its own: when construction means
composing several registered services into a particular arrangement rather than
handing them to a constructor. It registers two singletons — the factory as
`TFactory`, and `TCreates` resolved by calling `Create()` on it. Consumers
inject the product and never name the factory.

A product type has **exactly one owner**. A second registration of it is a
contradiction rather than a variant, so it throws: both descriptors would land
and the container would serve whichever came last, in an order decided by
`Assembly.GetTypes()` and therefore by nothing the author controls.

`Create` is a compiler-bound call, not a method found by name. The earlier shape
resolved it reflectively, which meant renaming it compiled cleanly and failed
only when the container was first asked for the product — and failed outright
for a factory implementing the interface explicitly, because the method is then
private and name-mangled.

### `[RegisterHostedService]`

Registers the class additionally as `IHostedService`, forwarding to its primary
registration so the object the host starts is the object consumers inject rather
than a second one of the same type.

**The accompanying `[RegisterDependency]` must say `Singleton`, and registration
throws otherwise.** The host resolves hosted services from the root provider, so
there is nothing else to forward to: scoped resolves from the root — a captive
dependency, and a throw under scope validation — and transient would hand the
host an instance of its own, which is the one thing the forwarding exists to
prevent. `Lifetime` defaults to `Scoped`, so this is a lifetime you write out:

```csharp
[RegisterDependency<IPump>(Lifetime = ServiceLifetime.Singleton), RegisterHostedService]
class Pump : IPump, IHostedService { }
```

The check is at registration because the alternative is not a check at all.
Left to run, the mistake surfaces at `Host.StartAsync` — far from the
declaration that caused it — and with `ValidateScopes` off it surfaces nowhere,
as a second instance quietly doing the work nobody can see.

### The passes run in an order, and it matters

The scan runs dependencies, then hosted services, then factories. The ordering
is load-bearing in opposite directions: the hosted-service pass forwards to a
registration the dependency pass must already have made, while the factory pass
refuses a product type that any earlier pass registered. Both checks run *while
registering*, so a declaration that cannot be honoured throws during startup.
Only the factory's product is built lazily, and by then its declaration has
already been validated.

## Janet.Azure.Storage

ImageSelector and the Neelam campaign tool each built their storage clients by hand, and
only one of them gave the clients a retry budget: the other ran on the SDK's 100-second
per-attempt timeout. `StorageClients` builds them once, from the endpoints and the
`TokenCredential` the app passes in, applying `StorageRetryOptions`, and checks every
endpoint is plain https with no query string, so a SAS token has nowhere to travel.

It never chooses the credential. One app allows a developer's Visual Studio sign-in and
another forbids it; one pins a tenant, another does not. That is each app's policy, so it
stays in the app. See `src/Janet.Azure.Storage/README.md`.

## Janet.Entra

`EntraClaims.Read(principal)` gives an `EntraUser` (object ID, tenant, groups, app roles) or
the reason there is none. Both claim spellings the Microsoft stacks use are read, and a
group overage, where the token points at Graph instead of listing groups, gives no user
rather than a partial list. What a group or role *means* is the app's business; this only
says what the sign-in states. See `src/Janet.Entra/README.md`.

## Testing

`Janet.Shared.Tests` ships a deterministic harness — a `SynchronizationContext`
you pump explicitly and a `TimeProvider` you advance by hand. There is no
`Thread.Sleep` anywhere in the suite, and no test depends on wall-clock timing.

```csharp
var harness = new CoalescerHarness<MyKey>();
harness.Coalescer.Post(MyKey.Thing, 1);
harness.Settle();          // pump, advance one frame, pump the drain
```

`Janet.DependencyInjection.Fixture` is a separate assembly for one reason:
`AddDependenciesFromAttributes` scans a whole assembly, so pinning it needs an
assembly whose every declaration is meant to succeed. The deliberately-broken
types the unit tests need would be scanned too. The unit tests drive the three
passes by hand, one type at a time; the fixture is what proves a single scan
runs them in an order where each pass has what it needs.

## Building

```powershell
dotnet build Janet.Shared.slnx -c Release
dotnet pack  Janet.Shared.slnx -c Release -o ..\.packages
```

Consumers reference the packages from a local feed (`nuget.config` pointing at
`..\.packages`), which means a shared-API change requires a deliberate pack and
version bump rather than silently rippling into every consumer on the next build.
