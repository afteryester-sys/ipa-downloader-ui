namespace IPAStudio.Core.Models;

/// <summary>
/// The outcome of an import into the device Camera Roll.
///
/// Copying files and having Photos show them are two different things: iOS only picks up
/// DCIM changes when its own importer runs, and on some firmware nothing short of a reboot
/// does that. The import used to report only a copied count, so a transfer Photos never
/// ingested still looked like a success. Each step is reported separately so the UI can say
/// what actually happened.
/// </summary>
public sealed record PhotoImportResult
{
    /// <summary>Files written to DCIM and verified by reading their size back.</summary>
    public int Copied { get; init; }

    /// <summary>Files the user picked.</summary>
    public int Total { get; init; }

    /// <summary>True when the device accepted the request to re-scan DCIM.</summary>
    public bool IndexingRequested { get; init; }

    /// <summary>
    /// True when the copied files were found in the library database afterwards, i.e. Photos
    /// really did ingest them. False means the files are on the device but not in the Camera
    /// Roll yet, which is when a reboot is worth offering.
    /// </summary>
    public bool AppearedInLibrary { get; init; }

    /// <summary>
    /// True when the device offered the private photo-sync service and answered the first
    /// exchange. Recorded for diagnostics: the protocol itself is not implemented, so the
    /// copy path above is what did the work either way.
    /// </summary>
    public bool PhotoSyncAvailable { get; init; }
}
