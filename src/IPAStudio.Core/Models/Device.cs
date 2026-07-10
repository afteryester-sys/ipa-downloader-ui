namespace IPAStudio.Core.Models;

/// <summary>
/// A connected iOS device discovered via libimobiledevice (idevice_id / ideviceinfo).
/// </summary>
public sealed class Device
{
    /// <summary>Unique device identifier (UDID).</summary>
    public required string Udid { get; init; }

    /// <summary>User-visible device name, e.g. "Ivan's iPhone".</summary>
    public string Name { get; set; } = "iPhone";

    /// <summary>Marketing model, e.g. "iPhone 15 Pro".</summary>
    public string Model { get; set; } = "";

    /// <summary>Internal product type, e.g. "iPhone16,1".</summary>
    public string ProductType { get; set; } = "";

    /// <summary>iOS version, e.g. "17.4.1".</summary>
    public string OsVersion { get; set; } = "";

    /// <summary>Battery level 0-100, or -1 when unknown.</summary>
    public int BatteryLevel { get; set; } = -1;

    /// <summary>Device class: iPhone / iPad / iPod.</summary>
    public string DeviceClass { get; set; } = "iPhone";

    /// <summary>When the device was first seen in the current session.</summary>
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.Now;
}
