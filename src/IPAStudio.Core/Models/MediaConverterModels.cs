using System;
using System.Collections.Generic;

namespace IPAStudio.Core.Models;

/// <summary>
/// Type of media item detected for conversion.
/// </summary>
public enum MediaItemKind
{
    HeicPhoto,
    OtherPhoto,
    Video,
    LivePhotoPair,
    Unknown
}

/// <summary>
/// Execution mode for HEIC conversion during device photo export.
/// </summary>
public enum HeicExportConversionMode
{
    Prompt = 0,
    PostTransfer = 1,
    DuringTransfer = 2,
    Disabled = 3
}

/// <summary>
/// Live Photo conversion output mode.
/// </summary>
public enum LivePhotoConversionMode
{
    UniversalJpegAndMp4 = 0,
    AnimatedGif = 1,
    VideoOnlyMp4 = 2,
    PhotoOnlyJpeg = 3
}

/// <summary>
/// Status of an individual media item in the conversion queue.
/// </summary>
public enum MediaConversionStatus
{
    Pending,
    Converting,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// An item queued or processed in the media converter.
/// </summary>
public sealed class MediaConverterItem
{
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Extension { get; set; } = "";
    public MediaItemKind Kind { get; set; } = MediaItemKind.Unknown;

    /// <summary>
    /// For Live Photos: path to the paired MOV video file.
    /// </summary>
    public string? PairedVideoPath { get; set; }

    /// <summary>
    /// For Live Photos: path to the paired HEIC/JPEG photo file.
    /// </summary>
    public string? PairedPhotoPath { get; set; }

    public string? OutputPath { get; set; }
    public MediaConversionStatus Status { get; set; } = MediaConversionStatus.Pending;
    public string? ErrorMessage { get; set; }
    public double ProgressPercent { get; set; }

    public string FormattedSize
    {
        get
        {
            if (FileSizeBytes < 1024) return $"{FileSizeBytes} B";
            if (FileSizeBytes < 1024 * 1024) return $"{FileSizeBytes / 1024.0:F1} KB";
            if (FileSizeBytes < 1024 * 1024 * 1024) return $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}

/// <summary>
/// Configuration options for batch media conversion.
/// </summary>
public sealed class MediaConverterOptions
{
    /// <summary>
    /// Output directory. If empty or null, output is saved alongside source files.
    /// </summary>
    public string? OutputFolder { get; set; }

    /// <summary>
    /// Whether to save converted files in the same folder as the source file.
    /// </summary>
    public bool SaveInSourceFolder { get; set; } = true;

    /// <summary>
    /// Preferred output format for photos ("jpeg" or "png"). Default is "jpeg".
    /// </summary>
    public string PhotoFormat { get; set; } = "jpeg";

    /// <summary>
    /// JPEG quality from 1 to 100. Default is 92.
    /// </summary>
    public int JpegQuality { get; set; } = 92;

    /// <summary>
    /// Video quality preset: "auto", "1080p", "720p". Default is "auto".
    /// </summary>
    public string VideoQuality { get; set; } = "auto";

    /// <summary>
    /// Live Photo conversion mode.
    /// </summary>
    public LivePhotoConversionMode LivePhotoMode { get; set; } = LivePhotoConversionMode.UniversalJpegAndMp4;

    /// <summary>
    /// Whether to delete the original source files after successful conversion.
    /// </summary>
    public bool DeleteOriginals { get; set; } = false;

    /// <summary>
    /// When scanning a directory, recursively process subdirectories.
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Replicate directory hierarchy in the output folder.
    /// </summary>
    public bool PreserveSubdirectories { get; set; } = true;

    /// <summary>
    /// Preserve original file timestamps (creation and modified time).
    /// </summary>
    public bool PreserveTimestamps { get; set; } = true;
}

/// <summary>
/// Progress reporting during media conversion.
/// </summary>
public sealed class MediaConverterProgress
{
    public int CompletedCount { get; }
    public int TotalCount { get; }
    public string CurrentItemName { get; }
    public double OverallProgressPercent { get; }
    public int SucceededCount { get; }
    public int FailedCount { get; }
    public string StatusMessage { get; }

    public MediaConverterProgress(
        int completedCount,
        int totalCount,
        string currentItemName,
        double overallProgressPercent,
        int succeededCount,
        int failedCount,
        string statusMessage = "")
    {
        CompletedCount = completedCount;
        TotalCount = totalCount;
        CurrentItemName = currentItemName;
        OverallProgressPercent = overallProgressPercent;
        SucceededCount = succeededCount;
        FailedCount = failedCount;
        StatusMessage = statusMessage;
    }
}
