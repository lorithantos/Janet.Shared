namespace Janet.Wpf.Testing;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

/// <summary>
/// Realises a page template and reports what WPF only whispers.
/// <para>
/// A binding to a property that does not exist does not throw, does not fail the
/// build, and does not stop the page rendering — it writes a line to a trace
/// source and leaves an empty control. That is why binding typos are found by a
/// person opening a page, and it is the failure this exists to convert into a
/// test result.
/// </para>
/// </summary>
public static class PageTemplateHarness
{
    /// <summary>
    /// Builds the implicit template for <paramref name="viewModel"/>'s type,
    /// realises it inside a real window, and returns every binding failure WPF
    /// traced while doing so.
    /// </summary>
    /// <param name="viewModel">
    /// The object to bind against. Its runtime type is the template key, so an
    /// implicit <see cref="DataTemplate"/> for that exact type must be in
    /// <c>Application.Resources</c> — a <see cref="WpfApplicationFixture"/> is what
    /// normally puts it there.
    /// </param>
    /// <param name="inspect">
    /// Assertions needing the live tree — anything inside a <c>Popup</c>, whose
    /// child is torn down with the window that hosts its placement target.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// No application is running, or it has no implicit template for that type.
    /// Both are thrown by name rather than left to a cast, because a harness whose
    /// own setup fails silently is the defect it exists to catch, one level up.
    /// </exception>
    public static IReadOnlyList<string> Realise(
        object viewModel, Action<ContentControl>? inspect = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        Application app = Application.Current
            ?? throw new InvalidOperationException(
                "No Application is running, so there are no application resources to "
                + "resolve a template against. Realising needs a WpfApplicationFixture.");

        Type type = viewModel.GetType();

        if (app.Resources[new DataTemplateKey(type)] is not DataTemplate template)
        {
            throw new InvalidOperationException(
                $"No implicit DataTemplate for {type.FullName} in Application.Resources. "
                + "A template declared with an x:Key is not implicit — it needs a "
                + "DataType and no key — and a dictionary the fixture never merged "
                + "resolves nothing at all.");
        }

        return RealiseTemplate(template, viewModel, inspect);
    }

    /// <summary>
    /// The same realisation against a template the caller resolved, for a template
    /// that is not registered implicitly or not registered at all.
    /// </summary>
    public static IReadOnlyList<string> RealiseTemplate(
        DataTemplate template, object viewModel, Action<ContentControl>? inspect = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        var host = new ContentControl { ContentTemplate = template, Content = viewModel };

        using var listener = new BindingFailureListener();

        // A template is a factory; nothing is built, and so no StaticResource is
        // resolved and no binding attached, until it is measured in a
        // presentation source.
        var window = new Window { Content = host, Width = 1400, Height = 1000 };
        window.Show();

        try
        {
            host.UpdateLayout();
            Pump();
            inspect?.Invoke(host);
        }
        finally
        {
            window.Close();
        }

        return listener.Failures;
    }

    /// <summary>
    /// The same realisation for an element built by hand rather than from a
    /// template. Exists so the listener itself can be tested against a binding
    /// known to be broken — a detector nothing has ever seen fire is
    /// indistinguishable from one that cannot.
    /// </summary>
    public static IReadOnlyList<string> RealiseElement(
        FrameworkElement element, object dataContext)
    {
        ArgumentNullException.ThrowIfNull(element);

        using var listener = new BindingFailureListener();

        element.DataContext = dataContext;

        var window = new Window { Content = element, Width = 400, Height = 300 };
        window.Show();

        try
        {
            element.UpdateLayout();
            Pump();
        }
        finally
        {
            window.Close();
        }

        return listener.Failures;
    }

    /// <summary>Binding attachment rides the dispatcher queue, which a bare test
    /// thread never services; drain it so transfers complete like they would in
    /// the running app.</summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Every descendant of <paramref name="root"/> of type <typeparamref name="T"/>,
    /// in visual-tree order. The visual tree is what a realised template produces,
    /// so it is what assertions about rendered content have to walk.
    /// </summary>
    public static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        ArgumentNullException.ThrowIfNull(root);

        List<T> found = [];
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) found.Add(match);
            found.AddRange(Descendants<T>(child));
        }

        return found;
    }

    /// <summary>
    /// Captures WPF's data-binding trace for the life of one realisation.
    /// <para>
    /// The source's level has to be raised deliberately: at its default
    /// <see cref="SourceLevels.Off"/> — which is what a Release build and a test
    /// host both get — WPF does not even format the message, so there is nothing
    /// to listen to. Raising it and restoring it is the whole trick.
    /// </para>
    /// </summary>
    private sealed class BindingFailureListener : TraceListener
    {
        private readonly SourceLevels previousLevel;
        private readonly System.Text.StringBuilder pending = new();

        public BindingFailureListener()
        {
            // Without this the source exists but is inert: WPF builds its trace
            // sources lazily and Refresh is what actually wires them up. Skipping
            // it produces a listener that never fires, which reads exactly like a
            // page with no binding errors — the failure mode this class exists to
            // remove, reproduced one level up.
            PresentationTraceSources.Refresh();

            var source = PresentationTraceSources.DataBindingSource;

            this.previousLevel = source.Switch.Level;
            source.Switch.Level = SourceLevels.Warning;
            source.Listeners.Add(this);
        }

        public List<string> Failures { get; } = [];

        // WPF writes a message in fragments and terminates it with WriteLine, so
        // a listener that treats each Write as a message reports shrapnel.
        public override void Write(string? message) => this.pending.Append(message);

        public override void WriteLine(string? message)
        {
            this.pending.Append(message);

            string line = this.pending.ToString().Trim();
            this.pending.Clear();

            if (line.Length > 0)
                this.Failures.Add(line);
        }

        protected override void Dispose(bool disposing)
        {
            var source = PresentationTraceSources.DataBindingSource;

            source.Listeners.Remove(this);
            source.Switch.Level = this.previousLevel;

            base.Dispose(disposing);
        }
    }
}
