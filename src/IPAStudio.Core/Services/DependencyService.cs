using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using IPAStudio.Core.Tools;
using Microsoft.Win32;

namespace IPAStudio.Core.Services;

public enum DependencyState
{
    Unknown,
    Checking,
    Ok,
    Missing,
    Installing,
    Failed,
}

public sealed class DependencyStatus
{
    public DependencyState AppleDrivers { get; set; } = DependencyState.Unknown;
    public DependencyState ITunes { get; set; } = DependencyState.Unknown;
    public DependencyState CliTools { get; set; } = DependencyState.Unknown;

    /// <summary>Everything required for device detection and installs is present.</summary>
    public bool AllReady =>
        AppleDrivers == DependencyState.Ok && CliTools == DependencyState.Ok;
}

/// <summary>
/// Autonomous environment setup: verifies and installs everything the app
/// needs to talk to an iPhone.
///
///  1. Apple Mobile Device Support (USB driver + service) — comes with iTunes.
///  2. iTunes itself (recommended, provides driver updates).
///  3. Bundled CLI tools (ipatool, libimobiledevice) — auto-downloaded when
///     missing so a plain portable copy also works offline-first.
///
/// iTunes install strategy: winget (silent) first, then direct download of the
/// official installer from apple.com with a silent flag.
/// </summary>
public sealed class DependencyService
{
    private const string ITunesDownloadUrl = "https://www.apple.com/itunes/download/win64";

    private const string RepoRaw =
        "https://raw.githubusercontent.com/kda2495/IPA_Downloader/main/MainApp";

    private const string ImobiledeviceZipUrl =
        "https://github.com/libimobiledevice-win32/imobiledevice-net/releases/download/v1.3.17/libimobiledevice.1.2.1-r1122-win-x64.zip";

    private readonly ToolLocator _tools;
    private readonly HttpClient _http;

    public DependencyStatus Status { get; } = new();

    public event Action? StatusChanged;

    public DependencyService(ToolLocator tools, HttpClient http)
    {
        _tools = tools;
        _http = http;
    }

    // ------------------------------------------------------------------ checks

    /// <summary>Runs all checks and updates <see cref="Status"/>.</summary>
    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        Status.AppleDrivers = DependencyState.Checking;
        Status.ITunes = DependencyState.Checking;
        Status.CliTools = DependencyState.Checking;
        StatusChanged?.Invoke();

        Status.AppleDrivers = await Task.Run(CheckAppleMobileDeviceSupport, ct);
        Status.ITunes = await Task.Run(CheckITunesInstalled, ct);
        Status.CliTools = _tools.ValidateTools().Count == 0
            ? DependencyState.Ok
            : DependencyState.Missing;

