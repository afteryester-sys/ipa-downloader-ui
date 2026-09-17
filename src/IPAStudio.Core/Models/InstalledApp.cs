namespace IPAStudio.Core.Models;

/// <summary>
/// An app that is currently installed on a connected device, as reported by the device
/// itself. Deliberately separate from <see cref="AppEntry"/>: this describes what is on
/// the phone right now, not a catalog entry that could be downloaded.
/// </summary>
public sealed class InstalledApp
{
    public required string BundleId { get; init; }

    /// <summary>Display name shown on the home screen.</summary>
    public required string Name { get; init; }

    /// <summary>User-visible version, when the device reports one.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// App Store item id, present only for apps installed from the store. Lets the app be
    /// re-downloaded without a name search, which would be ambiguous.
    /// </summary>
    public long? StoreItemId { get; init; }

    /// <summary>
    /// Apple ID the app was bought with, when the device discloses it. Only some iOS
    /// versions report this, so a null value means "unknown", never "a different account".
    /// </summary>
    public string? StoreAccount { get; init; }

    /// <summary>True when the device reported store metadata, i.e. not a sideloaded build.</summary>
    public bool IsFromStore => StoreItemId is > 0 || StoreAccount is not null;
}
