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

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>True for videos (MOV/MP4), false for stills.</summary>
    public bool IsVideo { get; init; }

    /// <summary>Last-modified timestamp, when reported by the device.</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }
}
