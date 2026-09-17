using System.Diagnostics;
using System.IO;
using IPAStudio.Core.Diagnostics;
using Microsoft.Win32;

namespace IPAStudio.Core.Services;

/// <summary>A located iTunes installation.</summary>
/// <param name="ExePath">Full path to iTunes.exe.</param>
/// <param name="Version">Product version as reported by the binary itself.</param>
/// <param name="SupportsAppStore">
/// True for 12.6.x and older, the builds that still contain the App Store tab and can therefore
/// download .ipa archives. Apple removed it in 12.7, so a newer iTunes is detected but useless
/// for this route and the UI has to say so rather than open a window with no store in it.
/// </param>
public sealed record ItunesInstallation(string ExePath, Version Version, bool SupportsAppStore);

/// <summary>An .ipa sitting in an iTunes library folder.</summary>
public sealed record ItunesIpa(string Path, DateTime LastWriteUtc, long SizeBytes);

/// <summary>
/// The "iTunes 12.6.5.3" route: a second, independent way to obtain an .ipa that does not use
/// ipatool and does not talk to Apple's authentication service at all.
///
/// Why it exists: Apple changed the native authentication endpoint, which is what ipatool signs
/// into. iTunes 12.6.5.3 still has the App Store tab and its own, still-working session, so it
/// can download an app that ipatool can no longer fetch. This service is the programmatic half
/// of the manual trick described on 4PDA (the "iTunes App Opener" script): it opens the app's
/// store page directly in iTunes with an <c>itmss://</c> link, then watches the iTunes library
/// for the archive that the user's click produces.
///
/// It deliberately does not automate iTunes' own UI. The download is started by the user inside
/// iTunes, with iTunes' own account and licence checks intact; this class only points iTunes at
/// the right page and picks up the resulting file.
/// </summary>
public sealed class ItunesLegacyService
{
    /// <summary>
    /// How long to keep watching the library after the store page was opened. Generous because
    /// the clock covers a human action — finding the button and letting a large app download —
    /// not just a transfer.
    /// </summary>
    private static readonly TimeSpan WatchTimeout = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A file must stop growing for this long before it is treated as finished. iTunes writes
    /// the .ipa in place, so a file that merely exists may still be half-written.
    /// </summary>
    private static readonly TimeSpan StableFor = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Locates iTunes. Registry first (set by Apple's installer, and correct even when iTunes
    /// was installed somewhere other than Program Files), then the two default locations.
    /// </summary>
    public ItunesInstallation? Detect()
    {
        foreach (var candidate in CandidateExePaths())
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(candidate);
                var version = new Version(
                    Math.Max(info.ProductMajorPart, 0),
                    Math.Max(info.ProductMinorPart, 0),
                    Math.Max(info.ProductBuildPart, 0),
                    Math.Max(info.ProductPrivatePart, 0));

                // 12.7 dropped the App Store tab; everything before it keeps it.
                var supportsStore = version.Major < 12 ||
                                    (version.Major == 12 && version.Minor <= 6);

                return new ItunesInstallation(candidate, version, supportsStore);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"iTunes route: could not read the version of '{candidate}': {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<string?> CandidateExePaths()
    {
        yield return ReadRegistryPath(@"SOFTWARE\Apple Computer, Inc.\iTunes", "iTunesExe");
        yield return CombineOrNull(ReadRegistryPath(@"SOFTWARE\Apple Computer, Inc.\iTunes", "InstallDir"), "iTunes.exe");
        yield return CombineOrNull(ReadRegistryPath(@"SOFTWARE\WOW6432Node\Apple Computer, Inc.\iTunes", "InstallDir"), "iTunes.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "iTunes", "iTunes.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "iTunes", "iTunes.exe");
    }

    private static string? CombineOrNull(string? directory, string file) =>
        string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, file);

    private static string? ReadRegistryPath(string subKey, string valueName)
    {
        // Same guard the rest of Core uses around the registry: the assembly targets plain
        // net8.0, so the analyzer has to be told the call is only reached on Windows.
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            // A missing or unreadable key just means "look somewhere else".
            return null;
        }
    }

