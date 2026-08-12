using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Janet.Wpf.Converters;

/// <summary>
/// Bool to Visibility with an optional inversion, so "show when empty" and
/// "show when present" are two resource instances of one converter instead of
/// trigger boilerplate at every use site.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Flips the mapping, so true hides and false shows.</summary>
    public bool Invert { get; init; }

    /// <inheritdoc/>

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true != this.Invert ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
