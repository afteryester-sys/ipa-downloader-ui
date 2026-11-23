namespace IPAStudio.Core.Models;

/// <summary>
/// Outcome of a native AirTraffic tethered photo synchronization session.
/// </summary>
public sealed record AirTrafficSyncResult
{
    /// <summary>Number of files successfully staged and registered.</summary>
    public int Copied { get; init; }

    /// <summary>Total number of candidate files requested.</summary>
    public int Total { get; init; }

    /// <summary>True if the AirTrafficHost sync connection was established and notified.</summary>
    public bool AirTrafficNotified { get; init; }

    /// <summary>True if staging and manifests were committed without fatal error.</summary>
    public bool Success { get; init; }

    /// <summary>Name of the album the photos were grouped into on the device.</summary>
    public string AlbumName { get; init; } = "IPA Studio";

    /// <summary>Any warning or failure description.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>List of file names that were processed.</summary>
    public IReadOnlyList<string> StagedFiles { get; init; } = Array.Empty<string>();
}
