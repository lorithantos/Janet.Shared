namespace Janet.Shared.Tests;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Janet.Wpf.Controls;
using Xunit;

/// <summary>
/// Keeping a user's column sort across an ItemsSource swap.
///
/// <para>The behaviour exists because WPF hangs sorting on the collection view
/// behind the list, so republishing the list throws the sort away and the table
/// snaps back to default order — mid-scrub, while somebody is dragging a slider.
/// Every assertion here is about that moment.</para>
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class GridSortMemoryTests
{
    private readonly JanetWpfFixture wpf;

    public GridSortMemoryTests(JanetWpfFixture wpf) => this.wpf = wpf;

    /// <summary>The whole point: sort, swap the list, still sorted.</summary>
    [Fact]
    public void AUserSortSurvivesTheListBeingReplaced()
    {
        var after = this.wpf.Run(() =>
        {
            var (grid, holder) = Bound(remember: true);

            grid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            holder.Rows = [new Row("c"), new Row("a"), new Row("b")];

            return grid.Items.SortDescriptions.ToList();
        });

        var sort = Assert.Single(after);
        Assert.Equal("Name", sort.PropertyName);
        Assert.Equal(ListSortDirection.Descending, sort.Direction);
    }

    /// <summary>
    /// The header glyph is restored too. The view sorts the rows and the column's
    /// SortDirection draws the arrow — reapplying one without the other leaves a
    /// correctly sorted table that claims to be unsorted.
    /// </summary>
    [Fact]
    public void TheHeaderArrowIsRestoredWithTheSort()
    {
        var direction = this.wpf.Run(() =>
        {
            var (grid, holder) = Bound(remember: true);

            grid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            holder.Rows = [new Row("c"), new Row("a")];

            return grid.Columns[0].SortDirection;
        });

        Assert.Equal(ListSortDirection.Descending, direction);
    }

    /// <summary>
    /// Untouched by default. A grid nobody has sorted must keep arriving in the
    /// order the viewmodel chose — imposing one would silently override a
    /// deliberate ordering.
    /// </summary>
    [Fact]
    public void ADefaultOrderIsLeftAloneUntilTheUserSorts()
    {
        var after = this.wpf.Run(() =>
        {
            var (grid, holder) = Bound(remember: true);
            holder.Rows = [new Row("c"), new Row("a")];

            return grid.Items.SortDescriptions.Count;
        });

        Assert.Equal(0, after);
    }

    /// <summary>
    /// Without the attached property the behaviour is absent, not merely idle.
    /// This is the control case: if the swap preserved the sort here too, every
    /// other test in this file would be proving nothing.
    /// </summary>
    [Fact]
    public void WithoutTheAttachedPropertyTheSortIsLost()
    {
        var after = this.wpf.Run(() =>
        {
            var (grid, holder) = Bound(remember: false);

            grid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            holder.Rows = [new Row("c"), new Row("a")];

            return grid.Items.SortDescriptions.Count;
        });

        Assert.Equal(0, after);
    }

    /// <summary>
    /// Documented limit, pinned so it stays a limit rather than becoming a
    /// surprise: the behaviour rides Binding.TargetUpdatedEvent, so a binding
    /// without NotifyOnTargetUpdated leaves it inert. Inert, never wrong — but a
    /// caller who forgets the flag gets silence, which is worth a test saying so.
    /// </summary>
    [Fact]
    public void WithoutNotifyOnTargetUpdatedTheBehaviourIsInert()
    {
        var after = this.wpf.Run(() =>
        {
            var (grid, holder) = Bound(remember: true, notify: false);

            grid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            holder.Rows = [new Row("c"), new Row("a")];

            return grid.Items.SortDescriptions.Count;
        });

        Assert.Equal(0, after);
    }

    /// <summary>Re-sorting after a swap replaces the memory rather than stacking on it.</summary>
    [Fact]
    public void TheMostRecentUserSortIsTheOneRemembered()
    {
        var after = this.wpf.Run(() =>
        {
            var (grid, holder) = Bound(remember: true);

            grid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            holder.Rows = [new Row("c"), new Row("a")];

            grid.Items.SortDescriptions.Clear();
            grid.Items.SortDescriptions.Add(new SortDescription("Rank", ListSortDirection.Ascending));
            holder.Rows = [new Row("b"), new Row("d")];

            return grid.Items.SortDescriptions.ToList();
        });

        var sort = Assert.Single(after);
        Assert.Equal("Rank", sort.PropertyName);
        Assert.Equal(ListSortDirection.Ascending, sort.Direction);
    }

    // ---- fixtures -----------------------------------------------------------

    private static (DataGrid Grid, Holder Holder) Bound(bool remember, bool notify = true)
    {
        var holder = new Holder { Rows = [new Row("b"), new Row("a")] };

        var grid = new DataGrid { AutoGenerateColumns = false, DataContext = holder };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(nameof(Row.Name)) });
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(nameof(Row.Rank)) });

        // Set BEFORE the binding, as a style would: the behaviour attaches on the
        // property changing, and must be listening by the first target update.
        GridSortMemory.SetRemember(grid, remember);

        grid.SetBinding(
            ItemsControl.ItemsSourceProperty,
            new Binding(nameof(Holder.Rows)) { NotifyOnTargetUpdated = notify });

        var host = new Border { Child = grid, Width = 300, Height = 200 };
        host.Measure(new Size(300, 200));
        host.Arrange(new Rect(0, 0, 300, 200));
        host.UpdateLayout();

        return (grid, holder);
    }

    private sealed record Row(string Name)
    {
        public int Rank => this.Name.Length;
    }

    private sealed class Holder : INotifyPropertyChanged
    {
        private IReadOnlyList<Row> rows = [];

        public event PropertyChangedEventHandler? PropertyChanged;

        public IReadOnlyList<Row> Rows
        {
            get => this.rows;
            set
            {
                this.rows = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Rows)));
            }
        }
    }
}
