namespace IPAStudio.App.Controls;

/// <summary>
/// A tile that wants to be laid out in the shape of what it shows, rather than in a cell of
/// the same size as its neighbours.
///
/// Implemented by the photo tiles. A camera roll is mostly upright frames — screenshots and
/// portrait photographs — and fitting an upright frame into a cell wider than it is tall left
/// the picture as a narrow strip with a broad empty panel on either side of it. Sizing the
/// cell from the frame instead means the whole picture is shown, and it is the picture, not a
/// box around it, that the eye lands on.
///
/// Read by <see cref="VirtualizingWrapPanel"/> off the item itself, not off a container: the
/// panel has to know how wide an item will be while it is still virtualised, so it cannot ask
/// an element that does not exist yet.
/// </summary>
public interface IAspectTile
{
    /// <summary>
    /// Width divided by height of the thing shown, or 0 while that is not known yet — the
    /// usual case for a photo whose thumbnail has not been fetched from the device. The panel
    /// substitutes an upright default for 0, so a roll lays out sensibly before anything has
    /// been decoded and settles as the thumbnails arrive.
    /// </summary>
    double TileAspect { get; }
}