    /// <summary>
    /// Folders iTunes drops purchased .ipa files into. Both the modern and the pre-12.x layout
    /// are returned because an installation upgraded over the years can still be using the old
    /// one, and a library moved by the user is covered by <paramref name="extraFolder"/>.
    /// </summary>
    public IReadOnlyList<string> LibraryFolders(string? extraFolder = null)
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(extraFolder)) candidates.Add(extraFolder);
        candidates.Add(Path.Combine(music, "iTunes", "iTunes Media", "Mobile Applications"));
        candidates.Add(Path.Combine(music, "iTunes", "Mobile Applications"));

        return candidates
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every .ipa currently in the iTunes library, newest first. Used both to show what is
    /// already available without downloading anything and as the "before" snapshot of a watch.
    /// </summary>
    public IReadOnlyList<ItunesIpa> ListLibrary(string? extraFolder = null)
    {
        var found = new List<ItunesIpa>();

        foreach (var folder in LibraryFolders(extraFolder))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*.ipa", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(path);
                    found.Add(new ItunesIpa(path, info.LastWriteTimeUtc, info.Length));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"iTunes route: could not read '{folder}': {ex.Message}");
            }
        }

        return found.OrderByDescending(f => f.LastWriteUtc).ToList();
    }

    /// <summary>
    /// The store page URL for an app, in the scheme iTunes registers for itself. An
    /// <c>itmss://</c> link opens straight inside iTunes instead of the browser, which is the
    /// whole point: the browser would send the user to the modern web App Store, which cannot
    /// produce a file.
    /// </summary>
    public static string StoreUrl(long appStoreId) =>
        $"itmss://apps.apple.com/app/id{appStoreId}";

    /// <summary>
    /// Opens the app's App Store page inside iTunes. Returns false when iTunes is missing or
    /// refuses to launch, so the caller can explain that instead of waiting for a file that
    /// will never appear.
    /// </summary>
    public bool OpenStorePage(long appStoreId)
    {
        if (appStoreId <= 0) return false;

        var url = StoreUrl(appStoreId);
        try
        {
            // ShellExecute, so Windows hands the itmss:// scheme to whichever iTunes is
            // registered for it - the same path the 4PDA script relies on.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppLog.Info($"iTunes route: opened {url}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iTunes route: could not open {url}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Watches the iTunes library for an .ipa that was not there before, and returns its path
    /// once it has finished being written.
    /// </summary>
    /// <param name="before">Snapshot taken before the download was started.</param>
    /// <param name="extraFolder">Optional user-configured library folder.</param>
    /// <param name="status">Receives human-readable progress for the UI.</param>
    public async Task<string?> WaitForNewIpaAsync(
        IReadOnlyList<ItunesIpa> before,
        string? extraFolder,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var known = before.ToDictionary(f => f.Path, f => f.SizeBytes, StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + WatchTimeout;

        string? candidate = null;
        long candidateSize = -1;
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // A file is interesting when it is new, or when it grew since the snapshot -
            // re-downloading an app overwrites the archive already in the library.
            var current = ListLibrary(extraFolder);
            var fresh = current
                .Where(f => !known.TryGetValue(f.Path, out var size) || size != f.SizeBytes)
                .OrderByDescending(f => f.LastWriteUtc)
                .FirstOrDefault();

            if (fresh is not null)
            {
                if (candidate is null || !string.Equals(candidate, fresh.Path, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = fresh.Path;
                    candidateSize = fresh.SizeBytes;
                    stableSince = DateTime.UtcNow;
                    status?.Report(Path.GetFileName(fresh.Path));
                }
                else if (fresh.SizeBytes != candidateSize)
                {
                    candidateSize = fresh.SizeBytes;
                    stableSince = DateTime.UtcNow;
                    status?.Report(Path.GetFileName(fresh.Path));
                }
                else if (DateTime.UtcNow - stableSince >= StableFor && IsReadable(fresh.Path))
                {
                    AppLog.Info($"iTunes route: picked up '{fresh.Path}' ({fresh.SizeBytes} bytes)");
                    return fresh.Path;
                }
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        AppLog.Warn("iTunes route: timed out waiting for a new .ipa in the iTunes library.");
        return null;
    }

    /// <summary>
    /// True when the file can be opened for reading without a sharing violation, i.e. iTunes
    /// has let go of it. Size alone is not enough: the last chunk and the close can be seconds
    /// apart, and copying a still-open archive produces a corrupt .ipa.
    /// </summary>
    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies an archive out of the iTunes library into <paramref name="destinationFolder"/>,
    /// without disturbing the original: iTunes still lists it, and deleting it there is the
    /// user's business, not ours. Returns the new path.
    /// </summary>
    public async Task<string> CopyOutAsync(string ipaPath, string destinationFolder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationFolder);

        var target = Path.Combine(destinationFolder, Path.GetFileName(ipaPath));
        var stem = Path.GetFileNameWithoutExtension(ipaPath);
        var counter = 2;

        // Never overwrite: two versions of the same app are both worth keeping, and the
        // older file may be the only copy of a build the store no longer serves.
        while (File.Exists(target))
            target = Path.Combine(destinationFolder, $"{stem} ({counter++}).ipa");

        await using var source = new FileStream(ipaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var sink = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(sink, ct).ConfigureAwait(false);

        AppLog.Info($"iTunes route: copied '{ipaPath}' to '{target}'");
        return target;
    }
}
