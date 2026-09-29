namespace Janet.Wpf.Testing;

using System.Windows;
using System.Windows.Threading;

/// <summary>
/// A real <see cref="Application"/> with an app's resource dictionaries merged into
/// it, and a way to run test bodies on its thread.
///
/// <para>Nothing short of a real <c>Application</c> reproduces how a page resolves
/// its resources. A <c>StaticResource</c> in a template does not care whether the
/// dictionary that defines it sits on the templated <c>ContentControl</c>, on an
/// ancestor <c>Window</c>, or is merged into the page dictionary itself — the
/// resolution it actually uses is <c>Application.Resources</c>. An
/// <c>Application</c> is one per process and bound to the thread that created it,
/// which is why this is a fixture rather than a helper.</para>
///
/// <para>Subclass it with a parameterless constructor to use it as an xUnit
/// collection fixture, which is how the resource list reaches a type xUnit
/// constructs for you:</para>
///
/// <code>
/// public sealed class MyAppFixture() : WpfApplicationFixture(
///     [PackUri("MyApp", "Themes/Light.xaml"), PackUri("MyApp", "Views/Templates.xaml")]);
/// </code>
/// </summary>
public class WpfApplicationFixture
{
    private readonly Dispatcher dispatcher;

    /// <summary>
    /// Starts the host if it is not running and merges <paramref name="resources"/>
    /// in the order given — which matters, because a dictionary read through
    /// <c>DynamicResource</c> only must be merged before whatever resolves against it.
    /// </summary>
    /// <param name="resources">
    /// Dictionaries to merge into <c>Application.Resources</c>. A URI already merged
    /// by another fixture in this process is skipped rather than merged twice.
    /// </param>
    /// <param name="configure">
    /// Anything declared beside the merge rather than inside it — a converter
    /// instance under a key, say. Runs on the host thread after the merge, once per
    /// fixture rather than once per process.
    /// </param>
    public WpfApplicationFixture(
        IEnumerable<Uri>? resources = null, Action<Application>? configure = null)
        => this.dispatcher = WpfHost.Ensure(resources ?? [], configure);

    /// <summary>
    /// The pack URI for a resource compiled into <paramref name="assembly"/>, which
    /// is the addressing scheme a merged dictionary needs and the one people get
    /// wrong by hand.
    /// </summary>
    public static Uri PackUri(string assembly, string path)
        => new($"pack://application:,,,/{assembly};component/{path}", UriKind.Absolute);

    /// <summary>Runs <paramref name="work"/> on the application's thread.</summary>
    public void Run(Action work) => this.dispatcher.Invoke(work);

    /// <summary>
    /// Runs <paramref name="work"/> on the application's thread and hands back its
    /// result.
    /// </summary>
    public T Run<T>(Func<T> work) => this.dispatcher.Invoke(work);
}
