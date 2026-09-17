using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Finds photos and videos anywhere under a chosen root — a hard drive, a USB stick, or an
/// Android phone mounted as a plain file system — and copies them onto the PC.
///
/// Deliberately works over <see cref="System.IO"/> rather than any device-specific protocol:
/// unlike the iPhone Camera Roll (<see cref="PhotoService"/>, reached over AFC), an Android
/// phone in file-transfer mode and any external drive already show up as an ordinary path, so
/// a recursive folder walk is both the simplest and the most general way to reach either.
/// </summary>
public sealed class MediaExportService
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".dng", ".cr2", ".nef", ".arw", ".raw",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".3gp", ".3g2", ".wmv",
        ".flv", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg",
    };

    /// <summary>How many files to walk before reporting progress, for the same reason the
    /// device listing batches its own updates: reporting on every single file would spend far
    /// more time marshalling to the UI thread than the scan itself.</summary>
    private const int ReportBatchSize = 64;

    public static bool IsPhoto(string path) => PhotoExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => IsPhoto(path) || IsVideo(path);

    /// <summary>
    /// Walks every file under <paramref name="root"/> and returns the media it found, grouped
    /// by the top-level folder each file sits under (so "DCIM" and "Download" show up as
    /// separate rows even though both live several levels deep on some phones).
    ///
    /// Nothing is copied here — this is the separate "look first" pass the export step reads
    /// its list from, so the user sees what is out there before anything touches the PC disk.
    /// </summary>
    public Task<MediaExportScanResult> ScanAsync(
        string root,
        long minFileSizeBytes,
        IProgress<string>? currentGroup = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            var files = new List<MediaExportFile>();
            var groups = new Dictionary<string, (int Photos, int Videos, long Bytes)>(StringComparer.OrdinalIgnoreCase);
            var skippedJunk = 0;
            var seen = 0;
            var lastReportedGroup = "";

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            };

            IEnumerable<string> walk;
            try
            {
                walk = Directory.EnumerateFiles(root, "*", options);
            }
            catch (Exception)
            {
                // The root itself could not be opened (drive ejected, permission denied on the
                // very first call). Reported as an empty result rather than throwing, so the
                // page can say "nothing found" instead of crashing the scan.
                return new MediaExportScanResult { Files = files, Groups = [], SkippedJunkCount = 0 };
            }

            foreach (var path in walk)
            {
                ct.ThrowIfCancellationRequested();

                if (!IsMedia(path)) continue;

                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // Vanished or became unreadable between the listing and the stat — not
                    // worth failing the whole scan over one file.
                    continue;
                }

                if (size < minFileSizeBytes)
                {
                    // Below the threshold: an app icon, a thumbnail sidecar, or a broken
                    // 0-byte placeholder rather than an actual photo or video.
                    skippedJunk++;
                    continue;
                }

                var groupName = TopLevelGroup(root, path);
                var isVideo = IsVideo(path);

                files.Add(new MediaExportFile
                {
                    FullPath = path,
                    GroupName = groupName,
                    IsVideo = isVideo,
                    SizeBytes = size,
                });

                if (!groups.TryGetValue(groupName, out var tally)) tally = (0, 0, 0);
                tally = isVideo ? (tally.Photos, tally.Videos + 1, tally.Bytes + size)
                                 : (tally.Photos + 1, tally.Videos, tally.Bytes + size);
                groups[groupName] = tally;

                seen++;
                if (seen % ReportBatchSize == 0 && groupName != lastReportedGroup)
                {
                    lastReportedGroup = groupName;
                    currentGroup?.Report(groupName);
                }
            }

            var groupRows = groups
                .Select(kv => new MediaExportGroup
                {
                    Name = kv.Key,
                    PhotoCount = kv.Value.Photos,
                    VideoCount = kv.Value.Videos,
                    TotalBytes = kv.Value.Bytes,
                })
                .OrderByDescending(g => g.Count)
                .ToList();

            return new MediaExportScanResult
            {
                Files = files,
                Groups = groupRows,
                SkippedJunkCount = skippedJunk,
            };
        }, ct);

    /// <summary>
    /// Copies every file the scan found to <paramref name="destinationRoot"/>, either flat or
    /// mirrored into one sub-folder per source group, and returns how many were copied.
    /// </summary>
    public Task<int> CopyAsync(
        MediaExportScanResult scan,
        string destinationRoot,
        MediaExportCopyMode mode,
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
                progress?.Report(new MediaExportProgress(done, total, name));

                var targetDir = mode == MediaExportCopyMode.ByFolder
                    ? Path.Combine(destinationRoot, SafeFolderName(
                        string.IsNullOrEmpty(file.GroupName) ? rootGroupLabel : file.GroupName))
                    : destinationRoot;

                try
                {
                    Directory.CreateDirectory(targetDir);
                    var destination = MakeUniquePath(Path.Combine(targetDir, name));
                    File.Copy(file.FullPath, destination, overwrite: false);
                    done++;
                }
                catch (Exception)
                {
                    // One locked or since-deleted file must not abort a batch of hundreds;
                    // it is simply left out of the count the caller reports.
                }
            }

            progress?.Report(new MediaExportProgress(done, total, ""));
            return done;
        }, ct);

    /// <summary>
    /// The first path segment under <paramref name="root"/>, e.g. "DCIM" for
    /// "E:\DCIM\100ANDRO\IMG_0001.jpg". Empty for a file that sits directly in the root, which
    /// the UI and the copy step both treat as its own group.
    /// </summary>
    private static string TopLevelGroup(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var separator = relative.IndexOfAny(['\\', '/']);
        return separator < 0 ? "" : relative[..separator];
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
