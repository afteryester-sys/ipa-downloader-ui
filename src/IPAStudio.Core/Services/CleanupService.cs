using System.Diagnostics;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>One group of disposable files, as measured by <see cref="CleanupService.ScanAsync"/>.</summary>
public sealed class CleanupGroup
{
    /// <summary>Resource key for the display name. Core never resolves it; the UI does.</summary>
    public required string Key { get; init; }

    /// <summary>Folder (or single file) this group covers, shown as a tooltip.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Files found, captured during the scan and reused when deleting. Kept rather than
    /// re-enumerated so the size shown in the confirmation is the size actually removed,
    /// and so progress can be reported against a known total.
    /// </summary>
    public required IReadOnlyList<string> Files { get; init; }

    public long Bytes { get; init; }
    public int FileCount => Files.Count;
}

/// <summary>Result of a scan: what is disposable right now, and how much it occupies.</summary>
public sealed class CleanupReport
{
    public required IReadOnlyList<CleanupGroup> Groups { get; init; }

    /// <summary>Only the groups that actually hold something, for a compact breakdown.</summary>
    public IEnumerable<CleanupGroup> NonEmptyGroups => Groups.Where(g => g.FileCount > 0);

    public long TotalBytes => Groups.Sum(g => g.Bytes);
    public int TotalFiles => Groups.Sum(g => g.FileCount);
    public bool IsEmpty => TotalFiles == 0;
}

