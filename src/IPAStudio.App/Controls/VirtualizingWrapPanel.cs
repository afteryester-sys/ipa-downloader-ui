using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IPAStudio.App.Controls;

/// <summary>
/// A wrap panel that virtualises: it only creates containers for the tiles currently
/// on screen. WPF has no built-in equivalent — the stock <see cref="WrapPanel"/> never
/// virtualises, so a ListBox using it builds a container for every item up front. With
/// a few thousand photos that alone makes opening and scrolling the grid slow, no
/// matter how cheaply thumbnails are loaded.
///
/// The layout assumes every tile is the same size, which holds here because the photo
/// tile template has a fixed width and height. That assumption is what keeps this small:
/// row and column positions are plain arithmetic instead of an incremental measure pass.
/// The size is measured once from a real container rather than hardcoded, so restyling
/// the tile can't silently break the layout.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    /// <summary>Uniform tile size including margins; measured from the first container.</summary>
    private Size _itemSize = Size.Empty;

    /// <summary>
    /// Anything whose change resizes the tiles - bind it to whatever the item template's size
    /// comes from.
    ///
    /// The measured size is cached, deliberately: measuring one container per layout pass is what
    /// keeps this panel cheap. That cache is also why a resizable tile needs this. Without it the
    /// panel would keep laying out a grid of the old size while the tiles drew at the new one,
    /// overlapping them or leaving gaps, and only a fresh navigation would put it right.
    /// </summary>
    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
        nameof(TileSize), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTileSizeChanged));

    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    private static void OnTileSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not VirtualizingWrapPanel panel) return;

        panel._itemSize = Size.Empty;

        // The containers are recycled, so they carry the previous measurement with them; without
        // invalidating each one the re-measure below would be handed the old desired size back.
        foreach (UIElement child in panel.InternalChildren)
            child.InvalidateMeasure();

        panel.InvalidateMeasure();
    }

    private int _columns = 1;
    private Size _extent;
    private Size _viewport;
    private double _verticalOffset;

    /// <summary>Index range realised by the last measure pass, or -1 when empty.</summary>
    public int FirstVisibleIndex { get; private set; } = -1;
    public int LastVisibleIndex { get; private set; } = -1;

    /// <summary>Raised after the realised range changes, so callers can react to scrolling.</summary>
    public event EventHandler? VisibleRangeChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;

        // Touching InternalChildren initialises the container generator; it must happen
        // before any generator call.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;

        if (itemCount == 0)
        {
            _extent = default;
            _viewport = availableSize;
            SetVisibleRange(-1, -1);
            ScrollOwner?.InvalidateScrollInfo();
            return default;
        }

        EnsureItemSize(generator);

        // A degenerate measurement would make the column count meaningless and could
        // divide by zero below; fall back to the template's nominal tile size.
        var itemWidth = _itemSize.Width > 0 ? _itemSize.Width : 142;
        var itemHeight = _itemSize.Height > 0 ? _itemSize.Height : 164;

        // Under an unconstrained parent (for example inside a StackPanel) fall back to a
        // single column rather than dividing by infinity.
        var viewportWidth = double.IsInfinity(availableSize.Width) ? itemWidth : availableSize.Width;
        var viewportHeight = double.IsInfinity(availableSize.Height) ? itemHeight : availableSize.Height;

        _columns = Math.Max(1, (int)Math.Floor(viewportWidth / itemWidth));
        var rows = (int)Math.Ceiling((double)itemCount / _columns);

        _extent = new Size(_columns * itemWidth, rows * itemHeight);
        _viewport = new Size(viewportWidth, viewportHeight);

        // Narrowing the window or removing items can leave the offset past the end.
        _verticalOffset = Math.Max(0, Math.Min(_verticalOffset, Math.Max(0, _extent.Height - _viewport.Height)));
        ScrollOwner?.InvalidateScrollInfo();

        // One extra row above and below keeps tiles from popping in at the edges.
        var firstRow = Math.Max(0, (int)Math.Floor(_verticalOffset / itemHeight) - 1);
        var lastRow = (int)Math.Ceiling((_verticalOffset + viewportHeight) / itemHeight) + 1;

        var first = Math.Max(0, firstRow * _columns);
        var last = Math.Min(itemCount - 1, lastRow * _columns - 1);

        RealizeRange(generator, first, last, new Size(itemWidth, itemHeight));
        SetVisibleRange(first, last);

        return new Size(
            double.IsInfinity(availableSize.Width) ? _extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = _itemSize.Width > 0 ? _itemSize.Width : 142;
        var itemHeight = _itemSize.Height > 0 ? _itemSize.Height : 164;
        // IndexFromGeneratorPosition is an explicit interface implementation, so it is
        // only reachable through IItemContainerGenerator.
        var generator = (IItemContainerGenerator)ItemContainerGenerator;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) continue;

            var row = itemIndex / _columns;
            var col = itemIndex % _columns;

            // Offsetting by the scroll position here is what makes this panel scroll:
            // children are placed in content space, shifted by the viewport.
            child.Arrange(new Rect(
                col * itemWidth,
                row * itemHeight - _verticalOffset,
                itemWidth,
                itemHeight));
        }

        return finalSize;
    }

    /// <summary>
    /// Measures one real container to learn the uniform tile size. Done once, with an
    /// unbounded constraint so the template reports the size it actually wants.
    /// </summary>
    private void EnsureItemSize(IItemContainerGenerator generator)
    {
        if (!_itemSize.IsEmpty) return;

        var position = generator.GeneratorPositionFromIndex(0);
        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            if (generator.GenerateNext(out var isNew) is not UIElement child) return;

            if (isNew)
            {
                AddInternalChild(child);
                generator.PrepareItemContainer(child);
            }

            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _itemSize = child.DesiredSize;
        }
    }

    /// <summary>Creates containers for <paramref name="first"/>..<paramref name="last"/> and drops the rest.</summary>
    private void RealizeRange(IItemContainerGenerator generator, int first, int last, Size itemSize)
    {
        var startPosition = generator.GeneratorPositionFromIndex(first);

        // Offset 0 means the index maps onto an existing container, so insertion starts
        // at that child; otherwise it belongs after it.
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (var i = first; i <= last; i++, childIndex++)
            {
                if (generator.GenerateNext(out var isNew) is not UIElement child) break;

                // A recycled container comes back with isNew == false and is no longer in
                // InternalChildren, so it has to be re-inserted by hand. Skipping this is
                // why recycled tiles would silently disappear while scrolling.
                if (isNew || !InternalChildren.Contains(child))
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                }

                generator.PrepareItemContainer(child);

                child.Measure(itemSize);
            }
        }

        VirtualizeRangeOutside(generator, first, last);
    }

    /// <summary>
    /// Releases containers outside the visible range. Honours the ItemsControl's
    /// virtualization mode so recycling containers are reused instead of discarded.
    /// </summary>
    private void VirtualizeRangeOutside(IItemContainerGenerator generator, int first, int last)
    {
        // Recycle lives on IRecyclingItemContainerGenerator, not on the base generator
        // interface. If the generator doesn't support recycling, fall back to Remove.
        var recycler = GetVirtualizationMode(this) == VirtualizationMode.Recycling
            ? generator as IRecyclingItemContainerGenerator
            : null;

        // Walk backwards: removing a child shifts the indices after it.
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= first && itemIndex <= last) continue;

            if (recycler is not null) recycler.Recycle(position, 1);
            else generator.Remove(position, 1);

            RemoveInternalChildRange(i, 1);
        }
    }

    private void SetVisibleRange(int first, int last)
    {
        if (first == FirstVisibleIndex && last == LastVisibleIndex) return;

        FirstVisibleIndex = first;
        LastVisibleIndex = last;
        VisibleRangeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        // A new collection may use a different template; re-measure the tile size.
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            _itemSize = Size.Empty;
            _verticalOffset = 0;
        }

        InvalidateMeasure();
    }

    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        _itemSize = Size.Empty;
        SetVisibleRange(-1, -1);
    }

    // ── IScrollInfo ───────────────────────────────────────────────────────────────
    // Implementing this is what lets the panel scroll pixel-wise inside a ScrollViewer
    // while only realising visible rows. Horizontal scrolling is unused: tiles wrap to
    // the available width instead.

    /// <summary>Approximate tile height used for line/wheel steps before the first measure.</summary>
    private double LineHeight => _itemSize.Height > 0 ? _itemSize.Height : 164;

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        var clamped = Math.Max(0, Math.Min(offset, max));
        if (Math.Abs(clamped - _verticalOffset) < 0.5) return;

        _verticalOffset = clamped;
        // Re-measure rather than only re-arrange: a new range must be realised.
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetHorizontalOffset(double offset) { /* tiles wrap; no horizontal scroll */ }

    public void LineUp() => SetVerticalOffset(_verticalOffset - LineHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + LineHeight);
    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);

    // Three tile rows per notch matches the feel of the list view.
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - LineHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + LineHeight * 3);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    /// <summary>
    /// Scrolls a child into view. Needed for keyboard navigation and for
    /// <c>ScrollIntoView</c>, which the selection logic relies on.
    /// </summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual as UIElement;
        if (child is null) return rectangle;

        var generator = (IItemContainerGenerator)ItemContainerGenerator;

        var index = -1;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], child)) continue;
            index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            break;
        }
        if (index < 0) return rectangle;

        var row = index / Math.Max(1, _columns);
        var top = row * LineHeight;
        var bottom = top + LineHeight;

        if (top < _verticalOffset) SetVerticalOffset(top);
        else if (bottom > _verticalOffset + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);

        return new Rect(0, top - _verticalOffset, _itemSize.Width, LineHeight);
    }
}
