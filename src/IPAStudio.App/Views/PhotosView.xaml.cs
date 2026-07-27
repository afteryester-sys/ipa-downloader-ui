using System.Windows;
using System.Windows.Controls;
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
        if (e.PropertyName != nameof(PhotosViewModel.IsListView)) return;

        // Wait for the layout pass that applies the new visibility, otherwise the
        // measurements below still describe the control that is about to be hidden.
        Dispatcher.BeginInvoke(ScheduleReport, DispatcherPriority.Loaded);
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
