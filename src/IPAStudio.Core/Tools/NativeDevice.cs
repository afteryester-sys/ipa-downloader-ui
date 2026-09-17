using IPAStudio.Core.Diagnostics;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;

namespace IPAStudio.Core.Tools;

/// <summary>
/// Thin wrapper over the parts of the native libimobiledevice bindings that more than one
/// service needs: loading the native libraries once, and opening a device on the transport
/// it was actually discovered on.
///
/// Opening goes through <c>idevice_new_with_options</c> rather than <c>idevice_new</c>,
/// because the latter is hard-wired to usbmux and so cannot see a device that is only on
/// Wi-Fi. For a cabled device the two are equivalent, so this is not a behaviour change for
/// the ordinary case.
/// </summary>
public static class NativeDevice
{
    private static bool _loaded;
    private static readonly object Gate = new();

    /// <summary>Loads the bundled native libraries once per process.</summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            NativeLibraries.Load();
            _loaded = true;
        }
    }

    /// <summary>
    /// Opens a device on its known transport. The caller owns the returned handle.
    /// </summary>
    public static iDeviceError Open(string udid, out iDeviceHandle handle)
    {
        EnsureLoaded();
        return LibiMobileDevice.Instance.iDevice.idevice_new_with_options(
            out handle, udid, DeviceTransport.LookupOptions(udid));
    }

    // Lockdown domain and key iOS uses to decide whether it should announce itself to
    // paired computers over the network. This is the same switch as iTunes/Finder's
    // "Sync with this iPhone over Wi-Fi" checkbox.
    private const string WirelessDomain = "com.apple.mobile.wireless_lockdown";
    private const string WifiConnectionsKey = "EnableWifiConnections";

    /// <summary>
    /// Turns Wi-Fi visibility on for a device that is currently reachable.
    ///
    /// Without this, "look for devices on the network" finds nothing on a phone that has
    /// never been synced wirelessly, because the device itself decides whether to advertise
    /// over Bonjour and the default is off. Setting it needs an existing trusted connection
    /// — in practice a cable — which is why this is offered when a cabled device is present
    /// rather than as a standalone action.
    /// </summary>
    /// <returns>True when the device accepted the change.</returns>
    public static bool TrySetWifiSync(string udid, bool enabled, out string error)
    {
        error = "";
        try
        {
            EnsureLoaded();
            var lockdown = LibiMobileDevice.Instance.Lockdown;
            var plist = LibiMobileDevice.Instance.Plist;

            var opened = Open(udid, out var device);
            if (opened != iDeviceError.Success)
            {
                error = $"idevice_new_with_options: {opened}";
                return false;
            }

            using (device)
            {
                var handshake = lockdown.lockdownd_client_new_with_handshake(device, out var client, "IPAStudio");
                if (handshake != LockdownError.Success)
                {
                    // Almost always an untrusted or locked device; say which call failed so
                    // the log distinguishes that from the device refusing the key itself.
                    error = $"lockdownd handshake: {handshake}";
                    return false;
                }

                using (client)
                {
                    // lockdownd_set_value TAKES OWNERSHIP of the value node: it hands it to
                    // plist_dict_set_item(dict, "Value", value) and then frees that dict, which
                    // frees the value along with it. Wrapping the handle in `using` as well
                    // released the same native node a second time.
                    //
                    // A double free corrupts the native heap; it is not an exception, so the
                    // catch below never saw it and never could have. That is what took the whole
                    // process down when this setting was switched on. Corruption also tends to
                    // surface at some later, unrelated allocation rather than at this line, which
                    // is why the symptom was "the app quits after enabling Wi-Fi" with nothing
                    // useful in the log.
                    var value = plist.plist_new_bool(enabled ? (char)1 : (char)0);
                    var set = lockdown.lockdownd_set_value(client, WirelessDomain, WifiConnectionsKey, value);

                    // The one path that does not consume the value is the argument check at the
                    // top of the native function, where it is still ours to release. On every
                    // other outcome the node is already gone, so the handle is abandoned without
                    // running its release.
                    if (set == LockdownError.InvalidArg) value.Dispose();
                    else value.SetHandleAsInvalid();

                    if (set != LockdownError.Success)
                    {
                        error = $"lockdownd_set_value: {set}";
                        return false;
                    }
                }
            }

            AppLog.Info($"Wi-Fi sync {(enabled ? "enabled" : "disabled")} on device {Short(udid)}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads whether a device currently advertises itself over the network. Returns false
    /// when the value cannot be read at all, which is indistinguishable from "off" as far
    /// as the outcome goes — either way the device will not be found over Wi-Fi.
    /// </summary>
    public static bool IsWifiSyncEnabled(string udid)
    {
        try
        {
            EnsureLoaded();
            var lockdown = LibiMobileDevice.Instance.Lockdown;
            var plist = LibiMobileDevice.Instance.Plist;

            if (Open(udid, out var device) != iDeviceError.Success) return false;
            using (device)
            {
                if (lockdown.lockdownd_client_new_with_handshake(device, out var client, "IPAStudio")
                    != LockdownError.Success) return false;

                using (client)
                {
                    if (lockdown.lockdownd_get_value(client, WirelessDomain, WifiConnectionsKey, out var value)
                        != LockdownError.Success) return false;

                    using (value)
                    {
                        char raw = (char)0;
                        plist.plist_get_bool_val(value, ref raw);
                        return raw != (char)0;
                    }
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Shortens a UDID for log lines, which do not need the whole thing.</summary>
    private static string Short(string udid) => udid.Length > 8 ? udid[..8] + "…" : udid;
}
