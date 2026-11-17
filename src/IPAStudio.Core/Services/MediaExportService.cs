using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Finds photos and videos anywhere under a chosen root — an external drive, USB stick,
/// or an Android phone connected via file transfer / MTP — and copies them onto the PC.
/// 
/// Features:
/// - Filtering: All media, Photos only, Videos only.
/// - Organization: By detected category (Camera, Screenshots, Selfies, Videos, Other),
///   by source folders (DCIM, Download...), by date (Year-Month), or flat in a single folder.
/// - Active Android / MTP Probing: Probes well-known Android directory paths (DCIM/Camera,
///   Pictures/Screenshots, Movies, etc.), handles delayed folder listing, and supports
///   virtual Windows Shell namespace paths.
/// </summary>
public sealed class MediaExportService
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".dng", ".cr2", ".nef", ".arw", ".raw", ".svg",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".3gp", ".3g2", ".wmv",
        ".flv", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg", ".ts",
    };

    /// <summary>
    /// Standard well-known Android media directory paths relative to internal storage root.
    /// Probing these explicitly wakes up MTP drivers and populates lazy directory listings.
    /// </summary>
    private static readonly string[] WellKnownAndroidPaths =
    [
        "DCIM",
        "DCIM/Camera",
        "DCIM/100ANDRO",
        "DCIM/100MEDIA",
        "DCIM/Screenshots",
        "DCIM/Restored",
        "Pictures",
        "Pictures/Screenshots",
        "Pictures/Telegram",
        "Pictures/WhatsApp",
        "Pictures/Instagram",
        "Pictures/VK",
        "Pictures/Twitter",
        "Pictures/Viber",
        "Pictures/Messenger",
        "Pictures/Facebook",
        "Movies",
        "Movies/Instagram",
        "Movies/TikTok",
        "Movies/Telegram",
        "Movies/WhatsApp",
        "Download",
        "Downloads",
        "Android/media",
        "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images",
        "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video",
        "WhatsApp/Media/WhatsApp Images",
        "WhatsApp/Media/WhatsApp Video",
        "Telegram",
        "Telegram/Telegram Images",
        "Telegram/Telegram Video",
        "Snapseed",
        "Documents",
    ];

    private static readonly Regex DateInFileNameRegex = new(
        @"(?:IMG_|VID_|PHOTO_|Screenshot_|Screen_|PXL_|\b)(20\d{2})[-_]?(0[1-9]|1[0-2])[-_]?(0[1-9]|[12]\d|3[01])(?:[-_](\d{2})(\d{2})(\d{2}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int ReportBatchSize = 32;

    public static bool IsPhoto(string path) => PhotoExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => IsPhoto(path) || IsVideo(path);

    /// <summary>
    /// Checks if a path is a valid filesystem directory or accessible via Windows Shell namespace.
    /// </summary>
    public static bool IsSourceAccessible(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Directory.Exists(path)) return true;

        if (OperatingSystem.IsWindows())
        {
            // Check Windows Shell namespace accessibility
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is not null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell is not null)
                    {
                        dynamic? folder = shell.NameSpace(path);
                        if (folder is not null) return true;
                    }
                }
            }
            catch
            {
                // Non-fatal
            }
        }

        return false;
    }

    /// <summary>
    /// Detects the semantic category of a media file:
    /// - Camera photos (DCIM, Camera, IMG_, DSC_, PXL_)
    /// - Screenshots (Screenshot_, Screen_, Screenshots folder)
    /// - Selfies (Front camera, Selfie, FRONT_, PORTRAIT_)
    /// - Videos (all video formats)
    /// - Other (downloads, messengers, misc)
    /// </summary>
    public static string DetectCategory(string fullPath, bool isVideo, string groupName, Func<string, string>? loc = null)
    {
        string GetText(string key, string fallback) => loc?.Invoke(key) ?? fallback;

        if (isVideo)
        {
            return GetText("L.MediaExport.Category.Videos", "Видеозаписи");
        }

        var fileName = Path.GetFileName(fullPath);
        var dirName = Path.GetDirectoryName(fullPath) ?? "";
        var normalizedDir = dirName.Replace('\\', '/');

        // 1. Screenshot detection
        if (fileName.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Screen_", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Screen-", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Снимок экрана", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Скриншот", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("screencap", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("Screenshots", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("Снимки экрана", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("Скриншоты", StringComparison.OrdinalIgnoreCase))
        {
            return GetText("L.MediaExport.Category.Screenshots", "Скриншоты");
        }

        // 2. Selfie / Front Camera detection
        if (fileName.Contains("FRONT", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Selfie", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Селфи", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("SEL_", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("PORTRAIT", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("Selfie", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("Селфи", StringComparison.OrdinalIgnoreCase))
        {
            return GetText("L.MediaExport.Category.Selfies", "Селфи");
        }

        // 3. Camera photos
        if (normalizedDir.Contains("/DCIM", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("DCIM/", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.EndsWith("/DCIM", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("/Camera", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("100ANDRO", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("100MEDIA", StringComparison.OrdinalIgnoreCase) ||
            normalizedDir.Contains("100APPLE", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("IMG_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("DSC_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("PXL_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("MVIMG_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("PHOTO_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("SAM_", StringComparison.OrdinalIgnoreCase))
        {
            return GetText("L.MediaExport.Category.Camera", "Снимки камеры");
        }

        return GetText("L.MediaExport.Category.Other", "Прочее");
    }

    /// <summary>
    /// Parses or determines the media timestamp (from filename pattern or filesystem attributes).
    /// </summary>
    public static DateTime? DetectMediaDate(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        var match = DateInFileNameRegex.Match(fileName);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var year) &&
                int.TryParse(match.Groups[2].Value, out var month) &&
                int.TryParse(match.Groups[3].Value, out var day))
            {
                try
                {
                    int hour = 0, min = 0, sec = 0;
                    if (match.Groups[4].Success && int.TryParse(match.Groups[4].Value, out var h)) hour = h;
                    if (match.Groups[5].Success && int.TryParse(match.Groups[5].Value, out var m)) min = m;
                    if (match.Groups[6].Success && int.TryParse(match.Groups[6].Value, out var s)) sec = s;

                    return new DateTime(year, month, day, Math.Clamp(hour, 0, 23), Math.Clamp(min, 0, 59), Math.Clamp(sec, 0, 59));
                }
                catch
                {
                    // Fall back to filesystem date
                }
            }
        }

        try
        {
            if (File.Exists(fullPath))
            {
                var writeTime = File.GetLastWriteTime(fullPath);
                if (writeTime.Year >= 2000 && writeTime <= DateTime.Now.AddDays(2))
                {
                    return writeTime;
                }

                var createTime = File.GetCreationTime(fullPath);
                if (createTime.Year >= 2000 && createTime <= DateTime.Now.AddDays(2))
                {
                    return createTime;
                }
            }
        }
        catch
        {
            // Non-fatal
        }

        return null;
    }

    /// <summary>
    /// Probes and scans the chosen root directory for media files with active Android/MTP probing.
    /// </summary>
    public Task<MediaExportScanResult> ScanAsync(
        string root,
        long minFileSizeBytes,
        MediaExportFilterType filter = MediaExportFilterType.All,
        IProgress<string>? currentStatus = null,
        Func<string, string>? locResolver = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            var files = new List<MediaExportFile>();
            var skippedJunk = 0;
            var seen = 0;
            var lastReported = "";

            void Report(string status)
            {
                if (status != lastReported)
                {
                    lastReported = status;
                    currentStatus?.Report(status);
                }
            }

            // Check if root is accessible via regular filesystem
            if (Directory.Exists(root))
            {
                ScanFileSystemWithProbing(
                    root,
                    minFileSizeBytes,
                    filter,
                    files,
                    ref skippedJunk,
                    ref seen,
                    Report,
                    locResolver,
                    ct);
            }
            else if (OperatingSystem.IsWindows())
            {
                // Fallback to Shell COM (for MTP shell namespace paths)
                ScanShellWithProbing(
                    root,
                    minFileSizeBytes,
                    filter,
                    files,
                    ref skippedJunk,
                    ref seen,
                    Report,
                    locResolver,
                    ct);
            }

            // Build breakdowns
            var folderGroups = files
                .GroupBy(f => string.IsNullOrEmpty(f.GroupName) ? "(корень)" : f.GroupName)
                .Select(g => new MediaExportGroup
                {
                    Name = g.Key,
                    PhotoCount = g.Count(f => !f.IsVideo),
                    VideoCount = g.Count(f => f.IsVideo),
                    TotalBytes = g.Sum(f => f.SizeBytes),
                })
                .OrderByDescending(g => g.Count)
                .ToList();

            var categoryGroups = files
                .GroupBy(f => f.Category)
                .Select(g => new MediaExportGroup
                {
                    Name = g.Key,
                    PhotoCount = g.Count(f => !f.IsVideo),
                    VideoCount = g.Count(f => f.IsVideo),
                    TotalBytes = g.Sum(f => f.SizeBytes),
                })
                .OrderByDescending(g => g.Count)
                .ToList();

            var dateGroups = files
                .GroupBy(f => f.MediaDate?.ToString("yyyy-MM") ?? "Unknown")
                .Select(g => new MediaExportGroup
                {
                    Name = g.Key,
                    PhotoCount = g.Count(f => !f.IsVideo),
                    VideoCount = g.Count(f => f.IsVideo),
                    TotalBytes = g.Sum(f => f.SizeBytes),
                })
                .OrderByDescending(g => g.Name)
                .ToList();

            return new MediaExportScanResult
            {
                Files = files,
                Groups = folderGroups,
                CategoryGroups = categoryGroups,
                DateGroups = dateGroups,
                SkippedJunkCount = skippedJunk,
            };
        }, ct);

    private void ScanFileSystemWithProbing(
        string root,
        long minFileSizeBytes,
        MediaExportFilterType filter,
        List<MediaExportFile> files,
        ref int skippedJunk,
        ref int seen,
        Action<string> report,
        Func<string, string>? locResolver,
        CancellationToken ct)
    {
        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirQueue = new Queue<string>();

        // 1. Queue the root directory
        dirQueue.Enqueue(root);
        visitedDirs.Add(Path.GetFullPath(root));

        // 2. Active Probing: Pre-probe well-known Android media folders to wake up MTP drivers
        report("Зондирование структуры устройства…");
        foreach (var relPath in WellKnownAndroidPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var candidate = Path.Combine(root, relPath);
                if (Directory.Exists(candidate))
                {
                    var fullCandidate = Path.GetFullPath(candidate);
                    if (visitedDirs.Add(fullCandidate))
                    {
                        dirQueue.Enqueue(candidate);
                    }
                }
            }
            catch
            {
                // Inaccessible folder: ignore
            }
        }

        // 3. BFS traversal
        while (dirQueue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentDir = dirQueue.Dequeue();

            var groupName = TopLevelGroup(root, currentDir);
            report(string.IsNullOrEmpty(groupName) ? Path.GetFileName(currentDir) : groupName);

            // Read files in current directory
            string[] subFiles;
            try
            {
                subFiles = Directory.GetFiles(currentDir);
            }
            catch
            {
                // Permission or transient MTP I/O delay: continue to other directories
                continue;
            }

            foreach (var filePath in subFiles)
            {
                ct.ThrowIfCancellationRequested();

                var isVid = IsVideo(filePath);
                var isPht = IsPhoto(filePath);

                if (!isVid && !isPht) continue;

                if (filter == MediaExportFilterType.PhotosOnly && isVid) continue;
                if (filter == MediaExportFilterType.VideosOnly && !isVid) continue;

                long size;
                try
                {
                    size = new FileInfo(filePath).Length;
                }
                catch
                {
                    continue;
                }

                if (size < minFileSizeBytes)
                {
                    skippedJunk++;
                    continue;
                }

                var cat = DetectCategory(filePath, isVid, groupName, locResolver);
                var date = DetectMediaDate(filePath);

                files.Add(new MediaExportFile
                {
                    FullPath = filePath,
                    GroupName = groupName,
                    IsVideo = isVid,
                    SizeBytes = size,
                    Category = cat,
                    MediaDate = date,
                });

                seen++;
                if (seen % ReportBatchSize == 0)
                {
                    report(groupName);
                }
            }

            // Discover and enqueue child directories
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fullSub = Path.GetFullPath(subDir);
                    if (visitedDirs.Add(fullSub))
                    {
                        dirQueue.Enqueue(subDir);
                    }
                }
                catch
                {
                    // Ignore inaccessible subdirectories
                }
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ScanShellWithProbing(
        string root,
        long minFileSizeBytes,
        MediaExportFilterType filter,
        List<MediaExportFile> files,
        ref int skippedJunk,
        ref int seen,
        Action<string> report,
        Func<string, string>? locResolver,
        CancellationToken ct)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            dynamic? folder = shell.NameSpace(root);
            if (folder is null) return;

            report("Зондирование MTP-устройства…");
            CrawlShellFolder(folder, root, "", minFileSizeBytes, filter, files, ref skippedJunk, ref seen, report, locResolver, 0, ct);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media export: shell scanning failed: {ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void CrawlShellFolder(
        dynamic folder,
        string rootPath,
        string relPath,
        long minFileSizeBytes,
        MediaExportFilterType filter,
        List<MediaExportFile> files,
        ref int skippedJunk,
        ref int seen,
        Action<string> report,
        Func<string, string>? locResolver,
        int depth,
        CancellationToken ct)
    {
        if (depth > 12) return; // Prevent excessive recursion

        dynamic? items = folder.Items();
        if (items is null) return;

        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            dynamic? item = items.Item(i);
            if (item is null) continue;

            bool isFolder = item.IsFolder;
            string name = item.Name ?? "";
            string itemPath = item.Path ?? name;

            if (isFolder)
            {
                var nextRel = string.IsNullOrEmpty(relPath) ? name : $"{relPath}/{name}";
                dynamic? subFolder = item.GetFolder;
                if (subFolder is not null)
                {
                    report(string.IsNullOrEmpty(relPath) ? name : relPath);
                    CrawlShellFolder(subFolder, rootPath, nextRel, minFileSizeBytes, filter, files, ref skippedJunk, ref seen, report, locResolver, depth + 1, ct);
                }
            }
            else
            {
                var isVid = IsVideo(name);
                var isPht = IsPhoto(name);
                if (!isVid && !isPht) continue;

                if (filter == MediaExportFilterType.PhotosOnly && isVid) continue;
                if (filter == MediaExportFilterType.VideosOnly && !isVid) continue;

                long size = 0;
                try
                {
                    size = (long)item.Size;
                }
                catch
                {
                    size = 100_000; // estimated size if Shell doesn't expose it
                }

                if (size < minFileSizeBytes)
                {
                    skippedJunk++;
                    continue;
                }

                var groupName = string.IsNullOrEmpty(relPath) ? "" : relPath.Split('/')[0];
                var cat = DetectCategory(name, isVid, groupName, locResolver);
                var date = DetectMediaDate(name);

                files.Add(new MediaExportFile
                {
                    FullPath = itemPath,
                    GroupName = groupName,
                    IsVideo = isVid,
                    SizeBytes = size,
                    Category = cat,
                    MediaDate = date,
                });

                seen++;
                if (seen % ReportBatchSize == 0)
                {
                    report(groupName);
                }
            }
        }
    }

    /// <summary>
    /// Copies every matched file to <paramref name="destinationRoot"/> organized according
    /// to <paramref name="organization"/>.
    /// </summary>
    public Task<int> CopyAsync(
        MediaExportScanResult scan,
        string destinationRoot,
        MediaExportOrganization organization,
        string rootGroupLabel,
        IProgress<MediaExportProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            Directory.CreateDirectory(destinationRoot);

            var done = 0;
            var total = scan.Files.Count;

            foreach (var file in scan.Files)
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(file.FullPath);
                if (string.IsNullOrEmpty(name)) name = "media_item";

                progress?.Report(new MediaExportProgress(done, total, name));

                string targetSubfolder = organization switch
                {
                    MediaExportOrganization.ByCategory => SafeFolderName(string.IsNullOrWhiteSpace(file.Category) ? "Other" : file.Category),
                    MediaExportOrganization.ByDate => file.MediaDate?.ToString("yyyy-MM") ?? "Unknown",
                    MediaExportOrganization.ByFolder => SafeFolderName(string.IsNullOrEmpty(file.GroupName) ? rootGroupLabel : file.GroupName),
                    MediaExportOrganization.SingleFolder => "",
                    _ => ""
                };

                var targetDir = string.IsNullOrEmpty(targetSubfolder)
                    ? destinationRoot
                    : Path.Combine(destinationRoot, targetSubfolder);

                try
                {
                    Directory.CreateDirectory(targetDir);
                    var destination = MakeUniquePath(Path.Combine(targetDir, name));

                    if (File.Exists(file.FullPath))
                    {
                        File.Copy(file.FullPath, destination, overwrite: false);
                    }
                    else if (OperatingSystem.IsWindows())
                    {
                        // Try Shell COM copy for virtual MTP items
                        CopyViaShell(file.FullPath, targetDir);
                    }
                    done++;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"media export: copy failed for '{file.FullPath}': {ex.Message}");
                }
            }

            progress?.Report(new MediaExportProgress(done, total, ""));
            return done;
        }, ct);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CopyViaShell(string shellItemPath, string targetDir)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            dynamic? destFolder = shell.NameSpace(targetDir);
            if (destFolder is null) return;

            // 4 = do not show progress dialog, 16 = respond with "Yes to All" for any dialog, 512 = do not confirm creation of new directory
            destFolder.CopyHere(shellItemPath, 4 | 16 | 512 | 1024);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media export: shell copy failed for '{shellItemPath}': {ex.Message}");
        }
    }

    private static string TopLevelGroup(string root, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(root, fullPath);
            if (string.IsNullOrEmpty(relative) || relative == ".") return "";
            var separator = relative.IndexOfAny(['\\', '/']);
            return separator < 0 ? relative : relative[..separator];
        }
        catch
        {
            return "";
        }
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
