namespace IPAStudio.Core.Tools;

/// <summary>
/// Resolves paths to the bundled command-line tools (ipatool, ideviceinstaller,
/// anisette) and well-known application folders.
///
/// Layout (relative to the application base directory):
///   tools/windows_amd64_v2/ipatool.exe
///   tools/windows_amd64_v3/ipatool.exe, anisette.exe
///   tools/imobiledevice/ideviceinstaller.exe, idevice_id.exe, ideviceinfo.exe
/// </summary>
public sealed class ToolLocator
{
    private readonly string _baseDir;

    /// <summary>Selected ipatool major version (2 or 3). Default is 2 (no iCloud requirement).</summary>
    public int IpatoolVersion { get; set; } = 2;

    public ToolLocator(string? baseDirectory = null)
    {
        _baseDir = baseDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// When set, tools are resolved from this folder instead of the default
    /// install location (used when Program Files is not writable and tools
    /// were auto-downloaded into LocalAppData).
    /// </summary>
    public string? ToolsRootOverride { get; set; }

    public string ToolsRoot => ToolsRootOverride ?? Path.Combine(_baseDir, "tools");

    public string IpatoolPath => Path.Combine(
        ToolsRoot,
        IpatoolVersion == 3 ? "windows_amd64_v3" : "windows_amd64_v2",
        "ipatool.exe");

    public string AnisettePath => Path.Combine(ToolsRoot, "windows_amd64_v3", "anisette.exe");

    public string IdeviceInstallerPath => Path.Combine(ToolsRoot, "imobiledevice", "ideviceinstaller.exe");
    public string IdeviceIdPath => Path.Combine(ToolsRoot, "imobiledevice", "idevice_id.exe");
    public string IdeviceInfoPath => Path.Combine(ToolsRoot, "imobiledevice", "ideviceinfo.exe");

    /// <summary>Folder where downloaded IPA files are stored.</summary>
    public string AppsFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "IPAStudio", "Apps");

    /// <summary>Local application data folder (icon cache, catalog cache, settings).</summary>
    public string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPAStudio");

    public string IconCacheFolder => Path.Combine(DataFolder, "icons");
    public string CatalogCacheFile => Path.Combine(DataFolder, "catalog-cache.json");
    public string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public void EnsureFolders()
    {
        Directory.CreateDirectory(AppsFolder);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(IconCacheFolder);
    }

    /// <summary>Verifies that the required tool binaries exist; returns missing paths.</summary>
    public IReadOnlyList<string> ValidateTools()
    {
        var required = new[] { IpatoolPath, IdeviceInstallerPath, IdeviceIdPath, IdeviceInfoPath };
        return required.Where(p => !File.Exists(p)).ToList();
    }
}
