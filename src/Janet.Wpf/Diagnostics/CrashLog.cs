namespace Janet.Wpf.Diagnostics;

using System.IO;

/// <summary>
/// Append-only record of exceptions that would otherwise vanish with the
/// process. "It crashed sometimes" is undiagnosable; a file naming the throw site
/// is not.
///
/// Best-effort by design: the log is a diagnostic courtesy, and a failure to
/// write it must never become the crash.
/// </summary>
public static class CrashLog
{
    private static string application = "App";

    /// <summary>
    /// Names the folder under LOCALAPPDATA that the log lives in. Call once at
    /// startup, before anything can throw; defaults to a generic name rather than
    /// refusing to work, because a log in a slightly wrong place still beats none.
    /// </summary>
    /// <param name="name">The application's folder name. Ignored if blank.</param>
    public static void UseApplicationName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) application = name;
    }

    /// <summary>
    /// Where the log lands: the application's folder under LOCALAPPDATA, file
    /// name <c>crash.log</c>.
    /// </summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        application,
        "crash.log");

    /// <summary>
    /// Appends one entry. Never throws: a failure to log must not become the
    /// crash it was trying to report.
    /// </summary>
    /// <param name="context">What was happening, in a few words.</param>
    /// <param name="exception">The throw, written in full including inner exceptions.</param>
    public static void Write(string context, Exception exception)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Swallowed on purpose: throwing from inside crash handling would
            // recurse into the very failure being reported.
        }
    }
}
