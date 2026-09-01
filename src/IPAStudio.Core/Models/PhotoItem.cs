namespace IPAStudio.Core.Models;

/// <summary>
/// A single media file in the device Camera Roll (accessed over AFC / DCIM).
/// </summary>
public sealed class PhotoItem
{
    /// <summary>Fully-qualified AFC path, e.g. "/DCIM/100APPLE/IMG_0001.HEIC".</summary>
    public required string RemotePath { get; init; }

    /// <summary>File name only, e.g. "IMG_0001.HEIC".</summary>
    public required string FileName { get; init; }

    /// <summary>DCIM sub-folder the file lives in (used as an album grouping).</summary>
    public required string Album { get; init; }

    /// <summary>
    /// File size in bytes. Zero until metadata has been fetched: listing the Camera
    /// Roll deliberately skips the per-file AFC stat so the grid can appear at once,
    /// and sizes are filled in afterwards. Check <see cref="HasMetadata"/> before
    /// presenting this as a real value.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>True for videos (MOV/MP4), false for stills.</summary>
    public bool IsVideo { get; init; }

    /// <summary>
    /// Last-modified timestamp, when reported by the device. Null until metadata has
    /// been fetched (see <see cref="SizeBytes"/>).
    /// </summary>
    public DateTimeOffset? ModifiedUtc { get; set; }

    /// <summary>
    /// True once the device has been asked for this file's size and date. Distinguishes
    /// "not looked up yet" from "the device genuinely reports 0 bytes / no date".
    /// </summary>
    public bool HasMetadata { get; set; }
}
