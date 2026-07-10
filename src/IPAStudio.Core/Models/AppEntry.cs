namespace IPAStudio.Core.Models;

/// <summary>
/// An App Store application entry from the bundled catalog, enriched with
/// metadata from the iTunes Lookup API.
/// </summary>
public sealed class AppEntry
{
    /// <summary>Display name from the catalog file.</summary>
    public required string Name { get; init; }

    /// <summary>Numeric App Store (Adam) ID.</summary>
    public required long AppStoreId { get; init; }

    // ---- Enriched from iTunes Lookup API (nullable until loaded) ----

    /// <summary>Bundle identifier, e.g. "com.example.app".</summary>
    public string? BundleId { get; set; }

    /// <summary>URL of the 100x100 artwork icon.</summary>
    public string? IconUrl { get; set; }

    /// <summary>URL of the 512x512 artwork icon.</summary>
    public string? IconUrlLarge { get; set; }

    /// <summary>Primary genre, e.g. "Social Networking".</summary>
    public string? Category { get; set; }

    /// <summary>Latest version string on the App Store.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>App Store seller / developer name.</summary>
    public string? Developer { get; set; }

    /// <summary>File size in bytes as reported by the store.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>Minimum supported iOS version.</summary>
    public string? MinimumOsVersion { get; set; }

    /// <summary>Local path to the cached icon file (if downloaded).</summary>
    public string? CachedIconPath { get; set; }

    // ---- Local/account status flags (computed at runtime) ----

    /// <summary>Whether an IPA for this app already exists in the local Apps folder.</summary>
    public bool IsDownloaded { get; set; }

    /// <summary>Path to the local IPA file, when <see cref="IsDownloaded"/> is true.</summary>
    public string? LocalIpaPath { get; set; }

    /// <summary>
    /// License state on the signed-in Apple ID ("signed by the account / iCloud").
    /// </summary>
    public LicenseState License { get; set; } = LicenseState.Unknown;

    /// <summary>Whether the app is already installed on the currently selected device.</summary>
    public bool IsInstalledOnDevice { get; set; }
}

public enum LicenseState
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    /// <summary>The Apple ID owns a license for this app (previously "purchased"/obtained).</summary>
    Owned,

    /// <summary>The Apple ID has no license; a purchase (free "Get") is required first.</summary>
    NotOwned,

    /// <summary>License check failed (network / tool error).</summary>
    CheckFailed,
}
