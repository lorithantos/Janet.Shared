namespace Janet.Shared.Tests;

using System.Windows;
using Janet.Wpf.Theming;
using Xunit;

/// <summary>
/// Skin swapping, which is the one piece of theming that is code.
///
/// <para>Its state is deliberately static and process-wide, so these run in the
/// serialised WPF collection and each one puts the service back the way it found
/// it. A theme test that leaks a dark skin makes an unrelated style test fail
/// somewhere else entirely.</para>
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ThemeServiceTests
{
    private readonly JanetWpfFixture wpf;

    public ThemeServiceTests(JanetWpfFixture wpf) => this.wpf = wpf;

    /// <summary>
    /// Swapping the skin replaces slot 0 and leaves everything the app merged
    /// behind it alone — it does not append a second skin, and it does not take
    /// the app's own dictionaries with it.
    ///
    /// <para>Slot 0 is the skin's, and an app's dictionaries go after it: a skin
    /// brings its own primitives, so it has to be merged before anything that
    /// resolves against them. Putting an app dictionary in slot 0 means losing it
    /// on the first swap, which is the arrangement this asserts against.</para>
    /// </summary>
    [Fact]
    public void SwappingTheSkinReplacesSlotZeroAndSparesWhatIsBehindIt()
    {
        this.wpf.Run(() =>
        {
            Restore(() =>
            {
                var resources = Application.Current.Resources.MergedDictionaries;
                resources.Clear();

                ThemeService.ApplySkin(ThemeService.Light);
                resources.Add(Marker());
                Assert.Equal(2, resources.Count);

                ThemeService.ApplySkin(ThemeService.Dark);

                Assert.Equal(2, resources.Count);
                Assert.Equal(ThemeService.Dark, ThemeService.CurrentSkin);
                Assert.Contains(resources, d => d.Contains("JanetTestMarker"));

                // And the swap reached the resources rather than only the property.
                Assert.NotNull(Application.Current.TryFindResource("TextPrimaryBrush"));
            });
        });
    }

    /// <summary>
    /// An app whose App.xaml merged nothing still gets a skin. Assigning past the
    /// end of the collection would throw an ArgumentOutOfRangeException the caller
    /// would have to read this source to understand.
    /// </summary>
    [Fact]
    public void ASkinCanBeAppliedWhenNothingHasBeenMergedYet()
    {
        this.wpf.Run(() =>
        {
            Restore(() =>
            {
                Application.Current.Resources.MergedDictionaries.Clear();

                ThemeService.ApplySkin(ThemeService.Light);

                Assert.Single(Application.Current.Resources.MergedDictionaries);
                Assert.NotNull(Application.Current.TryFindResource("TextPrimaryBrush"));
            });
        });
    }

    /// <summary>Toggling alternates, and says which it landed on.</summary>
    [Fact]
    public void TogglingAlternatesBetweenTheTwoSkins()
    {
        this.wpf.Run(() =>
        {
            Restore(() =>
            {
                ThemeService.ApplySkin(ThemeService.Light);

                ThemeService.ToggleSkin();
                Assert.Equal(ThemeService.Dark, ThemeService.CurrentSkin);

                ThemeService.ToggleSkin();
                Assert.Equal(ThemeService.Light, ThemeService.CurrentSkin);
            });
        });
    }

    /// <summary>
    /// The skins are different, and the swap actually reaches the resources. Two
    /// skins that resolve to the same brush would satisfy every other assertion
    /// here while doing nothing a user could see.
    /// </summary>
    [Fact]
    public void TheSkinsResolveToDifferentColours()
    {
        var (light, dark) = this.wpf.Run(() => Restore(() =>
        {
            ThemeService.ApplySkin(ThemeService.Light);
            var inLight = Application.Current.FindResource("SurfaceBrush").ToString();

            ThemeService.ApplySkin(ThemeService.Dark);
            return (inLight, Application.Current.FindResource("SurfaceBrush").ToString());
        }));

        Assert.NotEqual(light, dark);
    }

    /// <summary>
    /// A resolver handed in after startup is not silently ignored — the current
    /// skin is re-applied through it.
    /// </summary>
    [Fact]
    public void SupplyingAResolverReappliesTheCurrentSkinThroughIt()
    {
        var asked = this.wpf.Run(() => Restore(() =>
        {
            var seen = new List<string>();

            ThemeService.ApplySkin(ThemeService.Dark);
            ThemeService.UseSkinSource(skin =>
            {
                seen.Add(skin);
                return new Uri(
                    $"pack://application:,,,/Janet.Wpf;component/Themes/Semantic.{skin}.xaml");
            });

            return seen;
        }));

        Assert.Equal([ThemeService.Dark], asked);
    }

    [Fact]
    public void AnEmptySkinNameIsRefused() =>
        this.wpf.Run(() => Assert.Throws<ArgumentException>(() => ThemeService.ApplySkin("  ")));

    // ---- housekeeping -------------------------------------------------------

    /// <summary>
    /// Runs the body and puts the process-wide theme state back, whatever happens.
    /// The service has no "reset", by design — so the tests own the cleanup.
    /// </summary>
    private static T Restore<T>(Func<T> body)
    {
        var before = Application.Current.Resources.MergedDictionaries.ToList();

        try
        {
            return body();
        }
        finally
        {
            ThemeService.UseSkinSource(DefaultSource);
            var resources = Application.Current.Resources.MergedDictionaries;
            resources.Clear();
            foreach (var dictionary in before) resources.Add(dictionary);
            ThemeService.ApplySkin(ThemeService.Light);
        }
    }

    private static void Restore(Action body) => Restore<object?>(() => { body(); return null; });

    private static Uri DefaultSource(string skin) =>
        new($"pack://application:,,,/Janet.Wpf;component/Themes/Semantic.{skin}.xaml");

    private static ResourceDictionary Marker() => new() { { "JanetTestMarker", "here" } };
}
