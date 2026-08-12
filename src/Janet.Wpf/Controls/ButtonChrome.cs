using System.Windows;
using System.Windows.Media;

namespace Janet.Wpf.Controls;

/// <summary>
/// Attached hover brush for the shared button template. Primary and ghost
/// buttons differ only in resting/hover surfaces; parameterizing the hover
/// keeps one template instead of a copy per variant (Controls.xaml).
/// </summary>
public static class ButtonChrome
{
    /// <summary>The brush a button uses while the pointer is over it.</summary>
    public static readonly DependencyProperty HoverBrushProperty =
        DependencyProperty.RegisterAttached(
            "HoverBrush",
            typeof(Brush),
            typeof(ButtonChrome),
            new FrameworkPropertyMetadata(null));

    /// <summary>Reads the hover brush attached to a button.</summary>
    /// <param name="element">The button.</param>
    /// <returns>The brush, or null to leave the template's default.</returns>
    public static Brush? GetHoverBrush(DependencyObject element)
        => (Brush?)element.GetValue(HoverBrushProperty);

    /// <summary>Sets the hover brush, which is what distinguishes button variants sharing one template.</summary>
    /// <param name="element">The button.</param>
    /// <param name="value">The brush, or null for the template's default.</param>
    public static void SetHoverBrush(DependencyObject element, Brush? value)
        => element.SetValue(HoverBrushProperty, value);
}
