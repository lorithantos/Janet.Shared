namespace Janet.Shared.Tests;

using System.Windows;
using System.Windows.Threading;

/// <summary>
/// A UI thread for tests, and one <see cref="Application"/> to hang resources on.
///
/// <para>Both are process-wide singletons in WPF — a second <c>Application</c>
/// throws — so the thread is created once and kept pumping rather than started per
/// test. Nothing here shows a window: everything under test is resources, styles
/// and attached behaviour, all of which exist without a desktop.</para>
/// </summary>
internal static class Sta
{
    private static readonly object Gate = new();
    private static Dispatcher? ui;

    /// <summary>
    /// Runs <paramref name="work"/> on the shared STA thread and hands back its
    /// result. Exceptions cross back to the caller, so an assertion inside still
    /// fails the test that asked for it.
    /// </summary>
    internal static T Run<T>(Func<T> work) => Ui().Invoke(work);

    /// <summary>Runs <paramref name="work"/> on the shared STA thread.</summary>
    internal static void Run(Action work) => Ui().Invoke(work);

    private static Dispatcher Ui()
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
