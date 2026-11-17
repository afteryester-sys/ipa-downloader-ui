using System;
using System.Collections.Generic;
using System.Linq;

namespace IPAStudio.Core.Models;

/// <summary>Which media types to include during scan and export.</summary>
public enum MediaExportFilterType
{
    All,
    PhotosOnly,
    VideosOnly
}

/// <summary>How the copy step organizes files at the destination.</summary>
public enum MediaExportOrganization
{
    /// <summary>Organize into folders by detected media category (Camera, Screenshots, Selfies, Videos, Other).</summary>
    ByCategory,

    /// <summary>One sub-folder per source group (DCIM, Download, ...), mirroring the source structure.</summary>
    ByFolder,

    /// <summary>One sub-folder per year-month (e.g. 2024-05).</summary>
    ByDate,

    /// <summary>Every file straight into the destination folder, flat.</summary>
    SingleFolder,
}

/// <summary>Legacy copy mode kept for backwards compatibility.</summary>
public enum MediaExportCopyMode
{
    SingleFolder,
    ByFolder,
}

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

    /// <summary>Detected media category: Camera, Screenshots, Selfies, Videos, Other.</summary>
    public string Category { get; init; } = "";

    /// <summary>Date associated with the media file (from timestamp in filename or file creation/write time).</summary>
    public DateTime? MediaDate { get; init; }
}

/// <summary>One row of breakdown: a group/category/folder and its tally.</summary>
public sealed record MediaExportGroup
{
    public required string Name { get; init; }
    public int PhotoCount { get; init; }
    public int VideoCount { get; init; }
    public long TotalBytes { get; init; }
    public int Count => PhotoCount + VideoCount;
}

/// <summary>
/// Result of a scan: every matched file plus breakdowns by source folder, category, and date.
/// </summary>
public sealed record MediaExportScanResult
{
    public required IReadOnlyList<MediaExportFile> Files { get; init; }
    public required IReadOnlyList<MediaExportGroup> Groups { get; init; }
    public IReadOnlyList<MediaExportGroup> CategoryGroups { get; init; } = Array.Empty<MediaExportGroup>();
    public IReadOnlyList<MediaExportGroup> DateGroups { get; init; } = Array.Empty<MediaExportGroup>();

    /// <summary>Files that matched a media extension but were skipped as junk (too small —
    /// typically an icon or a thumbnail sidecar rather than an actual photo or video).</summary>
    public int SkippedJunkCount { get; init; }

    public int TotalPhotos => Files.Count(f => !f.IsVideo);
    public int TotalVideos => Files.Count(f => f.IsVideo);
    public long TotalBytes => Files.Sum(f => f.SizeBytes);
    public int TotalCount => Files.Count;
}

/// <summary>Progress of either the scan, probing, or copy step.</summary>
public sealed record MediaExportProgress(int Completed, int Total, string CurrentItem, string? StatusMessage = null);
