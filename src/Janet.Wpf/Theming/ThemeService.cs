namespace Janet.Wpf.Theming;

using System.Windows;

/// <summary>
/// The one piece of theming that is code rather than data: swapping the active
/// skin dictionary. Everything else about a skin lives in <c>Themes/*.xaml</c>
/// and reaches controls through <c>DynamicResource</c>, which updates itself
/// when the dictionary underneath it is replaced.
///
/// Deliberately has NO change event. An earlier version raised a static
/// <c>SkinChanged</c> so chart viewmodels could rebuild their paints, and its
/// handlers were never unsubscribed — a leak that outlived every attempt to
/// remember to unhook it. The reliable way not to leak a static event is not to
/// have one; a control that must react imperatively can watch the resource it
/// already binds to.
/// </summary>
public static class ThemeService
{
    /// <summary>Name of the light skin, and the skin in force before the first swap.</summary>
    public const string Light = nameof(Light);

    /// <summary>Name of the dark skin.</summary>
    public const string Dark = nameof(Dark);

    /// <summary>
    /// Which merged dictionary the skin occupies. Slot 0 by convention: a skin
    /// brings its own primitives, so it must be merged before anything that
    /// resolves against it.
    /// </summary>
    private const int SkinSlot = 0;

    private static Func<string, Uri> resolve = DefaultSource;

    /// <summary>
    /// The skin currently applied. Reflects the last successful
    /// <see cref="ApplySkin"/>, so it is a statement about the merged
    /// dictionaries rather than a preference someone recorded.
    /// </summary>
    public static string CurrentSkin { get; private set; } = Light;

    /// <summary>
    /// Points the service at a different set of skin dictionaries — for an app
    /// that ships its own semantic layer rather than using this package's.
    ///
    /// One seam rather than a configuration system: the resolver is handed a
    /// skin name and returns the dictionary to merge. Call before the first
    /// <see cref="ApplySkin"/>; the current skin is re-applied so a call after
    /// startup is not silently ignored.
    /// </summary>
    public static void UseSkinSource(Func<string, Uri> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        resolve = resolver;

        if (Application.Current is not null) ApplySkin(CurrentSkin);
    }

    /// <summary>
    /// Merges the named skin into the skin slot, replacing whatever was there.
    /// Controls bound through <c>DynamicResource</c> re-resolve on their own.
    /// </summary>
    /// <param name="skin"><see cref="Light"/>, <see cref="Dark"/>, or a name the
    /// configured resolver understands.</param>
    /// <exception cref="InvalidOperationException">No <see cref="Application"/>
    /// is running, so there are no resources to merge into.</exception>
    public static void ApplySkin(string skin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skin);

        var application = Application.Current
            ?? throw new InvalidOperationException(
                "No Application is running; a skin can only be applied once WPF has started.");

        var dictionaries = application.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = resolve(skin) };

        // Assigning past the end throws; an app whose App.xaml merged nothing
        // still gets a skin rather than an ArgumentOutOfRangeException it would
        // have to read this source to understand.
        if (dictionaries.Count > SkinSlot) dictionaries[SkinSlot] = replacement;
        else dictionaries.Insert(SkinSlot, replacement);

        CurrentSkin = skin;
    }

    /// <summary>Swaps between <see cref="Light"/> and <see cref="Dark"/>.</summary>
    public static void ToggleSkin() => ApplySkin(CurrentSkin == Light ? Dark : Light);

    /// <summary>
    /// This package's own semantic dictionaries. An absolute pack URI, because a
    /// relative one resolves against the *consuming* application's assembly and
    /// would not find anything shipped in here.
    /// </summary>
    private static Uri DefaultSource(string skin) =>
        new($"pack://application:,,,/Janet.Wpf;component/Themes/Semantic.{skin}.xaml", UriKind.Absolute);
}
