namespace IPAStudio.App.Controls;

/// <summary>
/// Marks an item that is a section heading rather than a tile, so
/// <see cref="VirtualizingWrapPanel"/> gives it a row of its own across the full width.
///
/// The panel tests for this interface instead of taking a delegate or a type name, because
/// it has to decide the layout of an item <em>before</em> a container exists for it: slot
/// positions are computed straight from the ItemsControl's item list while virtualised, and
/// only the visible slots ever get realised.
/// </summary>
public interface IGridGroupHeader
{
}
