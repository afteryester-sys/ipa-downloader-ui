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
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Tools missing or transient failure; keep polling.
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a single discovery pass. Public for manual "refresh" actions.</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(_tools.IdeviceIdPath, new[] { "-l" }, ct: ct).ConfigureAwait(false);

        var currentUdids = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.Length >= 24) // UDIDs are 40 (old) or 25 (dash format) chars
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            DeviceDisconnected?.Invoke(this, device);

        foreach (var udid in newUdids)
        {
            var device = await ReadDeviceInfoAsync(udid, ct).ConfigureAwait(false);
            lock (_devices) _devices[udid] = device;
            DeviceConnected?.Invoke(this, device);
        }

        // Refresh battery for devices that stayed connected.
        List<Device> existing;
        lock (_devices)
            existing = _devices.Values.Where(d => currentUdids.Contains(d.Udid) && !newUdids.Contains(d.Udid)).ToList();

        foreach (var device in existing)
        {
            var battery = await ReadBatteryAsync(device.Udid, ct).ConfigureAwait(false);
            if (battery != device.BatteryLevel && battery >= 0)
            {
                device.BatteryLevel = battery;
                DeviceUpdated?.Invoke(this, device);
            }
        }
    }

    private async Task<Device> ReadDeviceInfoAsync(string udid, CancellationToken ct)
    {
        var device = new Device { Udid = udid };

        // Primary path: a single full-domain dump (fast when it works).
        var info = await RunToolAsync(_tools.IdeviceInfoPath, new[] { "-u", udid }, ct).ConfigureAwait(false);
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
            ? new[] { "-u", udid, "-k", key }
            : new[] { "-u", udid, "-q", domain, "-k", key };

        var result = await RunToolAsync(_tools.IdeviceInfoPath, args, ct).ConfigureAwait(false);
        if (result is null || !result.Success) return "";
        return result.StdOut.Trim();
    }

    /// <summary>
    /// Runs a libimobiledevice tool with a hard timeout so a hung/unresponsive tool
    /// (e.g. idevicediagnostics on a locked device) can never freeze the UI. Returns
    /// null on timeout; rethrows only when the caller's own token is cancelled.
    /// </summary>
    private async Task<ProcessResult?> RunToolAsync(string exe, string[] args, CancellationToken ct, int timeoutSeconds = 12)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await _runner.RunAsync(exe, args, ct: timeoutCts.Token).ConfigureAwait(false);
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
                new[] { "-u", device.Udid, "-q", "com.apple.disk_usage" },
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
            // NominalChargeCapacity and CycleCount as a plist. This diagnostics relay
            // can hang on locked/newer devices, so it runs under the timeout wrapper.
            // It is also flaky right after pairing/unlock, so retry a couple of times
            // when the first attempt comes back empty.
            string text = "";
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await RunToolAsync(
                    _tools.IdeviceDiagnosticsPath,
                    new[] { "-u", device.Udid, "ioregentry", "AppleSmartBattery" },
                    ct).ConfigureAwait(false);
                if (result is not null && result.StdOut.Contains("DesignCapacity", StringComparison.Ordinal))
                {
                    text = result.StdOut;
                    break;
                }
                await Task.Delay(700, ct).ConfigureAwait(false);
            }
            if (text.Length == 0) return; // relay unavailable — leave health unknown

            int design = ReadPlistInt(text, "DesignCapacity");
            int rawMax = ReadPlistInt(text, "AppleRawMaxCapacity");
            int nominal = ReadPlistInt(text, "NominalChargeCapacity");
            int cycles = ReadPlistInt(text, "CycleCount");

            // Prefer AppleRawMaxCapacity; fall back to NominalChargeCapacity.
            var maxCap = rawMax > 0 ? rawMax : nominal;
            if (design > 0 && maxCap > 0)
                device.BatteryHealthPercent = Math.Clamp((int)Math.Round(100.0 * maxCap / design), 1, 100);
            if (cycles >= 0)
                device.BatteryCycleCount = cycles;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* diagnostics relay unavailable (locked / untrusted / tool missing) */ }
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
            new[] { "-u", udid, "-k", "AppleID" },
            // Older iOS / iPadOS (≤13)
            new[] { "-u", udid, "-q", "com.apple.mobile.iTunes",       "-k", "AppleID" },
            new[] { "-u", udid, "-q", "com.apple.mobile.iTunes.store", "-k", "AppleID" },
            new[] { "-u", udid, "-q", "com.apple.mobile.iTunes",       "-k", "AccountUsername" },
            new[] { "-u", udid, "-q", "com.apple.mobile.data_sync",    "-k", "AccountName" },
            // Backup-service domain (present on iOS 12-15)
            new[] { "-u", udid, "-q", "com.apple.mobile.backup",       "-k", "LastiTunesAccountHash" },
            // MobileDeviceCompatibility (works with newer libimobiledevice)
            new[] { "-u", udid, "-q", "com.apple.MobileDeviceCompatibility", "-k", "AppleID" },
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
                new[] { "-u", udid, "-q", "com.apple.mobile.iTunes" },
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

    private async Task<int> ReadBatteryAsync(string udid, CancellationToken ct)
    {
        var result = await RunToolAsync(
            _tools.IdeviceInfoPath,
            new[] { "-u", udid, "-q", "com.apple.mobile.battery", "-k", "BatteryCurrentCapacity" },
            ct).ConfigureAwait(false);
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
