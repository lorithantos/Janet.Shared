# Janet.Wpf.Testing

Turns WPF's silent binding failures into test failures.

## The problem

A binding to a property that does not exist throws nothing. It does not fail the
build, it does not stop the page rendering, and it does not stop the app. WPF
writes one line to a trace source — `PresentationTraceSources.DataBindingSource`
— and carries on with an empty control.

So the app runs, the control renders, and the value is silently absent. That is
why binding typos are found by a person opening a page, weeks later, rather than
by anything that runs on every commit. The whole package exists to move that
discovery back to the build.

Worse, the trace source defaults to `SourceLevels.Off`, which is what a Release
build and a test host both get. At `Off`, WPF does not even format the message,
so a listener attached without raising the level is a detector that can never
fire. Raising the level, and putting it back afterwards, is the trick.

## The shape

```csharp
public sealed class MyAppFixture() : WpfApplicationFixture(
    [WpfApplicationFixture.PackUri("MyApp", "Themes/Light.xaml"),
     WpfApplicationFixture.PackUri("MyApp", "Views/Templates.xaml")]);

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection : ICollectionFixture<MyAppFixture>
{
    public const string Name = "wpf";
}

[Collection(WpfCollection.Name)]
public sealed class PageTests(MyAppFixture wpf)
{
    [Fact]
    public void TheOverviewPageBindsToEverythingItNames() =>
        wpf.Run(() => Assert.Empty(PageTemplateHarness.Realise(new OverviewViewModel())));
}
```

The `[CollectionDefinition]` has to live in the test assembly, because xUnit
only discovers collection definitions in the assembly under test. Nothing here
references a test framework, so the harness works under any runner.

## `WpfApplicationFixture` exists because a real `Application` is the only thing
that resolves resources the way a page does

A `StaticResource` in a template does not care whether the dictionary that
defines it sits on the templated `ContentControl`, on an ancestor `Window`, or
is merged into the page dictionary itself. The resolution it actually uses is
`Application.Resources`. Stand the template up against a hand-built
`ResourceDictionary` and every lookup that a real page would satisfy through the
application resolves against nothing — the test fails where the app works, which
is worse than no test.

An `Application` is one per process — a second one throws — and it is bound to
the thread that created it. That is why this is a fixture rather than a helper:
the thing it owns cannot be made twice, and cannot be handed between threads.

## The `Application` is never torn down

There is no `Dispose` here that shuts the host down, and that is deliberate.
The thread is a background thread and dies with the process; the `Application`
goes with it. A fixture that tore it down on its own disposal would take every
*other* collection's host with it, because there is only ever one — and the
collection that ran second would fail on a dispatcher that had already stopped
pumping, with an error naming nothing that went wrong in it.

Merging resource dictionaries is likewise not part of thread startup. Work done
on the host thread before the ready signal has nobody to throw to: a bad pack
URI there leaves an unhandled exception on a thread no test owns and a caller
blocked on a signal nothing will ever set. Dictionaries are merged through
`Dispatcher.Invoke` afterwards, where a failure comes back to whoever asked for
it, named.

## `RealiseElement`, and why the harness tests its own detector

`Realise` and `RealiseTemplate` stand a template up and report what WPF traced.
`RealiseElement` does the same for an element built by hand, and it exists for
exactly one reason: so the listener can be pointed at a binding that is *known*
to be broken.

**A detector nothing has ever seen fire is indistinguishable from one that
cannot.** A harness that returns an empty failure list because the trace level
was never raised, or because `PresentationTraceSources.Refresh()` was skipped
and the source was still inert, reads precisely like a page with no binding
errors: green, fast, and worthless. So the suite binds a `TextBlock` to
`ThisPropertyDoesNotExist`, asserts the failure comes back naming it, and binds
one to a property that exists and asserts nothing comes back. Without the second
half, a detector that reported failures unconditionally would pass the first.

That is the same defect the package exists to catch, one level up — which is why
it is checked rather than assumed.
