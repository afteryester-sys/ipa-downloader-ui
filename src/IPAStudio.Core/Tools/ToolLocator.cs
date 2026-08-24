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

    /// <summary>
    /// Fixed passphrase used to lock/unlock ipatool's local keychain file. ipatool
    /// requires this in non-interactive mode; using a constant lets every command
    /// (login, info, purchase, download) unlock the same keychain without ever
    /// prompting on a terminal (which deadlocks when stdin is redirected).
    /// </summary>
    public const string KeychainPassphrase = "ipastudio-local-keychain";

    /// <summary>Selected engine slot: 2 is current ipatool-rs; 3 is legacy anisette fallback.</summary>
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

    public string IpatoolEngineName => IpatoolVersion == 3 ? "Legacy ipatool + anisette" : "ipatool-rs";

    public string AnisettePath => Path.Combine(ToolsRoot, "windows_amd64_v3", "anisette.exe");

    public string IdeviceInstallerPath => Path.Combine(ToolsRoot, "imobiledevice", "ideviceinstaller.exe");
    public string IdeviceIdPath => Path.Combine(ToolsRoot, "imobiledevice", "idevice_id.exe");
    public string IdeviceInfoPath => Path.Combine(ToolsRoot, "imobiledevice", "ideviceinfo.exe");

    /// <summary>Reads live IORegistry entries (used for battery health / cycle count).</summary>
    public string IdeviceDiagnosticsPath => Path.Combine(ToolsRoot, "imobiledevice", "idevicediagnostics.exe");

    /// <summary>Folder where downloaded IPA files are stored.</summary>
    public string AppsFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "IPAStudio", "Apps");

    /// <summary>Local application data folder (icon cache, catalog cache, settings).</summary>
    public string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPAStudio");

    public string IconCacheFolder => Path.Combine(DataFolder, "icons");
    public string CatalogCacheFile => Path.Combine(DataFolder, "catalog-cache.json");

    /// <summary>
    /// Artwork lifted out of local .ipa files, for the folders the user added on the
    /// install-from-file screen.
    ///
    /// Kept apart from <see cref="IconCacheFolder"/>, which holds store artwork fetched by URL
    /// and keyed by App Store id. These are keyed by archive path and timestamp instead, and
    /// go stale for an entirely different reason — the file on disk being replaced.
    /// </summary>
    public string LocalIpaIconCacheFolder => Path.Combine(DataFolder, "ipa-icons");

    /// <summary>
    /// Folders the user named and added as IPA libraries ("Денис", "Рабочие"), so the
    /// install-from-file screen can offer them instead of a bare file picker.
    ///
    /// User data, not a cache: it holds names that exist nowhere else and must survive a
    /// cache clear, which is why it does not live beside the disposable files above.
    /// </summary>
    public string IpaCatalogsFile => Path.Combine(DataFolder, "ipa-catalogs.json");

    /// <summary>
    /// Photo thumbnails already fetched from a device, so returning to the library does not
    /// re-read them.
    ///
    /// Thumbnails were previously held in memory only, which meant every visit to an album
    /// paid the full cost again: each tile is a separate round trip over AFC, and that is
    /// what made the grid fill slowly even for photos seen a minute earlier. On disk they
    /// survive both navigation and a restart.
    ///
    /// Kept apart from the icon cache so "clear cache" can report and drop the two
    /// independently — a photo library produces far more files than the app icons do.
    /// </summary>
    public string PhotoThumbCacheFolder => Path.Combine(DataFolder, "photo-thumbs");

    /// <summary>
    /// Copies of the device Photos library database, one per device.
    ///
    /// The real album names come from /PhotoData/Photos.sqlite, which runs to hundreds of
    /// megabytes on a full library and has to be copied off the device before SQLite can
    /// read it. That copy used to be staged in the system temp folder under a random name,
    /// so a later run could not find it and every visit to the screen paid for the whole
    /// transfer again — minutes of waiting for names the app had already read once.
    ///
    /// Keeping it here means a later visit reads a local file instead. It stays a cache and
    /// never user data: dropping it only costs one more transfer, so "clear cache" reports
    /// and clears it like the rest.
    /// </summary>
    public string PhotoLibraryDbCacheFolder => Path.Combine(DataFolder, "photo-library-db");

    /// <summary>
    /// Apps the user added by hand from the download screen. Kept separate from the
    /// bundled list, which is an embedded resource and therefore not writable, and from
    /// the metadata cache, which is disposable — this file is user data and must survive
    /// a cache rebuild.
    /// </summary>
    public string UserCatalogFile => Path.Combine(DataFolder, "user-apps.json");
    public string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>
    /// Exact sizes measured from finished downloads, keyed by App Store id.
    ///
    /// Apple's public catalog has no entry at all for delisted apps (VK's, for example),
    /// and for those downloads Apple also omits Content-Length, so ipatool prints no total
    /// either — leaving the transfer with no size and a bar that cannot fill. Once such an
    /// app has been fetched once its exact size is known, so every later download of it
    /// shows a real total. Kept out of catalog-cache.json because a metadata refresh
    /// rewrites that file wholesale.
    /// </summary>
    public string LearnedSizesFile => Path.Combine(DataFolder, "learned-sizes.json");

    /// <summary>
    /// Scratch area under the system temp folder. Photo-library database copies are
    /// staged here (a few hundred MB each on a large library). They are deleted after
    /// use, but a crash — or SQLite still holding the file — leaves them behind, so
    /// "clear cache" sweeps this directory as well.
    /// </summary>
    public string TempFolder => SharedTempFolder;

    /// <summary>
    /// The same scratch folder, reachable without a ToolLocator instance. Exists so code that
    /// is not handed one — the updater, which only gets an HttpClient — can still write into
    /// the swept directory instead of the temp root, where its files would never be cleaned.
    /// </summary>
    public static string SharedTempFolder => Path.Combine(Path.GetTempPath(), "IPAStudio");

    public void EnsureFolders()
    {
        Directory.CreateDirectory(AppsFolder);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(IconCacheFolder);
    }

    /// <summary>Verifies that the required tool binaries exist; returns missing paths.</summary>
    public IReadOnlyList<string> ValidateTools()
    {
        var required = new List<string>
        {
            IpatoolPath,
            IdeviceInstallerPath,
            IdeviceIdPath,
            IdeviceInfoPath,
            // Reads battery capacity and cycle count. Its absence used to go unnoticed
            // here, which reported the tool set as complete — so the repair step that
            // re-extracts it never ran, and the battery row stayed "unavailable" forever
            // on installs that predate it being shipped.
            IdeviceDiagnosticsPath,
        };
        // ipatool v3 spawns anisette.exe from the same directory; it is mandatory.
        if (IpatoolVersion == 3)
            required.Add(AnisettePath);
        return required.Where(p => !File.Exists(p)).ToList();
    }

    /// <summary>
    /// Returns the directory that ipatool expects to find its side-by-side helpers
    /// (e.g. anisette.exe) in — always the folder that contains ipatool.exe.
    /// Pass this as the working directory when launching ipatool.
    /// </summary>
    public string IpatoolWorkingDirectory =>
        Path.GetDirectoryName(IpatoolPath) ?? AppContext.BaseDirectory;
}
