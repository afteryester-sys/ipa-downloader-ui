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
    private readonly MediaConverterService _mediaConverter = new();
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
    /// Common storage container names on Android devices.
    /// Probing these prefixes allows discovering media whether the user picks the phone root
    /// or a specific internal storage container.
    /// </summary>
    private static readonly string[] StoragePrefixes =
    [
        "",
        "Internal storage",
        "Internal shared storage",
        "Внутренний накопитель",
        "Внутренняя память",
        "Phone",
        "Память телефона",
        "SD card",
        "SD-карта"
    ];

    /// <summary>
    /// Standard well-known Android media directory paths relative to internal storage root.
    /// Probing these explicitly wakes up MTP drivers and populates lazy directory listings.
    /// </summary>
    private static readonly string[] BaseWellKnownAndroidPaths =
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

    private static readonly string[] WellKnownAndroidPaths = BuildAllProbingPaths();

    private static string[] BuildAllProbingPaths()
    {
        var list = new List<string>(BaseWellKnownAndroidPaths);
        foreach (var prefix in StoragePrefixes)
        {
            if (string.IsNullOrEmpty(prefix)) continue;
            foreach (var rel in BaseWellKnownAndroidPaths)
            {
                list.Add($"{prefix}/{rel}");
            }
        }
        return list.ToArray();
    }

    private static readonly Regex DateInFileNameRegex = new(
        @"(?:IMG_|VID_|PHOTO_|Screenshot_|Screen_|PXL_|\b)(20\d{2})[-_]?(0[1-9]|1[0-2])[-_]?(0[1-9]|[12]\d|3[01])(?:[-_](\d{2})(\d{2})(\d{2}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int ReportBatchSize = 32;

    public static bool IsPhoto(string path) => PhotoExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => IsPhoto(path) || IsVideo(path);

    /// <summary>
    /// Checks if a path is a valid filesystem directory or accessible via Windows Shell namespace.
    /// Supports virtual shell paths like "This PC\Galaxy S21" and localized names.
    /// </summary>
    public static bool IsSourceAccessible(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Directory.Exists(path)) return true;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var folder = ResolveShellFolder(path);
                if (folder is not null) return true;
            }
            catch
            {
                // Non-fatal
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a folder via Windows Shell COM. Handles both filesystem paths, GUIDs, and
    /// virtual shell paths returned by Windows folder pickers (e.g. "This PC\Galaxy S21\Internal storage").
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static dynamic? ResolveShellFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            // 1. Direct NameSpace (works for regular directories, drive letters, GUIDs)
            dynamic? folder = shell.NameSpace(path);
            if (folder is not null) return folder;

            // 2. My Computer (17 / ssfDRIVES) traversal for virtual shell paths (MTP, portable devices)
            dynamic? myComputer = shell.NameSpace(17);
            if (myComputer is null) return null;

            var normalized = path.Replace('/', '\\').Trim();
            var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            // Strip virtual roots like "This PC", "Этот компьютер", "Мой компьютер", etc.
            var startIndex = 0;
            var first = parts[0].Trim();
            if (first.Equals("This PC", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("Этот компьютер", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("Мой компьютер", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("My Computer", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("Компьютер", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("Computer", StringComparison.OrdinalIgnoreCase))
            {
                startIndex = 1;
            }

            if (startIndex >= parts.Length) return myComputer;

            dynamic current = myComputer;
            for (var p = startIndex; p < parts.Length; p++)
            {
                var targetPart = parts[p].Trim();
                dynamic? next = null;
                dynamic? items = current.Items();
                if (items is null) return null;

                int count = items.Count;
                // First pass: exact match on Name or Path
                for (var i = 0; i < count; i++)
                {
                    dynamic? item = items.Item(i);
                    if (item is null) continue;
                    string name = item.Name ?? "";
                    string itemPath = item.Path ?? "";
                    string fileName = Path.GetFileName(itemPath);

                    if (string.Equals(name, targetPart, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(itemPath, targetPart, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, targetPart, StringComparison.OrdinalIgnoreCase))
                    {
                        next = item.GetFolder;
                        if (next is not null) break;
                    }
                }

                // Second pass: contains / prefix match if exact match not found
                if (next is null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        dynamic? item = items.Item(i);
                        if (item is null) continue;
                        string name = item.Name ?? "";
                        string itemPath = item.Path ?? "";
                        string fileName = Path.GetFileName(itemPath);

                        if (name.Contains(targetPart, StringComparison.OrdinalIgnoreCase) ||
                            targetPart.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                            fileName.Contains(targetPart, StringComparison.OrdinalIgnoreCase) ||
                            targetPart.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            next = item.GetFolder;
                            if (next is not null) break;
                        }
                    }
                }

                if (next is null) return null;
                current = next;
            }

            return current;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates all connected media sources: MTP portable devices (Android phones, tablets, cameras)
    /// via Windows Shell namespace (ssfDRIVES / 17), and physical drives (flash drives, external HDDs, local drives)
    /// via DriveInfo.
    /// </summary>
    public static List<MediaExportSourceDevice> GetAvailableSources(Func<string, string>? loc = null)
    {
        string GetText(string key, string fallback) => loc?.Invoke(key) ?? fallback;
        var result = new List<MediaExportSourceDevice>();

        if (OperatingSystem.IsWindows())
        {
            // 1. Enumerate Shell ssfDRIVES (17) for MTP Android phones and portable devices
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is not null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell is not null)
                    {
                        dynamic? myComputer = shell.NameSpace(17);
                        if (myComputer is not null)
                        {
                            dynamic? items = myComputer.Items();
                            if (items is not null)
                            {
                                int count = items.Count;
                                for (int i = 0; i < count; i++)
                                {
                                    try
                                    {
                                        dynamic? item = items.Item(i);
                                        if (item is null) continue;
                                        string name = item.Name ?? "";
                                        string path = item.Path ?? "";
                                        bool isFolder = false;
                                        try { isFolder = item.IsFolder; } catch { }

                                        if (string.IsNullOrWhiteSpace(name)) continue;

                                        // Skip user profile folders (Downloads, Documents, Desktop, Pictures, Videos, etc.)
                                        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                                        if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                                        {
                                            continue;
                                        }

                                        // Skip standard drive letters (C:\, D:\, etc.) — DriveInfo handles them with accurate volume metadata
                                        bool isDriveLetter = Regex.IsMatch(path, @"^[a-zA-Z]:\\?$");

                                        // If it's not a drive letter, not in user profile, and is a folder:
                                        // It is a portable device (Android phone, tablet, camera)!
                                        if (!isDriveLetter && isFolder)
                                        {
                                            dynamic? deviceFolder = null;
                                            try { deviceFolder = item.GetFolder; } catch { }

                                            var subStorages = new List<string>();
                                            if (deviceFolder is not null)
                                            {
                                                try
                                                {
                                                    dynamic? subItems = deviceFolder.Items();
                                                    if (subItems is not null)
                                                    {
                                                        int subCount = subItems.Count;
                                                        for (int s = 0; s < subCount; s++)
                                                        {
                                                            try
                                                            {
                                                                dynamic? subItem = subItems.Item(s);
                                                                if (subItem is not null)
                                                                {
                                                                    bool isSubFolder = false;
                                                                    try { isSubFolder = subItem.IsFolder; } catch { }
                                                                    if (isSubFolder)
                                                                    {
                                                                        string subName = (string?)subItem.Name ?? "";
                                                                        if (!string.IsNullOrWhiteSpace(subName))
                                                                        {
                                                                            subStorages.Add(subName);
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                }
                                                catch { }
                                            }

                                            string details = subStorages.Count > 0
                                                ? string.Join(", ", subStorages)
                                                : GetText("L.MediaExport.Device.MtpActive", "Подключено по MTP (файловая система)");

                                            // Add the phone root device
                                            result.Add(new MediaExportSourceDevice(
                                                Name: name,
                                                Path: name, // Using the device name resolves in ResolveShellFolder
                                                SourceType: MediaExportSourceType.AndroidPhone,
                                                Subtitle: GetText("L.MediaExport.Device.AndroidPhone", "Android-телефон / планшет"),
                                                Details: details,
                                                IconGlyph: "\uE8EA", // CellPhone icon in Segoe MDL2 Assets
                                                IsPhone: true,
                                                StorageContainers: subStorages
                                            ));

                                            // If multiple distinct storage containers exist (e.g. Internal storage, SD card),
                                            // also add them as individual selectable entries
                                            if (subStorages.Count > 1)
                                            {
                                                foreach (var sub in subStorages)
                                                {
                                                    result.Add(new MediaExportSourceDevice(
                                                        Name: $"{name} — {sub}",
                                                        Path: $"{name}\\{sub}",
                                                        SourceType: MediaExportSourceType.AndroidPhone,
                                                        Subtitle: GetText("L.MediaExport.Device.AndroidStorage", "Хранилище Android"),
                                                        Details: sub,
                                                        IconGlyph: "\uEDA2",
                                                        IsPhone: true
                                                    ));
                                                }
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        // Ignore transient COM read errors on individual items
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"media export: failed enumerating shell devices: {ex.Message}");
            }
        }

        // 2. Enumerate Physical Drives via DriveInfo
        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType == DriveType.CDRom) continue;

                var isRemovable = drive.DriveType == DriveType.Removable;
                var letter = drive.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? (isRemovable ? GetText("L.MediaExport.Device.UsbDrive", "USB-накопитель") : GetText("L.MediaExport.Device.LocalDisk", "Локальный диск"))
                    : drive.VolumeLabel;

                var freeGb = Math.Round((double)drive.AvailableFreeSpace / (1024 * 1024 * 1024), 1);
                var totalGb = Math.Round((double)drive.TotalSize / (1024 * 1024 * 1024), 1);

                var subtitle = isRemovable
                    ? GetText("L.MediaExport.Device.ExternalStorage", "Внешний накопитель / флешка")
                    : GetText("L.MediaExport.Device.InternalStorage", "Локальный диск");

                var details = $"{drive.DriveFormat} • {freeGb} ГБ свободно из {totalGb} ГБ";

                result.Add(new MediaExportSourceDevice(
                    Name: $"{label} ({letter})",
                    Path: drive.RootDirectory.FullName,
                    SourceType: isRemovable ? MediaExportSourceType.RemovableDrive : MediaExportSourceType.FixedDrive,
                    Subtitle: subtitle,
                    Details: details,
                    IconGlyph: "\uEDA2",
                    IsPhone: false
                ));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media export: failed enumerating drives: {ex.Message}");
        }

        return result;
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

            return BuildResultFromFiles(files, skippedJunk, locResolver);
        }, ct);

    /// <summary>
    /// Builds a <see cref="MediaExportScanResult"/> from an in-memory list of media files,
    /// generating folder, category, and date breakdowns dynamically.
    /// </summary>
    public MediaExportScanResult BuildResultFromFiles(
        IReadOnlyList<MediaExportFile> files,
        int skippedJunkCount,
        Func<string, string>? locResolver = null)
    {
        var rootGroup = locResolver?.Invoke("L.MediaExport.RootGroup") ?? "(корень)";
        var noDate = locResolver?.Invoke("L.MediaExport.NoDate") ?? "Без даты";

        var folderGroups = files
            .GroupBy(f => string.IsNullOrEmpty(f.GroupName) ? rootGroup : f.GroupName)
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
            .GroupBy(f => f.MediaDate?.ToString("yyyy-MM") ?? noDate)
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
            SkippedJunkCount = skippedJunkCount,
        };
    }

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
                    FileName = Path.GetFileName(filePath),
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
            var folder = ResolveShellFolder(root);
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

        dynamic? items = null;
        try { items = folder.Items(); } catch { return; }
        if (items is null) return;

        int count = 0;
        try { count = items.Count; } catch { count = 0; }

        // MTP devices can be slow to populate initially; retry up to 3 times if count is 0 to wake the driver
        int retries = 0;
        while (count == 0 && retries < 3)
        {
            Thread.Sleep(150);
            try
            {
                items = folder.Items();
                if (items is not null) count = items.Count;
            }
            catch { count = 0; }
            retries++;
        }

        if (items is null || count == 0) return;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            dynamic? item = null;
            try { item = items.Item(i); } catch { continue; }
            if (item is null) continue;

            bool isFolder = false;
            try { isFolder = item.IsFolder; } catch { continue; }

            string name = "";
            try { name = item.Name ?? ""; } catch {}
            string itemPath = "";
            try { itemPath = item.Path ?? name; } catch { itemPath = name; }

            if (isFolder)
            {
                // Skip system caches and protected Android folders to avoid permission errors and slowdowns
                if (name.StartsWith('.') ||
                    name.Equals(".thumbnails", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".trash", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("lost.dir", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                    (relPath.EndsWith("Android", StringComparison.OrdinalIgnoreCase) &&
                     (name.Equals("data", StringComparison.OrdinalIgnoreCase) || name.Equals("obb", StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                var nextRel = string.IsNullOrEmpty(relPath) ? name : $"{relPath}/{name}";
                dynamic? subFolder = null;
                try { subFolder = item.GetFolder; } catch {}
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
                    size = 0;
                }

                if (size > 0 && size < minFileSizeBytes)
                {
                    skippedJunk++;
                    continue;
                }

                // If relPath starts with a storage container prefix ("Internal storage/DCIM"), strip it for clean group naming
                var cleanRel = relPath;
                foreach (var prefix in StoragePrefixes)
                {
                    if (!string.IsNullOrEmpty(prefix) && cleanRel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanRel = cleanRel[(prefix.Length + 1)..];
                        break;
                    }
                }

                var groupName = string.IsNullOrEmpty(cleanRel) ? "" : cleanRel.Split('/')[0];
                var cat = DetectCategory(name, isVid, groupName, locResolver);
                var date = DetectMediaDate(name);

                files.Add(new MediaExportFile
                {
                    FullPath = itemPath,
                    FileName = name,
                    GroupName = groupName,
                    IsVideo = isVid,
                    SizeBytes = size,
                    Category = cat,
                    MediaDate = date,
                    ShellItem = item,
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
    /// to <paramref name="organization"/>, with optional HEIC to JPEG conversion.
    /// </summary>
    public Task<int> CopyAsync(
        MediaExportScanResult scan,
        string destinationRoot,
        MediaExportOrganization organization,
        string rootGroupLabel,
        IProgress<MediaExportProgress>? progress = null,
        HeicExportConversionMode conversionMode = HeicExportConversionMode.Disabled,
        bool deleteOriginalHeic = false,
        int jpegQuality = 92,
        Action? onDeviceTransferCompleted = null,
        CancellationToken ct = default)
        => Task.Run(async () =>
        {
            Directory.CreateDirectory(destinationRoot);

            var done = 0;
            var total = scan.Files.Count;
            var hasHeic = false;

            foreach (var file in scan.Files)
            {
                ct.ThrowIfCancellationRequested();

                var name = !string.IsNullOrWhiteSpace(file.FileName)
                    ? file.FileName
                    : Path.GetFileName(file.FullPath);
                if (string.IsNullOrEmpty(name)) name = "media_item";

                progress?.Report(new MediaExportProgress(done, total, name));

                string targetSubfolder = organization switch
                {
                    MediaExportOrganization.ByCategory => SafeFolderName(string.IsNullOrWhiteSpace(file.Category) ? "Other" : file.Category),
                    MediaExportOrganization.ByDate => file.MediaDate?.ToString("yyyy-MM") ?? SafeFolderName(rootGroupLabel == "(корень)" ? "Без даты" : "No date"),
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
                    bool isHeic = MediaConverterService.IsHeic(name);
                    if (isHeic) hasHeic = true;

                    if (isHeic && conversionMode == HeicExportConversionMode.DuringTransfer && File.Exists(file.FullPath))
                    {
                        var destName = Path.GetFileNameWithoutExtension(name) + ".jpg";
                        var destination = MakeUniquePath(Path.Combine(targetDir, destName));
                        if (MediaConverterService.ConvertImageToJpeg(file.FullPath, destination, jpegQuality))
                        {
                            done++;
                        }
                    }
                    else
                    {
                        var destination = MakeUniquePath(Path.Combine(targetDir, name));

                        if (File.Exists(file.FullPath))
                        {
                            File.Copy(file.FullPath, destination, overwrite: false);
                            done++;
                        }
                        else if (OperatingSystem.IsWindows())
                        {
                            // Try Shell COM copy for virtual MTP items
                            if (CopyViaShell(file.ShellItem ?? file.FullPath, targetDir, name))
                            {
                                done++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"media export: copy failed for '{file.FullPath}': {ex.Message}");
                }
            }

            if (conversionMode == HeicExportConversionMode.PostTransfer && hasHeic)
            {
                onDeviceTransferCompleted?.Invoke();
                progress?.Report(new MediaExportProgress(done, total, "Конвертация HEIC в JPEG..."));
                await _mediaConverter.ConvertPostExportFolderAsync(
                    destinationRoot,
                    deleteOriginalHeic,
                    jpegQuality,
                    null,
                    ct).ConfigureAwait(false);
            }

            progress?.Report(new MediaExportProgress(done, total, ""));
            return done;
        }, ct);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool CopyViaShell(object shellItemOrPath, string targetDir, string expectedFileName)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            dynamic? destFolder = shell.NameSpace(targetDir);
            if (destFolder is null) return false;

            // 4 = do not show progress dialog, 16 = respond with "Yes to All" for any dialog, 512 = do not confirm creation of new directory
            destFolder.CopyHere(shellItemOrPath, 4 | 16 | 512 | 1024);

            // Synchronize sequentially to avoid saturating the single-threaded MTP USB endpoint
            var targetFile = Path.Combine(targetDir, expectedFileName);
            var startWait = DateTime.UtcNow;
            while (!File.Exists(targetFile) && (DateTime.UtcNow - startWait).TotalSeconds < 10)
            {
                Thread.Sleep(30);
            }

            // If file appeared, wait until size is stable (transfer complete)
            if (File.Exists(targetFile))
            {
                long lastLen = -1;
                int stableTicks = 0;
                var copyWaitLimit = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < copyWaitLimit)
                {
                    try
                    {
                        var info = new FileInfo(targetFile);
                        if (info.Exists)
                        {
                            if (info.Length > 0 && info.Length == lastLen)
                            {
                                stableTicks++;
                                if (stableTicks >= 2) break;
                            }
                            else
                            {
                                lastLen = info.Length;
                                stableTicks = 0;
                            }
                        }
                    }
                    catch
                    {
                        // File locked by Shell during copy
                    }
                    Thread.Sleep(50);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media export: shell copy failed for '{expectedFileName}': {ex.Message}");
            return false;
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
