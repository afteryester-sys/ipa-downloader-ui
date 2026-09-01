namespace IPAStudio.Core.Models;

/// <summary>One media file found by <see cref="Services.MediaExportService"/>.</summary>
public sealed record MediaExportFile
{
    /// <summary>Full path on the scanned source (drive, folder or mounted device).</summary>
    public required string FullPath { get; init; }

    /// <summary>Name of the top-level folder under the scanned root the file was found in
    /// (e.g. "DCIM", "Download"), or "" when the file sits directly in the root.</summary>
    public required string GroupName { get; init; }

    public required bool IsVideo { get; init; }

    public required long SizeBytes { get; init; }
}

/// <summary>One row of the "found so far" breakdown: a top-level source folder and its tally.</summary>
public sealed record MediaExportGroup
{
    public required string Name { get; init; }
    public int PhotoCount { get; init; }
    public int VideoCount { get; init; }
    public long TotalBytes { get; init; }
    public int Count => PhotoCount + VideoCount;
}

/// <summary>
/// Result of a scan: every matched file (for the copy step) plus the per-folder breakdown
/// the UI shows before anything is copied.
/// </summary>
public sealed record MediaExportScanResult
{
    public required IReadOnlyList<MediaExportFile> Files { get; init; }
    public required IReadOnlyList<MediaExportGroup> Groups { get; init; }

    /// <summary>Files that matched a media extension but were skipped as junk (too small —
    /// typically an icon or a thumbnail sidecar rather than an actual photo or video).</summary>
    public int SkippedJunkCount { get; init; }

    public int TotalPhotos => Groups.Sum(g => g.PhotoCount);
    public int TotalVideos => Groups.Sum(g => g.VideoCount);
    public long TotalBytes => Groups.Sum(g => g.TotalBytes);
    public int TotalCount => Files.Count;
}

/// <summary>How the copy step lays files out at the destination.</summary>
public enum MediaExportCopyMode
{
    /// <summary>Every file straight into the destination folder, regardless of where it came from.</summary>
    SingleFolder,

    /// <summary>One sub-folder per source group (DCIM, Download, ...), mirroring what the scan found.</summary>
    ByFolder,
}

/// <summary>Progress of either the scan or the copy step.</summary>
public sealed record MediaExportProgress(int Completed, int Total, string CurrentItem);
