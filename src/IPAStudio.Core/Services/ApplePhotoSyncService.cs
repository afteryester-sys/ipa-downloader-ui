using System.Diagnostics;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Prepares a stable folder for the photo-sync feature provided by Apple Devices/iTunes.
/// Modern iOS does not import files copied directly into DCIM, so the official Apple client
/// remains the supported Windows path for adding computer photos to the device library.
/// </summary>
public sealed class ApplePhotoSyncService
{
    private const string AppleDevicesAumid = "AppleInc.AppleDevices_nzyj5cx40ttqa!App";
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".mov", ".mp4", ".m4v",
    };

    private readonly SettingsService _settings;

    public ApplePhotoSyncService(SettingsService settings) => _settings = settings;

    public static bool IsSupportedMedia(string path) =>
        File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    public async Task<ApplePhotoSyncResult> PrepareAsync(
        string deviceId,
        IReadOnlyCollection<string> sourcePaths,
        IProgress<PhotoTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var root = ResolveRootFolder();
        var folder = Path.Combine(root, SafeSegment(deviceId));
        Directory.CreateDirectory(folder);

        var candidates = sourcePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var skipped = new List<string>();
        var prepared = 0;

        foreach (var source in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(source);
            progress?.Report(new PhotoTransferProgress(prepared, candidates.Count, name));

            if (!IsSupportedMedia(source))
            {
                skipped.Add(name);
                continue;
            }

            var destination = UniquePath(folder, name);
            await CopyAndVerifyAsync(source, destination, ct).ConfigureAwait(false);
            prepared++;
        }

        progress?.Report(new PhotoTransferProgress(prepared, candidates.Count, string.Empty));
        if (prepared == 0)
            return new ApplePhotoSyncResult
            {
                Total = candidates.Count,
                Folder = folder,
                SkippedFiles = skipped,
            };

        _settings.Current.ApplePhotoSyncFolder = root;
        _settings.Save();

        var client = DetectClient();
        var opened = OpenClient(client);
        OpenFolder(folder);

        AppLog.Info($"Apple photo sync: prepared {prepared}/{candidates.Count} in {folder}; " +
                    $"client={client}; opened={opened}");
        return new ApplePhotoSyncResult
        {
            Prepared = prepared,
            Total = candidates.Count,
            Folder = folder,
            Client = client,
            ClientOpened = opened,
            SkippedFiles = skipped,
        };
    }

    public static ApplePhotoClient DetectClient()
    {
        if (HasAppleDevicesPackage()) return ApplePhotoClient.AppleDevices;
        return FindITunesPath() is not null ? ApplePhotoClient.ITunes : ApplePhotoClient.None;
    }

    private string ResolveRootFolder()
    {
        var configured = _settings.Current.ApplePhotoSyncFolder;
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "IPA Studio Sync");
    }

    private static bool HasAppleDevicesPackage()
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return false;

        try
        {
            return Directory.EnumerateDirectories(packages, "AppleInc.AppleDevices_*").Any();
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private static string? FindITunesPath()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "iTunes", "iTunes.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "iTunes", "iTunes.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "iTunes.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool OpenClient(ApplePhotoClient client)
    {
        try
        {
            ProcessStartInfo? start = client switch
            {
                ApplePhotoClient.AppleDevices => new ProcessStartInfo("explorer.exe",
                    $"shell:AppsFolder\\{AppleDevicesAumid}"),
                ApplePhotoClient.ITunes when FindITunesPath() is { } path => new ProcessStartInfo(path),
                _ => null,
            };
            if (start is null) return false;
            start.UseShellExecute = true;
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Apple photo sync: could not launch client ({ex.Message})");
            return false;
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Apple photo sync: could not open staging folder ({ex.Message})");
        }
    }

    private static async Task CopyAndVerifyAsync(string source, string destination, CancellationToken ct)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }

        var expected = new FileInfo(source).Length;
        var actual = new FileInfo(destination).Length;
        if (actual == expected) return;

        try { File.Delete(destination); } catch { /* best effort cleanup */ }
        throw new IOException($"Copy verification failed for '{Path.GetFileName(source)}': {actual} of {expected} bytes.");
    }

    private static string UniquePath(string folder, string name)
    {
        var safeName = SafeFileName(name);
        var candidate = Path.Combine(folder, safeName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SafeFileName(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(clean) ? $"media-{Guid.NewGuid():N}" : clean;
    }

    private static string SafeSegment(string value)
    {
        var clean = new string(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(clean) ? "device" : clean;
    }
}
