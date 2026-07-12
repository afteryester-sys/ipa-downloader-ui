using System.Text.Json;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>What the queue should do after downloading an IPA.</summary>
public enum InstallMode
{
    /// <summary>Download the IPA to disk and then install it on the device (default).</summary>
    DownloadAndInstall = 0,

    /// <summary>Download only; do not install. The IPA stays in the Apps folder.</summary>
    DownloadOnly = 1,

    /// <summary>Skip downloading; install a locally existing IPA file that is already cached.</summary>
    InstallExistingOnly = 2,
}

/// <summary>Persisted user settings.</summary>
public sealed class AppSettings
{

    /// <summary>UI language: "ru" or "en".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>ipatool major version: 2 (default, no iCloud needed) or 3.</summary>
    public int IpatoolVersion { get; set; } = 2;

    /// <summary>Folder where IPA files are stored.</summary>
    public string? AppsFolder { get; set; }

    /// <summary>Number of parallel downloads (1-5).</summary>
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>Last Apple ID used to sign in; pre-filled on the login screen.</summary>
    public string? LastAppleId { get; set; }

    /// <summary>Determines what happens after a download: install, download-only, or install-only.</summary>
    public InstallMode InstallMode { get; set; } = InstallMode.DownloadAndInstall;
}

/// <summary>Loads and saves settings as JSON in the local app data folder.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ToolLocator _tools;

    public AppSettings Current { get; private set; } = new();

    public SettingsService(ToolLocator tools)
    {
        _tools = tools;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_tools.SettingsFile))
            {
                var json = File.ReadAllText(_tools.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
        Apply();
    }

    public void Save()
    {
        _tools.EnsureFolders();
        File.WriteAllText(_tools.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
        Apply();
    }

    /// <summary>
    /// Exposes the current <see cref="InstallMode"/> so callers (e.g. QueueService)
    /// can read it without depending on the full settings file.
    /// </summary>
    public InstallMode InstallMode => Current.InstallMode;

    /// <summary>Pushes settings into dependent services.</summary>
    private void Apply()
    {
        _tools.IpatoolVersion = Current.IpatoolVersion;
        if (!string.IsNullOrWhiteSpace(Current.AppsFolder))
            _tools.AppsFolder = Current.AppsFolder;
    }
}
