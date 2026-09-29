namespace Janet.Shared.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

/// <summary>
/// The dictionaries, and what they do to a real control.
///
/// <para>This is the failure class the compiler cannot see. A <c>StaticResource</c>
/// naming a style that is not there, a <c>BasedOn</c> pointing at a key that moved,
/// a brush defined in one skin and forgotten in the other — every one of them
/// builds green and fails at the window, or worse, fails silently: an unresolved
/// <c>DynamicResource</c> throws nothing and simply leaves the control with its
/// system default.</para>
///
/// <para>So these assert on what a control ENDS UP WITH, not on what a dictionary
/// says. A test that asks a style what it thinks it sets will happily confirm a
/// setter the control never applies.</para>
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ThemeDictionaryTests
{
    private const string Package = "pack://application:,,,/Janet.Wpf;component/Themes/";

    private readonly JanetWpfFixture wpf;

    public ThemeDictionaryTests(JanetWpfFixture wpf) => this.wpf = wpf;

    [Theory]
    [InlineData("Primitives.Light.xaml")]
    [InlineData("Primitives.Dark.xaml")]
    [InlineData("Semantic.Light.xaml")]
    [InlineData("Semantic.Dark.xaml")]
    [InlineData("Controls.xaml")]
    public void EveryDictionaryLoads(string file)
    {
        // Loading is the assertion. A StaticResource that cannot resolve throws
        // HERE — at BAML load — which in a real app means a window that never
        // appears rather than a build that fails.
        var dictionary = this.wpf.Run(() => new ResourceDictionary { Source = new Uri(Package + file) });

        Assert.NotEmpty(dictionary.Keys.Cast<object>());
    }

    /// <summary>
    /// A key in one skin and not the other is a control that loses its brush the
    /// moment somebody toggles the theme — and it shows up as black-on-black, at
    /// runtime, on whichever screen nobody opened during the change.
    /// </summary>
    [Fact]
    public void TheTwoSkinsDefineTheSameKeys()
    {
        var (light, dark) = this.wpf.Run(() => (KeysOf("Semantic.Light.xaml"), KeysOf("Semantic.Dark.xaml")));

        Assert.Equal([.. light.Order()], [.. dark.Order()]);
    }

    /// <summary>
    /// The style reaches the control, and its brushes resolve to the skin's own
    /// rather than to whatever WPF would have used. Asserted against the brush
    /// looked up from the dictionary, because "not null" would pass on the system
    /// default and prove nothing.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void QuietDataGridTakesItsColoursFromTheActiveSkin(string skin)
    {
        var (foreground, expected) = this.wpf.Run(() =>
        {
            UseTheme(skin);

            var grid = new DataGrid { Style = (Style)Application.Current.FindResource("QuietDataGrid") };
            Realise(grid);

            return ((SolidColorBrush)grid.Foreground,
                    (SolidColorBrush)Application.Current.FindResource("TextPrimaryBrush"));
        });

        Assert.Equal(expected.Color, foreground.Color);
    }

    /// <summary>
    /// Cells centre their content, so a row that has grown taller than one line
    /// still reads as one line.
    ///
    /// <para><b>Measured in pixels, and the reason is a scar.</b> The first two
    /// attempts at this in the consuming app were green against a visibly broken
    /// window. Comparing each TextBlock's CENTRE passed because an unaligned
    /// TextBlock stretches to the full row height, putting its centre mid-row
    /// exactly where a correctly centred one sits. Asserting on
    /// <c>DataGridCell.VerticalContentAlignment</c> passed because WPF's default
    /// cell template never template-binds it — the property read back as Center
    /// while nothing had moved at all.</para>
    ///
    /// <para>So: put something tall in one cell, and check where the text in the
    /// next cell actually starts.</para>
    /// </summary>
    [Fact]
    public void QuietDataGridCentresCellContentInATallRow()
    {
        var (textTop, rowHeight) = this.wpf.Run(() =>
        {
            UseTheme("Light");

            var grid = new DataGrid
            {
                Style = (Style)Application.Current.FindResource("QuietDataGrid"),
                ItemsSource = new[] { new { Name = "one line" } },
                AutoGenerateColumns = false
            };

            grid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding("Name") });
            grid.Columns.Add(new DataGridTemplateColumn { CellTemplate = TallCell() });

            Realise(grid);

            var row = Descendants<DataGridRow>(grid).Single();
            var text = Descendants<TextBlock>(row).First(t => t.Text == "one line");

            return (text.TransformToAncestor(row).Transform(new Point(0, 0)).Y, row.ActualHeight);
        });

        // The tall cell forces a row well past one line; the text must sit in the
        // middle of it rather than pinned to the top.
        Assert.True(rowHeight > 50, $"the fixture did not produce a tall row ({rowHeight:F0}px)");
        Assert.True(
            textTop > 10,
            $"cell text starts {textTop:F0}px from the top of a {rowHeight:F0}px row — it is not centred");
    }

    // ---- fixtures -----------------------------------------------------------

    private static DataTemplate TallCell()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.HeightProperty, 60d);
        border.SetValue(FrameworkElement.WidthProperty, 20d);

        return new DataTemplate { VisualTree = border };
    }

    private static IEnumerable<string> KeysOf(string file)
    {
        var dictionary = new ResourceDictionary { Source = new Uri(Package + file) };

        return All(dictionary).SelectMany(d => d.Keys.Cast<object>()).Select(k => k.ToString()!);

        static IEnumerable<ResourceDictionary> All(ResourceDictionary dictionary) =>
            new[] { dictionary }.Concat(dictionary.MergedDictionaries.SelectMany(All));
    }

    /// <summary>The merge an app performs, done here so DynamicResource has somewhere to land.</summary>
    private static void UseTheme(string skin)
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        resources.Clear();
        resources.Add(new ResourceDictionary { Source = new Uri($"{Package}Semantic.{skin}.xaml") });
        resources.Add(new ResourceDictionary { Source = new Uri(Package + "Controls.xaml") });
    }

    /// <summary>
    /// Lays the control out and generates its item containers. The first pass
    /// produces an empty grid — containers are generated at Loaded priority, which
    /// nothing has pumped yet — so the queue is drained and it is laid out again.
    /// </summary>
    private static void Realise(FrameworkElement element)
    {
        var host = new Border { Child = element, Width = 400, Height = 300 };

        host.Measure(new Size(400, 300));
        host.Arrange(new Rect(0, 0, 400, 300));
        host.UpdateLayout();

        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        host.Measure(new Size(400, 300));
        host.Arrange(new Rect(0, 0, 400, 300));
        host.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}

internal static class SortExtension
{
    internal static IEnumerable<string> Order(this IEnumerable<string> keys) =>
        keys.Distinct().OrderBy(k => k, StringComparer.Ordinal);
}
