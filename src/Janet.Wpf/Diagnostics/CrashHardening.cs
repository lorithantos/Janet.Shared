namespace Janet.Wpf.Diagnostics;

using System.Windows;
using System.Windows.Threading;

/// <summary>
/// The four places a WPF app can die without saying anything, wired in one call.
///
/// WPF's default for an unhandled dispatcher exception is <b>silent process
/// exit</b> — the failure mode users describe as "it just closed". That default
/// is the reason this class exists: an app that logs and continues turns an
/// unexplained disappearance into a message and a stack trace.
/// </summary>
public static class CrashHardening
{
    /// <summary>
    /// Installs handlers for dispatcher exceptions, unobserved task exceptions,
    /// and non-dispatcher unhandled exceptions.
    /// </summary>
    /// <param name="application">Normally <c>this</c> from <c>OnStartup</c>.</param>
    /// <param name="applicationName">Folder under %LOCALAPPDATA% for the log.</param>
    /// <param name="continueOnDispatcherException">
    /// True marks dispatcher exceptions handled and keeps the app alive. Right
    /// when a throw from a render pass should cost one interaction rather than
    /// the session — but <b>wrong for an app holding unsaved state it cannot
    /// trust after an unexpected throw</b>, where exiting loudly is safer than
    /// continuing into an unknown state. The caller has to decide; there is no
    /// default that is right for both.
    /// </param>
    public static void Install(
        Application application,
        string applicationName,
        bool continueOnDispatcherException = true)
    {
        ArgumentNullException.ThrowIfNull(application);
        CrashLog.UseApplicationName(applicationName);

        application.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write("Dispatcher unhandled exception", e.Exception);
            e.Handled = continueOnDispatcherException;

            MessageBox.Show(
                $"{e.Exception.Message}\n\nDetails were appended to:\n{CrashLog.FilePath}",
                "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        // Non-dispatcher threads cannot be saved — the runtime tears the process
        // down regardless — but the log turns "it exited" into a stack trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(
                "Unhandled exception off the dispatcher (process exiting)",
                e.ExceptionObject as Exception
                    ?? new InvalidOperationException($"{e.ExceptionObject}"));
    }

    /// <summary>
    /// Watches a long-lived background loop and complains loudly if it faults.
    ///
    /// A faulted coalescer loop is the worst failure mode a coalescing app has:
    /// deliveries stop, so the window silently stops refreshing while everything
    /// looks alive and the underlying work carries on. Must be called from the
    /// thread that owns the UI, since the continuation shows a dialog.
    /// </summary>
    public static void WatchLoop(Task loop, string whatStopped)
    {
        ArgumentNullException.ThrowIfNull(loop);

        _ = loop.ContinueWith(
            completed =>
            {
                CrashLog.Write($"{whatStopped} faulted", completed.Exception!);
                MessageBox.Show(
                    $"{whatStopped} has stopped. Work already running is unaffected, but this " +
                    $"window will no longer refresh.\n\nDetails were appended to:\n{CrashLog.FilePath}",
                    "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.FromCurrentSynchronizationContext());
    }
}
