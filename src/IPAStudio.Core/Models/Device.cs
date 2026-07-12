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

    /// <summary>Apple ID associated with the device, when it can be read (best effort).</summary>
    public string? AppleId { get; set; }

    /// <summary>Hardware serial number.</summary>
    public string SerialNumber { get; set; } = "";

    /// <summary>Phone number of the SIM, when present.</summary>
    public string PhoneNumber { get; set; } = "";

    /// <summary>Wi-Fi MAC address.</summary>
    public string WifiAddress { get; set; } = "";

    /// <summary>Bluetooth MAC address.</summary>
    public string BluetoothAddress { get; set; } = "";

    /// <summary>Total disk capacity in bytes, or -1 when unknown.</summary>
    public long TotalDiskCapacity { get; set; } = -1;

    /// <summary>Free disk space in bytes, or -1 when unknown.</summary>
    public long FreeDiskSpace { get; set; } = -1;

    /// <summary>Region info / sold-in region, e.g. "LL/A".</summary>
    public string RegionInfo { get; set; } = "";

    /// <summary>Build version, e.g. "21E236".</summary>
    public string BuildVersion { get; set; } = "";

    /// <summary>When the device was first seen in the current session.</summary>
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.Now;
}
