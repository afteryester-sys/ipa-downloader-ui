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
    // Older settings represented an enabled subscription by its presence in this list.
    public bool AutoUpdateEnabled { get; set; } = true;
    public string? LastBuildId { get; set; }
    public string? LastFilePath { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public DateTimeOffset? NextCheckUtc { get; set; }
    public string? LastFoundVersion { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastDownloadUtc { get; set; }
}

public sealed record FirmwarePendingDownload(
    string ManifestPath,
    string DestinationPath,
    string FileName,
    string Url,
    string? Sha1,
    string? Md5,
    long Total,
    long Downloaded,
    string? DeviceIdentifier,
    string? DeviceName,
    string? FirmwareVersion,
    string? BuildId)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Downloaded * 100d / Total, 0, 100);
}

public sealed class FirmwareDownloadManifest
{
    public int FormatVersion { get; set; }
    public string Url { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public long ExpectedLength { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? Sha1 { get; set; }
    public string? Md5 { get; set; }
    public string? DeviceIdentifier { get; set; }
    public string? DeviceName { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? BuildId { get; set; }
    public List<FirmwareSegment> Segments { get; set; } = new();
}

public sealed class FirmwareSegment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }
    public bool IsComplete { get; set; }
    public string PartPath { get; set; } = "";
}

public sealed record FirmwareDownloadProgress(long Downloaded, long Total, double BytesPerSecond)
{
    public double Percent => Total > 0 ? Math.Clamp(Downloaded * 100d / Total, 0, 100) : 0;
}
