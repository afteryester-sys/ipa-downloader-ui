namespace IPAStudio.Core.Models;

using System;
using System.Collections.Generic;
using IPAStudio.Core.Services;

/// <summary>
/// Categories of files that can be transferred to an iOS device.
/// </summary>
public enum FileCategory
{
    Other = 0,
    Video,
    Audio,
    Book,
    Document,
    Photo,
    Archive,
    App,
    Contact,
}

/// <summary>
/// A single file to be transferred with metadata and classification.
/// </summary>
public sealed record TransferPayload(
    string FullPath,
    string FileName,
    long FileSizeBytes,
    FileCategory Category,
    string CategoryDisplayName,
    string Glyph,
    string TargetAppDisplayName)
{
    public string FormattedSize => FormatSize(FileSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:0.0} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes > 0) return $"{bytes} B";
        return "0 B";
    }
}

/// <summary>
/// Scored destination application recommendation for dropped files.
/// </summary>
public sealed record AppMatchScore(
    FileSharingApp App,
    int Score,
    bool IsRecommended,
    string MatchReason)
{
    public string DisplayName => App.Name;
    public string BundleId => App.BundleId;
}
