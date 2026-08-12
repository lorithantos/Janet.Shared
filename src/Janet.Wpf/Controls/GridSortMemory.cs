using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Janet.Wpf.Controls;

/// <summary>
/// Remembers a DataGrid's user-applied column sort across ItemsSource
/// replacement. The results grids republish a whole new row list whenever
/// their backing cell recomputes -- the percentile path on every slider step --
/// and WPF hangs sorting on the collection view behind the list, so each
/// replacement silently discards the sort and the table snaps back to default
/// order mid-scrub. The view model can't hold the list stable: its outputs
/// are lazy cells over immutable views, and instance identity is what pins
/// "survived without recomputing". So remembering the sort is the view's job:
/// snapshot the sort whenever the user changes it, reapply it when the
/// binding delivers a new list. Default order stays untouched until the user
/// first sorts.
/// </summary>
/// <remarks>
/// Reapplying rides the <see cref="Binding.TargetUpdatedEvent"/>, so the
/// grid's ItemsSource binding must set <c>NotifyOnTargetUpdated=True</c> --
/// without it the behavior is inert, never wrong.
/// </remarks>
public static class GridSortMemory
{
    /// <summary>Set true on a DataGrid to keep the user's sort across an ItemsSource swap.</summary>
    public static readonly DependencyProperty RememberProperty =
        DependencyProperty.RegisterAttached(
            "Remember",
            typeof(bool),
            typeof(GridSortMemory),
            new FrameworkPropertyMetadata(false, OnRememberChanged));

    /// <summary>Per-grid state; keyed on the grid so the shared style can
    /// attach the behavior to every ledger at once.</summary>
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State", typeof(SortState), typeof(GridSortMemory));

    /// <summary>Reads whether this grid remembers its sort across an ItemsSource swap.</summary>
    /// <param name="element">The grid.</param>
    /// <returns>True when sort memory is enabled.</returns>
    public static bool GetRemember(DependencyObject element)
        => (bool)element.GetValue(RememberProperty);

    /// <summary>
    /// Turns sort memory on for a grid. The ItemsSource binding must also set
    /// <c>NotifyOnTargetUpdated=True</c>, which is how the swap is noticed at all.
    /// </summary>
    /// <param name="element">The grid.</param>
    /// <param name="value">True to remember and reapply the user's sort.</param>
    public static void SetRemember(DependencyObject element, bool value)
        => element.SetValue(RememberProperty, value);

    private static void OnRememberChanged(
        DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        // Attach-only: the property arrives via a style and lives as long as
        // the grid; everything hooked here is reachable only from the grid,
        // so teardown is the grid's own collection.
        if (element is not DataGrid grid || e.NewValue is not true)
            return;

        var state = new SortState();
        grid.SetValue(StateProperty, state);
        SubscribeToSorts(grid, state);
        grid.AddHandler(
            Binding.TargetUpdatedEvent,
            new EventHandler<DataTransferEventArgs>(OnItemsSourceUpdated));
    }

    private static void SubscribeToSorts(DataGrid grid, SortState state)
    {
        // Items.SortDescriptions is where header clicks land. The collection
        // normally lives as long as the ItemCollection, but resubscribing on
        // every source swap costs nothing and assumes nothing.
        var sorts = (INotifyCollectionChanged)grid.Items.SortDescriptions;
        if (ReferenceEquals(state.Subscribed, sorts))
            return;

        if (state.Subscribed is not null)
            state.Subscribed.CollectionChanged -= state.Handler;

        state.Handler = (_, _) => OnSortsChanged(grid, state);
        state.Subscribed = sorts;
        sorts.CollectionChanged += state.Handler;
    }

    private static void OnSortsChanged(DataGrid grid, SortState state)
    {
        if (state.Reapplying)
            return;

        // Header clicks only ever cycle ascending/descending, so a user sort
        // always leaves at least one description; the collection emptying is
        // the grid clearing itself for a source swap. Keep the snapshot.
        if (grid.Items.SortDescriptions.Count > 0)
            state.Saved = [.. grid.Items.SortDescriptions];
    }

    private static void OnItemsSourceUpdated(object? sender, DataTransferEventArgs e)
    {
        if (e.Property != ItemsControl.ItemsSourceProperty
            || sender is not DataGrid grid
            || grid.GetValue(StateProperty) is not SortState state)
        {
            return;
        }

        SubscribeToSorts(grid, state);
        if (state.Saved is not { Count: > 0 } saved)
            return;

        state.Reapplying = true;
        try
        {
            var sorts = grid.Items.SortDescriptions;
            using (grid.Items.DeferRefresh())
            {
                sorts.Clear();
                foreach (var sort in saved)
                    sorts.Add(sort);
            }

            // The view sorts the rows; the column's SortDirection is only the
            // header arrow, restored separately so the glyph matches.
            foreach (var column in grid.Columns)
                column.SortDirection = DirectionFor(column, saved);
        }
        finally
        {
            state.Reapplying = false;
        }
    }

    private static ListSortDirection? DirectionFor(
        DataGridColumn column, IReadOnlyList<SortDescription> saved)
    {
        var path = EffectiveSortPath(column);
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var sort in saved)
        {
            if (sort.PropertyName == path)
                return sort.Direction;
        }

        return null;
    }

    /// <summary>The path a header click would sort by: the explicit
    /// SortMemberPath when set, else the column's binding path -- which is
    /// what WPF records in the SortDescription for these columns.</summary>
    private static string? EffectiveSortPath(DataGridColumn column)
    {
        if (!string.IsNullOrEmpty(column.SortMemberPath))
            return column.SortMemberPath;

        return (column as DataGridBoundColumn)?.Binding is Binding binding
            ? binding.Path?.Path
            : null;
    }

    private sealed class SortState
    {
        public List<SortDescription>? Saved { get; set; }

        public bool Reapplying { get; set; }

        public INotifyCollectionChanged? Subscribed { get; set; }

        public NotifyCollectionChangedEventHandler? Handler { get; set; }
    }
}
