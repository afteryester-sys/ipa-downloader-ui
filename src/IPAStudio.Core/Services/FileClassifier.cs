namespace IPAStudio.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;

/// <summary>
/// High-performance classifier that inspects file extensions and metadata to categorize
/// transfer payloads and describe their UI presentation.
/// </summary>
public static class FileClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".3gp", ".3g2",
        ".ts", ".mts", ".m2ts", ".wmv", ".flv", ".f4v", ".asf", ".vob", ".ogv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".aiff", ".aif", ".alac",
        ".ogg", ".oga", ".opus", ".m4r", ".wma", ".mid", ".midi", ".ac3", ".ape"
    };

    private static readonly HashSet<string> BookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".fb2", ".ibooks",
        ".cbr", ".cbz", ".djvu", ".djv"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".pages", ".numbers", ".key", ".keynote",
        ".txt", ".rtf", ".md", ".markdown", ".csv", ".tsv",
        ".json", ".xml", ".yaml", ".yml", ".html", ".htm"
    };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp",
        ".tif", ".tiff", ".bmp", ".dng", ".cr2", ".cr3", ".nef", ".arw",
        ".rw2", ".orf", ".pef", ".svg", ".ico", ".aae"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".cab", ".iso"
    };

    private static readonly HashSet<string> ContactExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vcf", ".vcard"
    };

    private static readonly HashSet<string> CameraRollExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp",
        ".tif", ".tiff", ".bmp", ".dng", ".cr2", ".cr3", ".nef", ".arw",
        ".mov", ".mp4", ".m4v", ".3gp"
    };

    /// <summary>
    /// Whether a file can be imported directly into the native iOS Camera Roll (Photos library).
    /// </summary>
    public static bool IsCameraRollMedia(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && CameraRollExtensions.Contains(ext);
    }

    /// <summary>
    /// Detects the category of a file by its extension.
    /// </summary>
    public static FileCategory Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return FileCategory.Other;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return FileCategory.Other;

        if (string.Equals(ext, ".ipa", StringComparison.OrdinalIgnoreCase))
            return FileCategory.App;

        if (ContactExtensions.Contains(ext)) return FileCategory.Contact;
        if (VideoExtensions.Contains(ext)) return FileCategory.Video;
        if (AudioExtensions.Contains(ext)) return FileCategory.Audio;
        if (BookExtensions.Contains(ext)) return FileCategory.Book;
        if (DocumentExtensions.Contains(ext)) return FileCategory.Document;
        if (PhotoExtensions.Contains(ext)) return FileCategory.Photo;
        if (ArchiveExtensions.Contains(ext)) return FileCategory.Archive;

        return FileCategory.Other;
    }

    /// <summary>
    /// Returns the MDL2 icon glyph representing the category in WPF UI.
    /// </summary>
    public static string GetGlyph(FileCategory category) => category switch
    {
        FileCategory.App => "\uE7BA",      // AllApps / Package
        FileCategory.Contact => "\uE77B",  // Contact / Person
        FileCategory.Video => "\uE714",    // Video
        FileCategory.Audio => "\uE8D6",    // Audio
        FileCategory.Book => "\uE82D",     // Library / Book
        FileCategory.Document => "\uE8A5", // Document
        FileCategory.Photo => "\uEB9F",    // Photo2
        FileCategory.Archive => "\uF012",  // ZipFolder
        _ => "\uE8A5",                     // Generic Document
    };

    /// <summary>
    /// Returns the localized display name for a file category.
    /// </summary>
    public static string GetCategoryDisplayName(FileCategory category) => category switch
    {
        FileCategory.App => Loc.Get("L.QuickTransfer.TypeApp"),
        FileCategory.Contact => Loc.Get("L.QuickTransfer.TypeContact"),
        FileCategory.Video => Loc.Get("L.QuickTransfer.TypeVideo"),
        FileCategory.Audio => Loc.Get("L.QuickTransfer.TypeAudio"),
        FileCategory.Book => Loc.Get("L.QuickTransfer.TypeBook"),
        FileCategory.Document => Loc.Get("L.QuickTransfer.TypeDocument"),
        FileCategory.Photo => Loc.Get("L.QuickTransfer.TypeImage"),
        FileCategory.Archive => Loc.Get("L.QuickTransfer.TypeArchive"),
        _ => Loc.Get("L.QuickTransfer.TypeOther"),
    };

    /// <summary>
    /// Expands dropped paths into individual files by traversing directories recursively.
    /// </summary>
    public static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        if (paths is null) yield break;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    yield return file;
                }
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// Creates a complete transfer payload description for a file.
    /// </summary>
    public static TransferPayload Describe(string path, string? targetAppName = null)
    {
        var category = Classify(path);
        var glyph = GetGlyph(category);
        var categoryName = GetCategoryDisplayName(category);
        var fileName = Path.GetFileName(path);
        long fileSize = 0;

        try
        {
            if (File.Exists(path))
            {
                fileSize = new FileInfo(path).Length;
            }
        }
        catch { }

        var destination = category == FileCategory.App
            ? Loc.Get("L.QuickTransfer.Apps")
            : (!string.IsNullOrWhiteSpace(targetAppName) ? targetAppName : Loc.Get("L.QuickTransfer.NoDestination"));

        return new TransferPayload(
            path,
            fileName,
            fileSize,
            category,
            categoryName,
            glyph,
            destination);
    }
}
