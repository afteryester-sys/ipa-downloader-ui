using IPAStudio.Core.Tools;

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

    /// <summary>
    /// Remaining battery capacity as a percentage of design capacity (the
    /// "Maximum Capacity" shown in iOS Settings → Battery → Battery Health),
    /// or -1 when it can't be read.
    /// </summary>
    public int BatteryHealthPercent { get; set; } = -1;

    /// <summary>Battery charge cycle count, or -1 when unknown.</summary>
    public int BatteryCycleCount { get; set; } = -1;

    /// <summary>
    /// Why battery health could not be read, in short user-facing wording; empty when it
    /// was read or has not been attempted yet. Lets the info screen state the actual
    /// cause instead of always blaming a locked device.
    /// </summary>
    public string BatteryHealthError { get; set; } = "";

    /// <summary>Device class: iPhone / iPad / iPod.</summary>
    public string DeviceClass { get; set; } = "iPhone";

    /// <summary>Apple ID associated with the device, when it can be read (best effort).</summary>
    public string? AppleId { get; set; }

    /// <summary>Hardware serial number.</summary>
    public string SerialNumber { get; set; } = "";

    /// <summary>IMEI (primary). Empty on Wi-Fi-only iPads / when not exposed.</summary>
    public string Imei { get; set; } = "";

    /// <summary>Second IMEI (dual-SIM / eSIM devices). Empty when absent.</summary>
    public string Imei2 { get; set; } = "";

    /// <summary>MEID (older CDMA identifier). Empty when absent.</summary>
    public string Meid { get; set; } = "";

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

    /// <summary>
    /// Whether this device is currently reached by cable or over the network. Set during
    /// discovery, which is the only place that can tell the two apart, and shown on the
    /// device card so a Wi-Fi device is not mistaken for a cabled one when a slow or
    /// failing operation needs explaining.
    /// </summary>
    public DeviceLink Link { get; set; } = DeviceLink.Usb;

    /// <summary>True when <see cref="Link"/> is a network connection. For XAML binding.</summary>
    public bool IsNetworkLink => Link == DeviceLink.Network;

    /// <summary>True when this device is on a cable. For XAML binding.</summary>
    public bool IsUsbLink => Link == DeviceLink.Usb;

    /// <summary>When the device was first seen in the current session.</summary>
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.Now;
}