/// <summary>Progress of an ongoing delete, in bytes as well as files.</summary>
public readonly record struct CleanupProgress(
    double Fraction, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>Outcome of a delete. Locked files are skipped, not treated as a failure.</summary>
public sealed record CleanupResult(long FreedBytes, int DeletedFiles, int SkippedFiles);

/// <summary>
/// Measures and removes everything the app can rebuild: downloaded IPA files (including
/// half-finished ones), the icon and catalog caches, staged photo-library copies in the
/// system temp folder, update installers left over from earlier versions, and log files
/// from previous days.
///
/// Deleting is done file by file rather than with a single recursive Directory.Delete so
/// that progress is real — clearing tens of gigabytes of IPA files otherwise looks frozen —
/// and so that one file held open by another process does not abort the whole sweep.
///
/// Never touches settings, the user's own catalog additions, learned download sizes, the
/// ipatool keychain or the saved iCloud session: those are not caches, and losing them
/// would silently sign the user out or discard their data.
/// </summary>
public sealed class CleanupService
{
    private readonly ToolLocator _tools;

    public CleanupService(ToolLocator tools) => _tools = tools;

    /// <summary>
    /// Walks every disposable location. Runs on a worker thread: on a slow or
    /// network drive an Apps folder with thousands of files takes a noticeable moment,
    /// and the UI has to keep drawing its progress bar meanwhile.
    /// </summary>
    public Task<CleanupReport> ScanAsync(IProgress<string>? onGroup = null,
                                         CancellationToken ct = default)
        => Task.Run(() =>
        {
            var groups = new List<CleanupGroup>
            {
                Measure("L.Cache.Item.Apps",    _tools.AppsFolder,      onGroup, ct),
                Measure("L.Cache.Item.Icons",   _tools.IconCacheFolder, onGroup, ct),
                // Icons extracted from local .ipa files. A separate folder from the store
                // icons above and simply missing from this list, so it was never scanned
                // and never cleared however often the user pressed the button.
                Measure("L.Cache.Item.IpaIcons", _tools.LocalIpaIconCacheFolder, onGroup, ct),
                // Home-screen artwork read off connected devices. Reportable and clearable
                // like the rest: it is a pure cache, and dropping it only costs one more
                // SpringBoard read the next time the device list is opened.
                Measure("L.Cache.Item.DeviceIcons", _tools.DeviceIconCacheFolder, onGroup, ct),
                Measure("L.Cache.Item.Thumbs",  _tools.PhotoThumbCacheFolder, onGroup, ct),
                // The largest single item here by far: one copy of the device Photos library
                // runs to hundreds of megabytes. It is kept deliberately (re-fetching it is
                // what made the album list take minutes), so it has to be reportable and
                // clearable — otherwise it would be invisible disk usage the user cannot find.
                Measure("L.Cache.Item.PhotoDb", _tools.PhotoLibraryDbCacheFolder, onGroup, ct),
                MeasureFile("L.Cache.Item.Catalog", _tools.CatalogCacheFile, onGroup),
                Measure("L.Cache.Item.Temp",    _tools.TempFolder,      onGroup, ct),
                MeasureStaleInstallers(onGroup, ct),
                MeasureOldLogs(onGroup, ct),
            };

            var report = new CleanupReport { Groups = groups };
            AppLog.Info($"Cache scan: {report.TotalFiles} files, {report.TotalBytes} bytes.");
            return report;
        }, ct);

    /// <summary>
    /// Deletes everything listed in <paramref name="report"/>, reporting progress as it
    /// goes. Cancelling stops early; what has already been deleted stays deleted, which
    /// is harmless because every one of these files is rebuildable.
    /// </summary>
    public Task<CleanupResult> CleanAsync(CleanupReport report,
                                          IProgress<CleanupProgress>? progress = null,
                                          CancellationToken ct = default)
        => Task.Run(() =>
        {
            long total = report.TotalBytes;
            int totalFiles = report.TotalFiles;
            long freed = 0;
            int deleted = 0, skipped = 0;

            // Throttle: a few thousand files would otherwise post a few thousand
            // dispatcher callbacks and make the bar itself the slowest part.
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            void Report(bool force)
            {
                if (progress is null) return;
                if (!force && clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(60)) return;
                lastReport = clock.Elapsed;
                var fraction = total > 0 ? (double)freed / total
                             : totalFiles > 0 ? (double)deleted / totalFiles
                             : 1d;
                progress.Report(new CleanupProgress(Math.Clamp(fraction, 0, 1),
                                                    freed, total, deleted, totalFiles));
            }

            Report(force: true);

            foreach (var group in report.Groups)
            {
                foreach (var file in group.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var size = SizeOf(file);
                        File.Delete(file);
                        freed += size;
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        // Typically a staged photo database SQLite still holds, or an
                        // IPA open in another program. Leave it and carry on.
                        skipped++;
                        AppLog.Warn($"Clear cache: kept '{file}': {ex.Message}");
                    }
                    Report(force: false);
                }
            }

            Report(force: true);

            // Directories are emptied above; drop the leftover empty shells so a stale
            // folder tree does not accumulate, then put back the ones the app needs.
            PruneEmptyDirectories(_tools.IconCacheFolder);
            PruneEmptyDirectories(_tools.LocalIpaIconCacheFolder);
            // One sub-folder per device, so clearing leaves a shell behind for every
            // iPhone ever plugged in without this.
            PruneEmptyDirectories(_tools.DeviceIconCacheFolder);
            // The thumbnail cache is sharded into up-to-256 sub-folders, so clearing it
            // leaves that many empty shells behind without this.
            PruneEmptyDirectories(_tools.PhotoThumbCacheFolder);
            PruneEmptyDirectories(_tools.TempFolder);
            PruneEmptyDirectories(_tools.AppsFolder, keepRoot: true);
            _tools.EnsureFolders();

            AppLog.Info($"Cache cleared: {deleted} files, {freed} bytes freed, {skipped} kept.");
            return new CleanupResult(freed, deleted, skipped);
        }, ct);

    private static CleanupGroup Measure(string key, string folder,
                                        IProgress<string>? onGroup, CancellationToken ct)
    {
        onGroup?.Report(key);
        var files = new List<string>();
        long bytes = 0;

        if (Directory.Exists(folder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(file);
                    bytes += SizeOf(file);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn($"Cache scan: could not read '{folder}': {ex.Message}");
            }
        }

        return new CleanupGroup { Key = key, Path = folder, Files = files, Bytes = bytes };
    }

    private static CleanupGroup MeasureFile(string key, string file, IProgress<string>? onGroup)
    {
        onGroup?.Report(key);
        var exists = File.Exists(file);
        return new CleanupGroup
        {
            Key = key,
            Path = file,
            Files = exists ? new[] { file } : Array.Empty<string>(),
            Bytes = exists ? SizeOf(file) : 0,
        };
    }

    /// <summary>
    /// Update installers left in the temp root by earlier versions of the app.
    ///
    /// New downloads go into the swept temp sub-folder, but builds up to 1.6.104 wrote here,
    /// and each one is around 60 MB. Nothing removes them, so they accumulate for as long as
    /// the user keeps updating — this reclaims what those versions left behind.
    ///
    /// Matched by the app's own installer naming only, and non-recursively: the temp root is
    /// shared with every other program on the machine and must not be swept broadly.
    /// </summary>
    private static CleanupGroup MeasureStaleInstallers(IProgress<string>? onGroup, CancellationToken ct)
    {
        const string key = "L.Cache.Item.Installers";
        onGroup?.Report(key);

        var folder = Path.GetTempPath();
        var files = new List<string>();
        long bytes = 0;

        try
        {
            foreach (var pattern in new[] { "IPAStudio-Setup-*.exe", "IPAStudio-Update.exe" })
            {
                foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(file);
                    bytes += SizeOf(file);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"Cache scan: could not read '{folder}': {ex.Message}");
        }

        return new CleanupGroup { Key = key, Path = folder, Files = files, Bytes = bytes };
    }

    /// <summary>
    /// Log files from earlier days. Today's file is deliberately left alone: AppLog holds
    /// it open, and it is the one file support would ask for after a problem.
    /// </summary>
    private CleanupGroup MeasureOldLogs(IProgress<string>? onGroup, CancellationToken ct)
    {
        const string key = "L.Cache.Item.Logs";
        onGroup?.Report(key);

        // Derived from the live log path so the two can never disagree about where
        // logs are, including the temp-folder fallback AppLog uses when LocalAppData
        // is not writable.
        var current = AppLog.FilePath;
        var folder = Path.GetDirectoryName(current) ?? "";
        var files = new List<string>();
        long bytes = 0;

        if (Directory.Exists(folder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "ipastudio-*.log"))
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase))
                        continue;
                    files.Add(file);
                    bytes += SizeOf(file);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn($"Cache scan: could not read '{folder}': {ex.Message}");
            }
        }

        return new CleanupGroup { Key = key, Path = folder, Files = files, Bytes = bytes };
    }

    private static long SizeOf(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    private static void PruneEmptyDirectories(string root, bool keepRoot = false)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* still in use; nothing depends on it being gone */ }
            }

            if (!keepRoot && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Clear cache: could not prune '{root}': {ex.Message}");
        }
    }
}
