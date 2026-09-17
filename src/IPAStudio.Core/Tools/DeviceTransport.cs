using System.Collections.Concurrent;

namespace IPAStudio.Core.Tools;

/// <summary>How a device is currently reachable.</summary>
public enum DeviceLink
{
    /// <summary>Attached by cable and reachable through usbmuxd.</summary>
    Usb = 0,

    /// <summary>Reachable over the local network (Wi-Fi sync).</summary>
    Network = 1,
}

/// <summary>
/// Remembers which transport each known device is reachable over, so that every device
/// operation can address it correctly.
///
/// This exists because libimobiledevice never searches both transports on its own. Both
/// the command-line tools and the native library take the transport as an explicit
/// argument and then look in exactly one place: <c>idevice_new</c> is USB-only, and
/// passing <c>-n</c> to a tool makes it network-only. A device paired over Wi-Fi is
/// therefore invisible to every call that does not ask for the network, which is why
/// supporting Wi-Fi has to reach every call site and not just the discovery pass.
///
/// The alternative — threading a transport parameter through every method of
/// DeviceService, InstallService and PhotoService — would mean changing some twenty
/// signatures and would still leave each call site free to forget it. The transport is a
/// property of the machine's current connection to a device rather than of any one call,
/// so it is recorded centrally by DeviceService during discovery and read back by whoever
/// needs to talk to that device.
///
/// Unknown UDIDs resolve to <see cref="DeviceLink.Usb"/>, which is what the code assumed
/// before this type existed; a device that was never discovered therefore behaves exactly
/// as it used to rather than failing in a new way.
/// </summary>
public static class DeviceTransport
{
    private static readonly ConcurrentDictionary<string, DeviceLink> Links =
        new(StringComparer.OrdinalIgnoreCase);

    // Mirrors AppSettings.WifiDeviceConnection. Duplicated here because Core services are
    // constructed without settings on some paths (the setup wizard, for one) and must
    // still be able to answer "should I look on the network at all?".
    private static volatile bool _wifiEnabled;

    /// <summary>
    /// Whether discovery should also look for devices on the local network. Off by
    /// default: while off, discovery issues exactly the same USB-only query it always did,
    /// so the common case cannot regress.
    /// </summary>
    public static bool WifiEnabled
    {
        get => _wifiEnabled;
        set => _wifiEnabled = value;
    }

    /// <summary>Records the transport a device was just discovered on.</summary>
    public static void Remember(string udid, DeviceLink link)
    {
        if (string.IsNullOrEmpty(udid)) return;
        Links[udid] = link;
    }

    /// <summary>Drops a device that is no longer connected.</summary>
    public static void Forget(string udid)
    {
        if (string.IsNullOrEmpty(udid)) return;
        Links.TryRemove(udid, out _);
    }

    /// <summary>Transport for a device; USB for anything not seen during discovery.</summary>
    public static DeviceLink LinkFor(string udid)
        => !string.IsNullOrEmpty(udid) && Links.TryGetValue(udid, out var link) ? link : DeviceLink.Usb;

    public static bool IsNetwork(string udid) => LinkFor(udid) == DeviceLink.Network;

    /// <summary>
    /// The leading arguments that point a libimobiledevice command-line tool at a device:
    /// <c>-u UDID</c>, plus <c>-n</c> when that device is on the network.
    ///
    /// <c>ideviceinfo</c>, <c>ideviceinstaller</c> and <c>idevicediagnostics</c> all accept
    /// <c>-n</c> and all read it the same way — as "network instead of USB", never "as well
    /// as" — so the flag must be added for network devices and withheld for cabled ones.
    /// </summary>
    public static string[] TargetArgs(string udid)
        => IsNetwork(udid)
            ? new[] { "-u", udid, "-n" }
            : new[] { "-u", udid };

    /// <summary>
    /// <see cref="TargetArgs(string)"/> followed by <paramref name="rest"/>, so a call site
    /// can stay a single expression instead of concatenating arrays by hand.
    /// </summary>
    public static string[] TargetArgs(string udid, params string[] rest)
    {
        var target = TargetArgs(udid);
        var args = new string[target.Length + rest.Length];
        target.CopyTo(args, 0);
        rest.CopyTo(args, target.Length);
        return args;
    }

    // IDEVICE_LOOKUP_USBMUX / IDEVICE_LOOKUP_NETWORK from libimobiledevice's
    // idevice_options. The managed bindings declare idevice_new_with_options as taking a
    // plain int, so the values are spelled out here rather than cast from the bindings'
    // UsbmuxLookupOptions, which is a different (usbmuxd) enum that merely happens to
    // share these numbers.
    private const int LookupUsbmuxOption = 1 << 1;   // 2
    private const int LookupNetworkOption = 1 << 2;  // 4

    /// <summary>
    /// Lookup bitmask for <c>idevice_new_with_options</c>. Passing the usbmux option for a
    /// cabled device makes that call exactly equivalent to the plain <c>idevice_new</c> it
    /// replaces, so cabled behaviour is unchanged.
    /// </summary>
    public static int LookupOptions(string udid)
        => IsNetwork(udid) ? LookupNetworkOption : LookupUsbmuxOption;
}
