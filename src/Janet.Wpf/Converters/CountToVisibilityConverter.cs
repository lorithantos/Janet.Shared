using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Janet.Wpf.Converters;

/// <summary>
/// Zero collapses, anything positive shows — the rail badge rule: a page with
/// no faults shows nothing at all, not a zero.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
