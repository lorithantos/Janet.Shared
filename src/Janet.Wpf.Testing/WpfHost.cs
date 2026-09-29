namespace Janet.Wpf.Testing;

using System.Windows;
using System.Windows.Threading;

/// <summary>
/// The one <see cref="Application"/> a test process is allowed, and the STA thread
/// it is bound to.
///
/// <para>Both are process-wide singletons in WPF — a second <c>Application</c>
/// throws — so this is static however many fixtures ask for it. Nothing is ever
/// shut down: the thread is a background thread and dies with the process, and a
/// fixture that tore it down would take every other collection's host with it.</para>
///
/// <para>Startup does the least it possibly can. Merging resource dictionaries is
/// deliberately NOT part of it, because work done on the host thread before the
/// ready signal has nobody to throw to: a bad pack URI there leaves an unhandled
/// exception on a thread no test owns and a caller waiting on a signal that is
/// never set. Dictionaries are merged through <see cref="Dispatcher.Invoke(Action)"/>
/// afterwards, where a failure comes back to whoever asked for it.</para>
/// </summary>
internal static class WpfHost
{
    private static readonly object Gate = new();
    private static readonly HashSet<Uri> Merged = [];
    private static Dispatcher? ui;

    /// <summary>
    /// Returns the host dispatcher, starting it on first call, and merges any of
    /// <paramref name="resources"/> this process has not merged already.
    /// </summary>
    internal static Dispatcher Ensure(IEnumerable<Uri> resources, Action<Application>? configure)
    {
        Dispatcher dispatcher = Start();

        // On the host thread: a ResourceDictionary belongs to the thread that built
        // it, and Application.Resources is no different.
        dispatcher.Invoke(() =>
        {
            var app = Application.Current;

            foreach (Uri source in resources)
            {
                // Merged is only ever touched under Invoke on the single host
                // thread, so the set needs no lock of its own.
                if (!Merged.Add(source)) continue;

                try
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
                }
                catch (Exception ex)
                {
                    // Undo the claim, or a retry after a typo is silently skipped.
                    Merged.Remove(source);

                    // The inner exception is where a missing StaticResource is
                    // actually named; the outer says which dictionary was loading.
                    throw new InvalidOperationException(
                        $"Resource dictionary '{source}' could not be loaded: {ex.Message}", ex);
                }
            }

            configure?.Invoke(app);
        });

        return dispatcher;
    }

    private static Dispatcher Start()
    {
        lock (Gate)
        {
            if (ui is not null) return ui;

            using var ready = new ManualResetEventSlim();
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                // Anything thrown here would otherwise be unhandled on a thread no
                // test owns, which takes the whole run down with a stack trace
                // instead of failing one test. Carried back to the caller instead.
                try
                {
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    ui = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    ready.Set();
                    return;
                }

                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "janet-wpf-tests"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (failure is not null)
                throw new InvalidOperationException("the WPF host thread could not start", failure);

            return ui!;
        }
    }
}
