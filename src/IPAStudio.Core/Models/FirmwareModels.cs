using System.Text.Json.Serialization;

namespace IPAStudio.Core.Models;

public class FirmwareDevice
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("boardconfig")] public string? BoardConfig { get; set; }
    [JsonIgnore] public string DisplayName => $"{Name} ({Identifier})";
}

public sealed class FirmwareDeviceDetails : FirmwareDevice
{
    [JsonPropertyName("firmwares")] public List<FirmwareRelease> Firmwares { get; set; } = new();
}

public sealed class FirmwareRelease
{
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("buildid")] public string BuildId { get; set; } = "";
    [JsonPropertyName("sha1sum")] public string? Sha1 { get; set; }
    [JsonPropertyName("md5sum")] public string? Md5 { get; set; }
    [JsonPropertyName("filesize")] public long FileSize { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("releasedate")] public DateTimeOffset? ReleaseDate { get; set; }
    [JsonPropertyName("uploaddate")] public DateTimeOffset? UploadDate { get; set; }
    [JsonPropertyName("signed")] public bool Signed { get; set; }
    [JsonIgnore] public string SizeText => FileSize <= 0 ? "—" : $"{FileSize / 1024d / 1024d / 1024d:F2} GB";
    [JsonIgnore] public string StatusText => Signed ? "Signed" : "Unsigned";
}

public sealed class FirmwareSubscription
{
    public string Identifier { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? LastBuildId { get; set; }
    public string? LastFilePath { get; set; }
    /// <summary>When the auto-updater last asked Apple about this model.</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }
    /// <summary>When a firmware for this model was last downloaded to the end.</summary>
    public DateTimeOffset? LastDownloadUtc { get; set; }
}

/// <summary>An interrupted download found on disk, described purely by its manifest.</summary>
public sealed record FirmwarePendingDownload(
    string ManifestPath,
    string DestinationPath,
    string FileName,
    string Url,
    string? Sha1,
    long Total,
    long Downloaded)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Downloaded * 100d / Total, 0, 100);
}

public sealed class FirmwareDownloadManifest
{
    public string Url { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public long ExpectedLength { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? Sha1 { get; set; }
    public List<FirmwareSegment> Segments { get; set; } = new();
}

public sealed class FirmwareSegment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }
    public string PartPath { get; set; } = "";
}

public sealed record FirmwareDownloadProgress(long Downloaded, long Total, double BytesPerSecond)
{
    public double Percent => Total > 0 ? Downloaded * 100d / Total : 0;
}
