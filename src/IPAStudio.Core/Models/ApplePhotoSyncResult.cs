namespace IPAStudio.Core.Models;

public enum ApplePhotoClient
{
    None,
    AppleDevices,
    ITunes,
}

/// <summary>Outcome of preparing media for Apple's supported folder-sync workflow.</summary>
public sealed record ApplePhotoSyncResult
{
    public int Prepared { get; init; }
    public int Total { get; init; }
    public string Folder { get; init; } = string.Empty;
    public ApplePhotoClient Client { get; init; }
    public bool ClientOpened { get; init; }
    public IReadOnlyList<string> SkippedFiles { get; init; } = Array.Empty<string>();
}
