using System.IO;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Live discovery of connected iOS devices via libimobiledevice:
///   idevice_id -l          -> list of connected UDIDs
///   ideviceinfo -u UDID    -> device details (name, model, iOS version, battery)
/// Polls every few seconds and raises DeviceConnected / DeviceDisconnected so the
/// UI can play connect/disconnect animations.
/// </summary>
public sealed class DeviceService : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly Dictionary<string, Device> _devices = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public DeviceService(ToolLocator tools, ProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
    }

    public IReadOnlyCollection<Device> ConnectedDevices
    {
        get { lock (_devices) return _devices.Values.ToList(); }
    }

    public event EventHandler<Device>? DeviceConnected;
    public event EventHandler<Device>? DeviceDisconnected;
    public event EventHandler<Device>? DeviceUpdated;

    /// <summary>Starts background polling for device connections.</summary>
    public void StartMonitoring()
    {
        if (_pollTask is not null) return;
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    public async Task StopMonitoringAsync()
    {
        if (_pollCts is null) return;
        _pollCts.Cancel();
        try { if (_pollTask is not null) await _pollTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _pollCts.Dispose();
        _pollCts = null;
        _pollTask = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // quiet: this fires every few seconds forever; only state changes
                // and failures should reach the default log.
                await PollOnceAsync(ct, quiet: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Tools missing or transient failure; keep polling.
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a single discovery pass. Public for manual "refresh" actions.
    /// </summary>
    /// <param name="quiet">
    /// True for the automatic poll loop, which runs every few seconds: the underlying
    /// tool invocations log at Debug level so they don't flood the log. State changes
    /// (connect/disconnect) are always logged at Info regardless, since those are the
    /// events worth seeing. Manual refreshes pass false and stay fully logged.
    /// </param>
    public async Task PollOnceAsync(CancellationToken ct = default, bool quiet = false)
    {
        var links = await ListDevicesAsync(ct, quiet).ConfigureAwait(false);
        var currentUdids = links.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Record the transport before anything tries to talk to these devices: every
        // later call resolves its own -u/-n arguments from here, so a device discovered
        // on the network has to be registered first or it would be addressed over USB
        // and simply not be found.
        foreach (var (udid, link) in links)
            DeviceTransport.Remember(udid, link);

        List<Device> disconnected;
        List<string> newUdids;
        lock (_devices)
        {
            disconnected = _devices.Values.Where(d => !currentUdids.Contains(d.Udid)).ToList();
            foreach (var device in disconnected)
                _devices.Remove(device.Udid);
            newUdids = currentUdids.Where(u => !_devices.ContainsKey(u)).ToList();
        }

        foreach (var device in disconnected)
        {
            // Always worth a line: this is a real state change, not routine polling.
            AppLog.Info($"Device disconnected: {device.Name} ({device.Udid})");
            DeviceTransport.Forget(device.Udid);
            DeviceDisconnected?.Invoke(this, device);
        }

        foreach (var udid in newUdids)
        {
            // A newly attached device is read in full at Info level even during a quiet
            // poll — this happens once per connection, so it is signal, not noise.
            var device = await ReadDeviceInfoAsync(udid, ct).ConfigureAwait(false);
            device.Link = links[udid];
            lock (_devices) _devices[udid] = device;
            var over = device.Link == DeviceLink.Network ? "Wi-Fi" : "USB";
            AppLog.Info($"Device connected over {over}: {device.Name} — {device.Model}, iOS {device.OsVersion} ({udid})");
            DeviceConnected?.Invoke(this, device);
        }

        // Refresh battery for devices that stayed connected.
        List<Device> existing;
        lock (_devices)
            existing = _devices.Values.Where(d => currentUdids.Contains(d.Udid) && !newUdids.Contains(d.Udid)).ToList();

        foreach (var device in existing)
        {
            var changed = false;

            // Unplugging a phone that is also on Wi-Fi does not disconnect it, it moves it
            // to the other transport. Without noticing that, the card would keep claiming a
            // cable and, worse, later calls would keep being addressed over USB.
            if (links.TryGetValue(device.Udid, out var link) && link != device.Link)
            {
                AppLog.Info($"Device {device.Name} moved to {(link == DeviceLink.Network ? "Wi-Fi" : "USB")}");
                device.Link = link;
                changed = true;
            }

            var battery = await ReadBatteryAsync(device.Udid, ct, quiet).ConfigureAwait(false);
            if (battery != device.BatteryLevel && battery >= 0)
            {
                device.BatteryLevel = battery;
                changed = true;
            }

            if (changed) DeviceUpdated?.Invoke(this, device);
        }
    }

    /// <summary>
    /// Lists every reachable device together with the transport it is reachable on.
    ///
    /// With Wi-Fi off this issues the same USB-only <c>idevice_id -l</c> the app has always
    /// issued, so nothing about the cabled case changes. With Wi-Fi on it asks for both
    /// transports at once; given both flags <c>idevice_id</c> annotates each line with
    /// <c>(USB)</c> or <c>(Network)</c>, which is the only way to learn a device's transport
    /// without probing it, and probing is exactly what needs the answer.
    /// </summary>
    private async Task<Dictionary<string, DeviceLink>> ListDevicesAsync(CancellationToken ct, bool quiet)
    {
        var wantNetwork = DeviceTransport.WifiEnabled;

        if (wantNetwork)
        {
            var both = await _runner
                .RunAsync(_tools.IdeviceIdPath, new[] { "-l", "-n" }, quiet: quiet, ct: ct)
                .ConfigureAwait(false);

            if (both.Success)
                return ParseDeviceList(both.StdOut, annotated: true);

            // An idevice_id predating network support rejects -n outright and exits with a
            // usage error. Falling back keeps such an install working over USB instead of
            // showing no devices at all, which is how this would otherwise fail.
            if (!_networkUnsupportedLogged)
            {
                _networkUnsupportedLogged = true;
                AppLog.Warn("idevice_id rejected -n, so this build cannot see network devices; using USB only");
            }
        }

        var usbOnly = await _runner
            .RunAsync(_tools.IdeviceIdPath, new[] { "-l" }, quiet: quiet, ct: ct)
            .ConfigureAwait(false);

        return ParseDeviceList(usbOnly.StdOut, annotated: false);
    }

    private bool _networkUnsupportedLogged;

    /// <summary>
    /// Parses <c>idevice_id</c> output. Lines are a bare UDID, or a UDID followed by
    /// <c>(USB)</c> / <c>(Network)</c> when both transports were requested.
    /// </summary>
    /// <param name="annotated">
    /// Whether the suffix was expected. A device that is both plugged in and on the network
    /// is listed twice, so when it is, USB wins: it is the faster and steadier transport,
    /// and preferring it means plugging a phone in silently upgrades the connection.
    /// </param>
    private static Dictionary<string, DeviceLink> ParseDeviceList(string stdout, bool annotated)
    {
        var links = new Dictionary<string, DeviceLink>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Take the UDID as the first whitespace-delimited token so the parse is the same
            // whether or not a suffix is present; the old code read the whole line, which
            // would have quietly turned "UDID (USB)" into an unusable identifier.
            var space = line.IndexOf(' ');
            var udid = space < 0 ? line : line[..space].Trim();

            // UDIDs are 40 chars (through iPhone X) or 25 with the dash (iPhone XS onward).
            if (udid.Length < 24) continue;

            var link = annotated && line.Contains("(Network)", StringComparison.OrdinalIgnoreCase)
                ? DeviceLink.Network
                : DeviceLink.Usb;

            if (links.TryGetValue(udid, out var existing) && existing == DeviceLink.Usb) continue;
            links[udid] = link;
        }

        return links;
    }

    /// <summary>
    /// Asks every currently connected device to advertise itself over the network, so that
    /// it can still be found once unplugged.
    ///
    /// This is the step that makes the Wi-Fi setting do anything on a phone that has only
    /// ever been synced by cable: the device, not the computer, decides whether to announce
    /// itself, and that flag defaults to off. It needs a live trusted connection to set, so
    /// it is run when the setting is switched on, while a cable is presumably still in.
    /// </summary>
    /// <returns>How many devices accepted the change.</returns>
    public Task<int> EnableWifiSyncOnConnectedAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            var enabled = 0;
            foreach (var device in ConnectedDevices)
            {
                ct.ThrowIfCancellationRequested();

                if (NativeDevice.TrySetWifiSync(device.Udid, true, out var error))
                    enabled++;
                else
                    AppLog.Warn($"Could not enable Wi-Fi sync on {device.Name}: {error}");
            }
            return enabled;
        }, ct);

    /// <summary>
    /// Whether Apple's Bonjour service is present on this machine.
    ///
    /// A network device is discovered by mDNS and by nothing else: <c>idevice_id -n</c> asks
    /// Bonjour which devices are advertising themselves, so without it the call returns an
    /// empty list and reports no error at all. From the caller's side that is
    /// indistinguishable from "no device on the network", and it is the usual reason an
    /// unplugged phone never appears even when everything else was set up correctly.
    ///
    /// Bonjour arrives with iTunes but is a separate service, and cleanup utilities remove or
    /// disable it routinely — so its absence is worth reporting rather than guessing at.
    ///
    /// Read from the registry rather than through ServiceController: the service registration
    /// is what actually needs checking, and the registry answers it without adding a package
    /// reference. mDNSResponder is Bonjour's own service name.
    /// </summary>
    public static bool IsBonjourInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Bonjour Service");

            if (key is null) return false;

            // Start = 4 is "Disabled". A disabled service will not be running and will not be
            // started on demand, so for discovery purposes it is the same as missing.
            return key.GetValue("Start") is not int start || start != 4;
        }
        catch
        {
            // A locked-down machine can refuse the read. Reporting "present" on an unknown
            // answer keeps this from raising a false alarm on a working setup.
            return true;
        }
    }

    private async Task<Device> ReadDeviceInfoAsync(string udid, CancellationToken ct)
    {
        var device = new Device { Udid = udid };

        // Primary path: a single full-domain dump (fast when it works).
        var info = await RunToolAsync(_tools.IdeviceInfoPath, DeviceTransport.TargetArgs(udid), ct).ConfigureAwait(false);
        if (info is not null)
            ApplyInfoLines(device, info.StdOut);

        // Fallback: on some devices (notably iPhone 15+ / recent iOS) the bundled
        // ideviceinfo returns an EMPTY or partial full-domain dump, while individual
        // keyed reads (-k) still succeed — which is why battery (a keyed read) works
        // but the model/iOS/serial rows come back blank. Fill any core field that is
        // still missing with per-key queries so the info screen always populates.
        await FillMissingCoreFieldsAsync(device, ct).ConfigureAwait(false);

        device.BatteryLevel = await ReadBatteryAsync(udid, ct).ConfigureAwait(false);
        return device;
    }

    /// <summary>Parses "Key: Value" lines from a full ideviceinfo dump into the device.</summary>
    private static void ApplyInfoLines(Device device, string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            switch (key)
            {
                case "DeviceName": device.Name = value; break;
                case "ProductType":
                    device.ProductType = value;
                    device.Model = MapProductType(value);
                    break;
                case "ProductVersion": device.OsVersion = value; break;
                case "DeviceClass": device.DeviceClass = value; break;
                case "SerialNumber": device.SerialNumber = value; break;
                case "InternationalMobileEquipmentIdentity": device.Imei = value; break;
                case "InternationalMobileEquipmentIdentity2": device.Imei2 = value; break;
                case "MobileEquipmentIdentifier": device.Meid = value; break;
                case "PhoneNumber": device.PhoneNumber = value; break;
                case "WiFiAddress": device.WifiAddress = value; break;
                case "BluetoothAddress": device.BluetoothAddress = value; break;
                case "RegionInfo": device.RegionInfo = value; break;
                case "BuildVersion": device.BuildVersion = value; break;
            }
        }
    }

    /// <summary>
    /// Fills any core field left empty by the full dump using individual `-k` key
    /// reads (these keep working on newer iOS where the whole-domain dump fails).
    /// </summary>
    private async Task FillMissingCoreFieldsAsync(Device device, CancellationToken ct)
    {
        var udid = device.Udid;
        var dumpIncomplete = string.IsNullOrEmpty(device.ProductType) || string.IsNullOrEmpty(device.OsVersion);

        if (string.IsNullOrEmpty(device.ProductType))
        {
            var pt = await ReadKeyAsync(udid, null, "ProductType", ct).ConfigureAwait(false);
            if (pt.Length > 0) { device.ProductType = pt; device.Model = MapProductType(pt); }
        }
        if (string.IsNullOrEmpty(device.OsVersion))
        {
            var v = await ReadKeyAsync(udid, null, "ProductVersion", ct).ConfigureAwait(false);
            if (v.Length > 0) device.OsVersion = v;
        }
        if (string.IsNullOrEmpty(device.BuildVersion))
        {
            var b = await ReadKeyAsync(udid, null, "BuildVersion", ct).ConfigureAwait(false);
            if (b.Length > 0) device.BuildVersion = b;
        }
        if (string.IsNullOrEmpty(device.SerialNumber))
        {
            var s = await ReadKeyAsync(udid, null, "SerialNumber", ct).ConfigureAwait(false);
            if (s.Length > 0) device.SerialNumber = s;
        }
        if (string.IsNullOrEmpty(device.Imei))
        {
            var i = await ReadKeyAsync(udid, null, "InternationalMobileEquipmentIdentity", ct).ConfigureAwait(false);
            if (i.Length > 0) device.Imei = i;
        }
        if (string.IsNullOrEmpty(device.Imei2))
        {
            var i2 = await ReadKeyAsync(udid, null, "InternationalMobileEquipmentIdentity2", ct).ConfigureAwait(false);
            if (i2.Length > 0) device.Imei2 = i2;
        }
        if (string.IsNullOrEmpty(device.Meid))
        {
            var m = await ReadKeyAsync(udid, null, "MobileEquipmentIdentifier", ct).ConfigureAwait(false);
            if (m.Length > 0) device.Meid = m;
        }
        if (string.IsNullOrEmpty(device.RegionInfo))
        {
            var r = await ReadKeyAsync(udid, null, "RegionInfo", ct).ConfigureAwait(false);
            if (r.Length > 0) device.RegionInfo = r;
        }
        if (string.IsNullOrEmpty(device.WifiAddress))
        {
            var w = await ReadKeyAsync(udid, null, "WiFiAddress", ct).ConfigureAwait(false);
            if (w.Length > 0) device.WifiAddress = w;
        }
        if (string.IsNullOrEmpty(device.BluetoothAddress))
        {
            var bt = await ReadKeyAsync(udid, null, "BluetoothAddress", ct).ConfigureAwait(false);
            if (bt.Length > 0) device.BluetoothAddress = bt;
        }
        if (string.IsNullOrEmpty(device.PhoneNumber))
        {
            var ph = await ReadKeyAsync(udid, null, "PhoneNumber", ct).ConfigureAwait(false);
            if (ph.Length > 0) device.PhoneNumber = ph;
        }

        // Name and DeviceClass have non-empty defaults ("iPhone"), so only override
        // them with a real keyed read when we know the dump was incomplete.
        if (dumpIncomplete)
        {
            var name = await ReadKeyAsync(udid, null, "DeviceName", ct).ConfigureAwait(false);
            if (name.Length > 0) device.Name = name;

            var dc = await ReadKeyAsync(udid, null, "DeviceClass", ct).ConfigureAwait(false);
            if (dc.Length > 0) device.DeviceClass = dc;
        }
    }

    /// <summary>
    /// Reads a single lockdown value via <c>ideviceinfo -u UDID [-q domain] -k key</c>.
    /// Returns an empty string on any failure/timeout.
    /// </summary>
    private async Task<string> ReadKeyAsync(string udid, string? domain, string key, CancellationToken ct)
    {
        var args = domain is null
            ? DeviceTransport.TargetArgs(udid, "-k", key)
            : DeviceTransport.TargetArgs(udid, "-q", domain, "-k", key);

        var result = await RunToolAsync(_tools.IdeviceInfoPath, args, ct).ConfigureAwait(false);
        if (result is null || !result.Success) return "";
        return result.StdOut.Trim();
    }

    /// <summary>
    /// Runs a libimobiledevice tool with a hard timeout so a hung/unresponsive tool
    /// (e.g. idevicediagnostics on a locked device) can never freeze the UI. Returns
    /// null on timeout; rethrows only when the caller's own token is cancelled.
    /// </summary>
    private async Task<ProcessResult?> RunToolAsync(
        string exe, string[] args, CancellationToken ct, int timeoutSeconds = 12, bool quiet = false)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await _runner.RunAsync(exe, args, quiet: quiet, ct: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout fired — the tool hung. Treat as "no data".
            return null;
        }
    }

    /// <summary>
    /// Fetches extra details that aren't in the default lockdown domain (disk usage,
    /// and a best-effort Apple ID). Safe to call repeatedly; failures are ignored so
    /// the info screen still shows whatever could be read.
    /// </summary>
    public async Task EnrichInfoAsync(Device device, CancellationToken ct = default)
    {
        try
        {
            var disk = await RunToolAsync(
                _tools.IdeviceInfoPath,
                DeviceTransport.TargetArgs(device.Udid, "-q", "com.apple.disk_usage"),
                ct).ConfigureAwait(false);

            // The disk_usage domain exposes several free-space keys that differ a lot:
            //   TotalDataAvailable  – free on the data partition INCLUDING purgeable /
            //                         reserved space; often far larger than reality, so
            //                         a nearly-full phone wrongly looked half-empty.
            //   AmountDataAvailable – the realistic free space, matching what iOS
            //                         Settings → General → iPhone Storage shows.
            // We therefore prefer AmountDataAvailable and only fall back to
            // TotalDataAvailable when the accurate key isn't present.
            long amountAvailable = -1, totalAvailable = -1;
            foreach (var line in (disk?.StdOut ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                switch (key)
                {
                    case "TotalDiskCapacity" when long.TryParse(value, out var total):
                        device.TotalDiskCapacity = total; break;
                    case "AmountDataAvailable" when long.TryParse(value, out var amt):
                        amountAvailable = amt; break;
                    case "TotalDataAvailable" when long.TryParse(value, out var tot):
                        totalAvailable = tot; break;
                }
            }

            var free = amountAvailable >= 0 ? amountAvailable : totalAvailable;
            if (free >= 0) device.FreeDiskSpace = free;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* disk usage domain unavailable */ }

        if (string.IsNullOrEmpty(device.AppleId))
            device.AppleId = await TryReadAppleIdAsync(device.Udid, ct).ConfigureAwait(false);

        await ReadBatteryHealthAsync(device, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads battery health (remaining capacity vs. design capacity) and cycle count
    /// from the device's live IORegistry via idevicediagnostics. This mirrors the
    /// "Maximum Capacity" figure shown in iOS Settings → Battery → Battery Health.
    /// The device must be unlocked and trusted; failures are ignored.
    /// </summary>
    public async Task ReadBatteryHealthAsync(Device device, CancellationToken ct = default)
    {
        try
        {
            // AppleSmartBattery exposes DesignCapacity, AppleRawMaxCapacity,
            // NominalChargeCapacity and CycleCount as a plist. The diagnostics relay can
            // hang on locked/newer devices, so it runs under the timeout wrapper, and it
            // is flaky right after pairing, so empty answers are retried.
            //
            // The tool must actually be present. Older installs shipped without it, and a
            // missing file looks exactly like a locked device from the outside — so check
            // explicitly and say so, instead of blaming the device.
            if (!File.Exists(_tools.IdeviceDiagnosticsPath))
            {
                device.BatteryHealthError = Loc.Get("L.Battery.Error.ToolMissing");
                AppLog.Warn($"Battery health: {_tools.IdeviceDiagnosticsPath} not found");
                return;
            }

            // Newer iOS builds answer under different IORegistry classes: AppleSmartBattery
            // is standard on iPhone 8+, but some report only via the charger node. Try both
            // rather than concluding the data is unavailable after one miss.
            string text = "";
            string lastErr = "";
            var entries = new[] { "AppleSmartBattery", "AppleARMPMUCharger" };

            for (var attempt = 0; attempt < 3 && text.Length == 0; attempt++)
            {
                foreach (var entry in entries)
                {
                    var result = await RunToolAsync(
                        _tools.IdeviceDiagnosticsPath,
                        DeviceTransport.TargetArgs(device.Udid, "ioregentry", entry),
                        ct).ConfigureAwait(false);

                    if (result is null) { lastErr = Loc.Get("L.Battery.Error.Timeout"); continue; }

                    if (result.StdOut.Contains("DesignCapacity", StringComparison.Ordinal))
                    {
                        text = result.StdOut;
                        break;
                    }

                    // Keep whatever the tool complained about; it is the only real clue as
                    // to why this failed, and it is what ends up in front of the user.
                    var err = (result.StdErr ?? "").Trim();
                    if (err.Length > 0) lastErr = err.Length > 120 ? err[..120] : err;
                    else if (result.StdOut.Trim().Length > 0) lastErr = Loc.Get("L.Battery.Error.NoCapacity");
                }

                if (text.Length == 0) await Task.Delay(700, ct).ConfigureAwait(false);
            }

            if (text.Length == 0)
            {
                // Record why. Previously this returned silently, so the screen showed
                // "unlock the device" even when that was not the actual cause.
                device.BatteryHealthError = lastErr.Length > 0 ? lastErr : Loc.Get("L.Battery.Error.NoResponse");
                AppLog.Warn($"Battery health unavailable: {device.BatteryHealthError}");
                return;
            }

            int design = ReadPlistInt(text, "DesignCapacity");
            int rawMax = ReadPlistInt(text, "AppleRawMaxCapacity");
            int nominal = ReadPlistInt(text, "NominalChargeCapacity");
            int cycles = ReadPlistInt(text, "CycleCount");

            // Match the figure iOS shows in Settings -> Battery -> Battery Health.
            //
            // NominalChargeCapacity is the value iOS itself divides by DesignCapacity,
            // so it is the one that agrees with Settings. AppleRawMaxCapacity is the
            // raw fuel-gauge reading and runs a couple of points higher, which is why
            // this screen used to claim 82% for a battery Settings called 80%.
            // Raw is kept only as a fallback for devices that omit the nominal key.
            var maxCap = nominal > 0 ? nominal : rawMax;
            if (design > 0 && maxCap > 0)
                device.BatteryHealthPercent = Math.Clamp((int)Math.Round(100.0 * maxCap / design), 1, 100);
            if (cycles >= 0)
                device.BatteryCycleCount = cycles;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Swallowed before, which is why this failure was invisible in the log.
            device.BatteryHealthError = Loc.Get("L.Battery.Error.ReadFailed");
            AppLog.Warn($"Battery health failed: {ex.Message}");
        }
    }

    /// <summary>Extracts an integer value for a plist &lt;key&gt; from idevicediagnostics XML output.</summary>
    private static int ReadPlistInt(string plist, string key)
    {
        // Matches: <key>KeyName</key>\s*<integer>1234</integer>
        var match = System.Text.RegularExpressions.Regex.Match(
            plist,
            $@"<key>{System.Text.RegularExpressions.Regex.Escape(key)}</key>\s*<integer>(\d+)</integer>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : -1;
    }

    /// <summary>
    /// Best-effort read of the Apple ID associated with the device. Modern iOS hides
    /// this behind privacy protections, so several lockdown domains are probed and the
    /// first value that looks like an email is returned; null when nothing is exposed.
    /// </summary>
    public async Task<string?> TryReadAppleIdAsync(string udid, CancellationToken ct = default)
    {
        // Probe list — ordered from most reliable (iOS 14+) to fallback.
        // The Apple ID key moved between iOS releases; we try every known location.
        string[][] probes =
        {
            // iOS 14+ root-level key (no domain needed)
            DeviceTransport.TargetArgs(udid, "-k", "AppleID"),
            // Older iOS / iPadOS (≤13)
            DeviceTransport.TargetArgs(udid, "-q", "com.apple.mobile.iTunes",       "-k", "AppleID"),
            DeviceTransport.TargetArgs(udid, "-q", "com.apple.mobile.iTunes.store", "-k", "AppleID"),
            DeviceTransport.TargetArgs(udid, "-q", "com.apple.mobile.iTunes",       "-k", "AccountUsername"),
            DeviceTransport.TargetArgs(udid, "-q", "com.apple.mobile.data_sync",    "-k", "AccountName"),
            // Backup-service domain (present on iOS 12-15)
            DeviceTransport.TargetArgs(udid, "-q", "com.apple.mobile.backup",       "-k", "LastiTunesAccountHash"),
            // MobileDeviceCompatibility (works with newer libimobiledevice)
            DeviceTransport.TargetArgs(udid, "-q", "com.apple.MobileDeviceCompatibility", "-k", "AppleID"),
        };

        foreach (var args in probes)
        {
            var result = await RunToolAsync(_tools.IdeviceInfoPath, args, ct).ConfigureAwait(false);
            var value = result?.StdOut.Trim() ?? "";
            if (IsValidEmail(value))
                return value;
        }

        // Last resort: try to read the Apple Account plist via AFC (requires
        // a trusted pair and libimobiledevice's ideviceenterrecovery/afc tool).
        // We shell out to ideviceinfo asking for the whole iTunes domain and parse
        // the text output for any line containing '@'.
        try
        {
            var dump = await RunToolAsync(
                _tools.IdeviceInfoPath,
                DeviceTransport.TargetArgs(udid, "-q", "com.apple.mobile.iTunes"),
                ct).ConfigureAwait(false);

            foreach (var line in (dump?.StdOut ?? "").Split('\n'))
            {
                var trimmed = line.Trim().Trim('"');
                if (IsValidEmail(trimmed))
                    return trimmed;
                // "AppleID: user@example.com" format
                if (trimmed.Contains(':'))
                {
                    var val = trimmed.Split(':', 2).Last().Trim().Trim('"');
                    if (IsValidEmail(val))
                        return val;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* no result */ }

        return null;
    }

    private static bool IsValidEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) &&
        s.Contains('@') &&
        s.Length is > 3 and < 128 &&
        !s.Contains(' ') &&
        !s.StartsWith('-'); // guard against ideviceinfo error strings

    private async Task<int> ReadBatteryAsync(string udid, CancellationToken ct, bool quiet = false)
    {
        var result = await RunToolAsync(
            _tools.IdeviceInfoPath,
            DeviceTransport.TargetArgs(udid, "-q", "com.apple.mobile.battery", "-k", "BatteryCurrentCapacity"),
            ct, quiet: quiet).ConfigureAwait(false);
        return result is not null && int.TryParse(result.StdOut.Trim(), out var level) ? level : -1;
    }

    /// <summary>Maps internal product types to marketing names (common models).</summary>
    private static string MapProductType(string productType) => productType switch
    {
        "iPhone12,1" => "iPhone 11",
        "iPhone12,3" => "iPhone 11 Pro",
        "iPhone12,5" => "iPhone 11 Pro Max",
        "iPhone12,8" => "iPhone SE (2nd gen)",
        "iPhone13,1" => "iPhone 12 mini",
        "iPhone13,2" => "iPhone 12",
        "iPhone13,3" => "iPhone 12 Pro",
        "iPhone13,4" => "iPhone 12 Pro Max",
        "iPhone14,2" => "iPhone 13 Pro",
        "iPhone14,3" => "iPhone 13 Pro Max",
        "iPhone14,4" => "iPhone 13 mini",
        "iPhone14,5" => "iPhone 13",
        "iPhone14,6" => "iPhone SE (3rd gen)",
        "iPhone14,7" => "iPhone 14",
        "iPhone14,8" => "iPhone 14 Plus",
        "iPhone15,2" => "iPhone 14 Pro",
        "iPhone15,3" => "iPhone 14 Pro Max",
        "iPhone15,4" => "iPhone 15",
        "iPhone15,5" => "iPhone 15 Plus",
        "iPhone16,1" => "iPhone 15 Pro",
        "iPhone16,2" => "iPhone 15 Pro Max",
        "iPhone17,1" => "iPhone 16 Pro",
        "iPhone17,2" => "iPhone 16 Pro Max",
        "iPhone17,3" => "iPhone 16",
        "iPhone17,4" => "iPhone 16 Plus",
        "iPhone17,5" => "iPhone 16e",
        "iPhone18,1" => "iPhone 17 Pro",
        "iPhone18,2" => "iPhone 17 Pro Max",
        "iPhone18,3" => "iPhone 17",
        "iPhone18,4" => "iPhone Air",
        _ when productType.StartsWith("iPad", StringComparison.Ordinal) => "iPad",
        _ when productType.StartsWith("iPhone", StringComparison.Ordinal) => "iPhone",
        _ => productType,
    };

    public async ValueTask DisposeAsync() => await StopMonitoringAsync().ConfigureAwait(false);
}
