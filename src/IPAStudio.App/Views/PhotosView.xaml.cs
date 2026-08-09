using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IPAStudio.App.Controls;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App.Views;

/// <summary>
/// Code-behind for the media library. Its only job is telling the view model which rows
/// are on screen, so thumbnails are fetched for those instead of the whole Camera Roll.
///
/// This has to live in the view: only the scroll viewport knows what is visible, and
/// that is view state the view model can't see.
/// </summary>
public partial class PhotosView : UserControl
{
    /// <summary>
    /// Coalesces bursts of scroll events. Scrolling raises many events per second, and
    /// recomputing the range for each one would queue up work for rows already gone.
    /// </summary>
    private readonly DispatcherTimer _throttle = new() { Interval = TimeSpan.FromMilliseconds(90) };

    public PhotosView()
    {
        InitializeComponent();

        _throttle.Tick += (_, _) =>
        {
            _throttle.Stop();
            ReportVisibleRange();
        };

        Loaded += (_, _) =>
        {
            ScheduleReport();

            // Switching list/grid swaps which control is visible without any scrolling,
            // so no ScrollChanged fires. Without this the loader would keep serving the
            // range from the old mode, and tiles in the newly shown one would stay blank.
            if (DataContext is PhotosViewModel vm)
                vm.PropertyChanged += OnViewModelPropertyChanged;
        };

        Unloaded += (_, _) =>
        {
            _throttle.Stop();
            if (DataContext is PhotosViewModel vm)
                vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Leaving the album tiles also swaps which control is visible, for the same reason.
        if (e.PropertyName is not (nameof(PhotosViewModel.IsListView)
                                   or nameof(PhotosViewModel.IsAlbumMode))) return;

        // Wait for the layout pass that applies the new visibility, otherwise the
        // measurements below still describe the control that is about to be hidden.
        Dispatcher.BeginInvoke(ScheduleReport, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Opens an album on a single click.
    ///
    /// Handled here rather than through the list box selection because selection alone does
    /// not fire again when the same tile is clicked twice, which would leave a returning
    /// user unable to reopen the album they just left.
    /// </summary>
    private void OnAlbumTileClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not PhotosViewModel vm) return;
        if (sender is not ListBoxItem { DataContext: PhotoAlbumViewModel album }) return;

        // The tile carries a save button of its own. This handler sees the click first,
        // because it tunnels down from the container, so without this check pressing save
        // would also open the album and leave the user inside it mid-transfer. The button
        // is left to raise its own Click, which is what runs the command.
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;

        vm.OpenAlbumCommand.Execute(album);
    }

    /// <summary>True when the clicked element sits inside a button on the tile.</summary>
    private static bool IsWithinButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return true;
            if (node is ListBoxItem) return false;
        }

        return false;
    }

    /// <summary>Hooked from XAML on both list boxes; fires as either one scrolls.</summary>
    private void OnPhotosScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // A viewport size change (window resize, switching list/grid) also changes what
        // is visible, so it needs the same treatment as a scroll.
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)
            return;

        ScheduleReport();
    }

    private void ScheduleReport()
    {
        _throttle.Stop();
        _throttle.Start();
    }

    /// <summary>
    /// Works out the visible index range and hands it to the view model.
    ///
    /// In grid mode the virtualizing panel already knows exactly which items it realised,
    /// so that range is authoritative and used as-is. List mode uses a stock
    /// <c>VirtualizingStackPanel</c>, which exposes no such range, so it is derived from
    /// the scroll offset and a measured row height instead of hardcoded metrics.
    /// </summary>
    private void ReportVisibleRange()
    {
        if (DataContext is not PhotosViewModel vm) return;

        var list = FindActiveListBox();
        if (list is null || list.Items.Count == 0) return;

        if (FindDescendant<VirtualizingWrapPanel>(list) is { FirstVisibleIndex: >= 0 } panel)
        {
            vm.SetVisibleRange(panel.FirstVisibleIndex, panel.LastVisibleIndex);
            return;
        }

        var scroller = FindDescendant<ScrollViewer>(list);
        if (scroller is null) return;

        if (list.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement container
            || container.ActualHeight <= 0)
        {
            // Containers aren't built yet on the very first layout pass; try again once
            // this one completes rather than reporting a bogus range.
            Dispatcher.BeginInvoke(ScheduleReport, DispatcherPriority.Loaded);
            return;
        }

        var rowHeight = container.ActualHeight + container.Margin.Top + container.Margin.Bottom;
        if (rowHeight <= 0) return;

        var firstRow = (int)(scroller.VerticalOffset / rowHeight);
        // One extra row so a partially scrolled row still counts as visible.
        var rows = (int)Math.Ceiling(scroller.ViewportHeight / rowHeight) + 1;

        vm.SetVisibleRange(
            Math.Max(0, firstRow),
            Math.Min(list.Items.Count - 1, Math.Max(0, firstRow + rows)));
    }

    /// <summary>The list box for the mode currently shown; the other one is collapsed.</summary>
    private ListBox? FindActiveListBox()
        => PhotosGrid.IsVisible ? PhotosGrid : PhotosList.IsVisible ? PhotosList : null;

    /// <summary>
    /// Lights up the drop overlay while files are dragged over the page, and refuses drops that
    /// cannot be honoured — no device, or a transfer already in flight — so the cursor says no
    /// before the user lets go rather than a message appearing afterwards.
    ///
    /// Dragging is view plumbing, which is why it is here: the view model only ever sees the
    /// resulting list of paths.
    /// </summary>
    private void OnDragOverPage(object sender, DragEventArgs e)
    {
        var accepted = DataContext is PhotosViewModel { CanAcceptDrop: true }
                       && e.Data.GetDataPresent(DataFormats.FileDrop);

        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;

        if (DataContext is PhotosViewModel vm) vm.IsDropTarget = accepted;

        // Without this WPF keeps looking for a handler further up and applies its answer
        // instead, which shows the "no" cursor over a page that would in fact take the files.
        e.Handled = true;
    }

    private void OnDragLeavePage(object sender, DragEventArgs e)
    {
        if (DataContext is PhotosViewModel vm) vm.IsDropTarget = false;
    }

    private async void OnDropOnPage(object sender, DragEventArgs e)
    {
        if (DataContext is not PhotosViewModel vm) return;

        // Clear the overlay first: the copy runs for as long as it takes, and leaving the
        // "drop here" panel over the page for its duration would hide the progress it reports.
        vm.IsDropTarget = false;
        e.Handled = true;

        if (!vm.CanAcceptDrop) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        // Folders are expanded rather than ignored: dragging a folder of holiday pictures is
        // the obvious thing to try, and refusing it would look like the drop failed. Recursive,
        // because camera imports and phone backups both nest by date.
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            else if (File.Exists(path))
                files.Add(path);
        }

        // The view model drops anything that is not media and reports it.
        await vm.ImportFilesAsync(files);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
