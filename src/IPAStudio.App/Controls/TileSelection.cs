using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Controls;

/// <summary>
/// An item in a list that can be picked out as part of a batch. The three pages that offer
/// batch actions each already had a per-item <c>IsSelected</c> flag driving their "download
/// selected" and "save selected" commands; this interface is only what lets one piece of
/// click handling work against all of them.
/// </summary>
public interface ISelectableTile
{
    /// <summary>Whether the item is part of the current batch.</summary>
    bool IsSelected { get; set; }

    /// <summary>
    /// Whether the item can join a batch at all. An app that is already downloading, or that
    /// cannot be fetched, is shown but is not a candidate, and selecting it would put the
    /// page in a state its commands would then have to filter back out.
    /// </summary>
    bool CanSelect { get; }
}

/// <summary>
/// Click-to-select for the tile and row lists, attached to a <see cref="ListBox"/> rather
/// than written into each page: the behaviour (plain click replaces the batch, Ctrl adds one,
/// Shift adds a run) has to be identical everywhere or it is worse than not having it.
///
/// Deliberately not implemented by binding <c>ListBoxItem.IsSelected</c> to the item's flag
/// and switching the ListBox to extended selection, which looks like the shorter route. The
/// photo grid's items are tiles and date headings mixed together, and WPF's own range
/// selection has no notion of an item that may not be picked, so a Shift-drag would take the
/// headings and the ineligible apps along with everything else. Driving the item's own flag
/// keeps that decision in one place, and leaves <c>SelectedItem</c> free to go on meaning
/// "the item being previewed".
/// </summary>
public static class TileSelection
{
    /// <summary>
    /// Which way the list selects. Bound to the page's setting, so switching the mode takes
    /// effect on the open page instead of on the next visit.
    ///
    /// Nullable with a null default, which is what makes the click handler attach at all.
    /// WPF raises the change callback only when the effective value actually changes, so with
    /// a default of <see cref="TileSelectionMode.Click"/> a page whose setting was also Click
    /// — the shipped default — bound the same value the property already held, the callback
    /// never ran, and nothing was ever hooked. Click selection was dead on every list while
    /// the tick boxes stayed hidden for being in click mode, so no list could be selected in
    /// any way. Against a null default every real value is a change, whichever mode it is.
    /// </summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(TileSelectionMode?), typeof(TileSelection),
        new PropertyMetadata(null, OnModeChanged));

    public static void SetMode(DependencyObject element, TileSelectionMode? value) =>
        element.SetValue(ModeProperty, value);

    public static TileSelectionMode? GetMode(DependencyObject element) =>
        (TileSelectionMode?)element.GetValue(ModeProperty);

    /// <summary>Anchor for Shift-range selection: the item of the last plain or Ctrl click.</summary>
    private static readonly DependencyProperty AnchorProperty = DependencyProperty.RegisterAttached(
        "Anchor", typeof(object), typeof(TileSelection));

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;

        // Hooked unconditionally and filtered inside the handler instead of subscribing and
        // unsubscribing as the mode changes: the mode is bound, so it can arrive before the
        // list is loaded and change again afterwards, and paired handlers are easy to leak.
        list.PreviewMouseLeftButtonDown -= OnListMouseDown;
        list.PreviewMouseLeftButtonDown += OnListMouseDown;
    }

    private static void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (GetMode(list) != TileSelectionMode.Click) return;

        // A press on a control inside the tile is that control's: the tiles carry buttons
        // (save this app, open this album) and a checkbox, and selecting as well would make
        // pressing them feel like a misclick.
        if (e.OriginalSource is DependencyObject source && IsInteractive(source, list)) return;

        var container = FindContainer(e.OriginalSource as DependencyObject, list);

        // A press on the empty space below the tiles clears the batch, which is the only
        // discoverable way to undo a selection made by clicking.
        if (container is null)
        {
            if (Keyboard.Modifiers == ModifierKeys.None) SelectOnly(list, null);
            return;
        }

        if (container.DataContext is not ISelectableTile tile) return;

        // An item that cannot join a batch is inert rather than destructive: clicking an
        // unreadable archive or a busy app would otherwise fall through to "select only this",
        // clearing a batch and selecting nothing in its place.
        if (!tile.CanSelect) return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (shift && list.GetValue(AnchorProperty) is { } anchor)
        {
            SelectRange(list, anchor, container.DataContext);
            return;
        }

        if (ctrl)
        {
            if (tile.CanSelect) tile.IsSelected = !tile.IsSelected;
            list.SetValue(AnchorProperty, container.DataContext);
            return;
        }

        // A plain click on the one item already selected is left alone, so that clicking a
        // selected tile to preview it does not throw the rest of the batch away.
        if (!(tile.IsSelected && CountSelected(list) == 1)) SelectOnly(list, container.DataContext);
        list.SetValue(AnchorProperty, container.DataContext);
    }

    /// <summary>Clears the batch and selects <paramref name="only"/>, when it can be selected.</summary>
    private static void SelectOnly(ListBox list, object? only)
    {
        foreach (var candidate in Enumerate(list))
        {
            var wanted = ReferenceEquals(candidate, only) && candidate.CanSelect;
            if (candidate.IsSelected != wanted) candidate.IsSelected = wanted;
        }
    }

    /// <summary>
    /// Selects everything between the anchor and the clicked item inclusive, leaving items
    /// outside the run alone so a Shift-click can extend a batch rather than replace it.
    /// </summary>
    private static void SelectRange(ListBox list, object anchor, object target)
    {
        var items = list.Items;
        var from = items.IndexOf(anchor);
        var to = items.IndexOf(target);
        if (from < 0 || to < 0) return;

        if (from > to) (from, to) = (to, from);

        for (var i = from; i <= to; i++)
        {
            if (items[i] is not ISelectableTile tile || !tile.CanSelect) continue;
            if (!tile.IsSelected) tile.IsSelected = true;
        }
    }

    private static int CountSelected(ListBox list)
    {
        var count = 0;
        foreach (var tile in Enumerate(list))
            if (tile.IsSelected) count++;
        return count;
    }

    /// <summary>
    /// The list's selectable items. Reads <c>Items</c> rather than the bound source so it
    /// sees the same order and filtering the user does, and skips anything that is not a
    /// tile — the photo grid's date headings share the collection with real items.
    /// </summary>
    private static IEnumerable<ISelectableTile> Enumerate(ListBox list)
    {
        foreach (var item in (IEnumerable)list.Items)
            if (item is ISelectableTile tile) yield return tile;
    }

    /// <summary>Walks up from the clicked element to the row or tile that contains it.</summary>
    private static ListBoxItem? FindContainer(DependencyObject? source, ListBox list)
    {
        while (source is not null && !ReferenceEquals(source, list))
        {
            if (source is ListBoxItem container) return container;
            source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>
    /// True when the press landed on something that handles clicks itself. Checked by walking
    /// up to the container, so it also covers controls nested inside a tile's layout.
    /// </summary>
    private static bool IsInteractive(DependencyObject source, ListBox list)
    {
        while (source is not null && source is not ListBoxItem && !ReferenceEquals(source, list))
        {
            if (source is ButtonBase or CheckBox or TextBoxBase or ScrollBar or ComboBox) return true;
            source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
