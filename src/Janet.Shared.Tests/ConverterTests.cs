namespace Janet.Shared.Tests;

using System.Globalization;
using System.Windows;
using Janet.Wpf.Converters;
using Xunit;

/// <summary>
/// The two converters, including what they do with input they were not promised.
///
/// <para>A converter runs inside the binding engine, which swallows exceptions and
/// writes a trace line nobody reads. So "what happens on null" is not a pedantic
/// question here: the wrong answer is an element that is silently visible on a
/// page where it should not be.</para>
/// </summary>
public sealed class ConverterTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(42, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void ACountShowsOnlyWhenItIsPositive(int count, bool visible) =>
        Assert.Equal(
            visible ? Visibility.Visible : Visibility.Collapsed,
            new CountToVisibilityConverter().Convert(count, typeof(Visibility), null!, Culture));

    /// <summary>
    /// Anything that is not an int collapses. A badge whose binding failed must
    /// disappear rather than sit there showing nothing in a box.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("3")]
    [InlineData(3L)]
    [InlineData(3.0)]
    public void ACountThatIsNotAnIntCollapses(object? value) =>
        Assert.Equal(
            Visibility.Collapsed,
            new CountToVisibilityConverter().Convert(value!, typeof(Visibility), null!, Culture));

    [Theory]
    [InlineData(true, false, Visibility.Visible)]
    [InlineData(false, false, Visibility.Collapsed)]
    [InlineData(true, true, Visibility.Collapsed)]
    [InlineData(false, true, Visibility.Visible)]
    public void InversionFlipsTheMapping(bool value, bool invert, Visibility expected) =>
        Assert.Equal(
            expected,
            new BoolToVisibilityConverter { Invert = invert }
                .Convert(value, typeof(Visibility), null!, Culture));

    /// <summary>
    /// Null is not true, so it follows the false branch — which means an INVERTED
    /// converter SHOWS on null. That is the "show when empty" rule behaving
    /// correctly on a binding that has not arrived yet, and it is worth pinning
    /// rather than rediscovering.
    /// </summary>
    [Theory]
    [InlineData(false, Visibility.Collapsed)]
    [InlineData(true, Visibility.Visible)]
    public void NullFollowsTheFalseBranch(bool invert, Visibility expected) =>
        Assert.Equal(
            expected,
            new BoolToVisibilityConverter { Invert = invert }
                .Convert(null!, typeof(Visibility), null!, Culture));

    /// <summary>Both are one-way. ConvertBack must refuse rather than guess.</summary>
    [Fact]
    public void NeitherConverterGoesBackwards()
    {
        Assert.Throws<NotSupportedException>(() =>
            new BoolToVisibilityConverter().ConvertBack(Visibility.Visible, typeof(bool), null!, Culture));

        Assert.Throws<NotSupportedException>(() =>
            new CountToVisibilityConverter().ConvertBack(Visibility.Visible, typeof(int), null!, Culture));
    }

    private static CultureInfo Culture => CultureInfo.InvariantCulture;
}