        StatusChanged?.Invoke();
    }

    /// <summary>Apple Mobile Device Support service is registered (installed with iTunes).</summary>
    private static DependencyState CheckAppleMobileDeviceSupport()
    {
        if (!OperatingSystem.IsWindows()) return DependencyState.Missing;

        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service");
        return key is not null ? DependencyState.Ok : DependencyState.Missing;
    }

    private static DependencyState CheckITunesInstalled()
    {
        if (!OperatingSystem.IsWindows()) return DependencyState.Missing;

        // Classic desktop install
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Apple Computer, Inc.\iTunes"))
        {
            if (key is not null) return DependencyState.Ok;
        }

        // Microsoft Store version registers the AMDS driver but a different key;
        // presence of the driver service is treated as equivalent above, so here
        // we only report the desktop app.
        using (var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\WOW6432Node\Apple Computer, Inc.\iTunes"))
        {
            return key is not null ? DependencyState.Ok : DependencyState.Missing;
        }
    }

    // ---------------------------------------------------------------- installs

    /// <summary>
    /// Installs iTunes silently: tries winget first, falls back to downloading
    /// the official installer from apple.com. Reports progress via <paramref name="progress"/> (0..1, or -1 for indeterminate).
    /// </summary>
    public async Task<bool> InstallITunesAsync(
        IProgress<(double fraction, string stage)>? progress = null,
        CancellationToken ct = default)
    {
        Status.ITunes = DependencyState.Installing;
        Status.AppleDrivers = DependencyState.Installing;
        StatusChanged?.Invoke();

        try
        {
            // --- 1) winget (present on all up-to-date Windows 10/11) ---------
            progress?.Report((-1, "winget"));
            if (await TryWingetInstallAsync(ct))
            {
                await CheckAllAsync(ct);
                return Status.ITunes == DependencyState.Ok;
            }

            // --- 2) direct download from apple.com ---------------------------
            var setupPath = Path.Combine(Path.GetTempPath(), "iTunes64Setup.exe");
            await DownloadWithProgressAsync(ITunesDownloadUrl, setupPath,
                f => progress?.Report((f, "download")), ct);

            progress?.Report((-1, "install"));
            var ok = await RunElevatedAsync(setupPath, "/quiet /norestart", ct);
            try { File.Delete(setupPath); } catch { /* best effort */ }

            await CheckAllAsync(ct);
            return ok && Status.AppleDrivers == DependencyState.Ok;
        }
        catch
        {
            Status.ITunes = DependencyState.Failed;
            Status.AppleDrivers = DependencyState.Failed;
            StatusChanged?.Invoke();
            return false;
        }
    }

    private static async Task<bool> TryWingetInstallAsync(CancellationToken ct)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("winget", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (probe is null) return false;
            await probe.WaitForExitAsync(ct);
            if (probe.ExitCode != 0) return false;

            using var install = Process.Start(new ProcessStartInfo(
                "winget",
                "install --id Apple.iTunes --silent --disable-interactivity " +
                "--accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (install is null) return false;
            await install.WaitForExitAsync(ct);
            return install.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunElevatedAsync(
        string path, string arguments, CancellationToken ct)
    {
        // UAC prompt: elevation requires ShellExecute, so no output redirection.
        using var proc = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
        });
        if (proc is null) return false;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0;
    }

    /// <summary>
    /// Downloads any missing CLI tools (ipatool v2/v3, anisette, libimobiledevice)
    /// into the tools folder — makes a bare portable copy self-sufficient.
    /// </summary>
    public async Task<bool> InstallCliToolsAsync(
        IProgress<(double fraction, string stage)>? progress = null,
        CancellationToken ct = default)
    {
        Status.CliTools = DependencyState.Installing;
        StatusChanged?.Invoke();

        try
        {
            var root = GetWritableToolsRoot();

            // ipatool v2 / v3 / anisette — small, direct downloads.
            var direct = new (string url, string relative)[]
            {
                ($"{RepoRaw}/windows_amd64_v2/ipatool.exe", @"windows_amd64_v2\ipatool.exe"),
                ($"{RepoRaw}/windows_amd64_v3/ipatool.exe", @"windows_amd64_v3\ipatool.exe"),
                ($"{RepoRaw}/windows_amd64_v3/anisette.exe", @"windows_amd64_v3\anisette.exe"),
            };

            for (var i = 0; i < direct.Length; i++)
            {
                var (url, relative) = direct[i];
                var dest = Path.Combine(root, relative);
                if (File.Exists(dest)) continue;

                var step = i; // capture
                await DownloadWithProgressAsync(url, dest,
                    f => progress?.Report(((step + f) / 4.0, "tools")), ct);
            }

            // libimobiledevice suite — zip that we extract selectively.
            var imobileDir = Path.Combine(root, "imobiledevice");
            if (!File.Exists(Path.Combine(imobileDir, "ideviceinstaller.exe")))
            {
                var zipPath = Path.Combine(Path.GetTempPath(), "imobiledevice-net.zip");
                await DownloadWithProgressAsync(ImobiledeviceZipUrl, zipPath,
                    f => progress?.Report(((3 + f) / 4.0, "tools")), ct);

                Directory.CreateDirectory(imobileDir);
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var wanted = new[]
                    {
                        "ideviceinstaller.exe", "idevice_id.exe",
                        "ideviceinfo.exe", "idevicepair.exe",
                    };
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.Length == 0) continue;
                        var isWanted = wanted.Contains(entry.Name,
                            StringComparer.OrdinalIgnoreCase);
                        var isDll = entry.Name.EndsWith(".dll",
                            StringComparison.OrdinalIgnoreCase);
                        if (!isWanted && !isDll) continue;

                        entry.ExtractToFile(
                            Path.Combine(imobileDir, entry.Name), overwrite: true);
                    }
                }
                try { File.Delete(zipPath); } catch { /* best effort */ }
            }

            await CheckAllAsync(ct);
            return Status.CliTools == DependencyState.Ok;
        }
        catch
        {
            Status.CliTools = DependencyState.Failed;
            StatusChanged?.Invoke();
            return false;
        }
    }

    /// <summary>
    /// Preferred tools root; falls back to LocalAppData when the install
    /// folder (e.g. Program Files) is not writable without elevation.
    /// </summary>
    private string GetWritableToolsRoot()
    {
        var root = _tools.ToolsRoot;
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return root;
        }
        catch (UnauthorizedAccessException)
        {
            var fallback = Path.Combine(_tools.DataFolder, "tools");
            Directory.CreateDirectory(fallback);
            _tools.ToolsRootOverride = fallback;
            return fallback;
        }
    }

    private async Task DownloadWithProgressAsync(
        string url, string destination, Action<double> onFraction, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destination);
        if (dir is not null) Directory.CreateDirectory(dir);

        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);

        var buffer = new byte[81920];
        long read = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, count), ct);
            read += count;
            if (total > 0) onFraction(Math.Min(1.0, (double)read / total));
        }
        onFraction(1.0);
    }
}
