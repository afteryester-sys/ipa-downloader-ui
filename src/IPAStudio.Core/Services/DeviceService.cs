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

        var info = await _runner.RunAsync(_tools.IdeviceInfoPath, new[] { "-u", udid }, ct: ct).ConfigureAwait(false);
        foreach (var line in info.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
                case "PhoneNumber": device.PhoneNumber = value; break;
                case "WiFiAddress": device.WifiAddress = value; break;
                case "BluetoothAddress": device.BluetoothAddress = value; break;
                case "RegionInfo": device.RegionInfo = value; break;
                case "BuildVersion": device.BuildVersion = value; break;
            }
        }

        device.BatteryLevel = await ReadBatteryAsync(udid, ct).ConfigureAwait(false);
        return device;
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
            var disk = await _runner.RunAsync(
                _tools.IdeviceInfoPath,
                new[] { "-u", device.Udid, "-q", "com.apple.disk_usage" },
                ct: ct).ConfigureAwait(false);

            foreach (var line in disk.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                switch (key)
                {
                    case "TotalDiskCapacity" when long.TryParse(value, out var total):
                        device.TotalDiskCapacity = total; break;
                    case "TotalDataAvailable" when long.TryParse(value, out var free):
                        device.FreeDiskSpace = free; break;
                }
            }
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
            // NominalChargeCapacity and CycleCount as a plist.
            var result = await _runner.RunAsync(
                _tools.IdeviceDiagnosticsPath,
                new[] { "-u", device.Udid, "ioregentry", "AppleSmartBattery" },
                ct: ct).ConfigureAwait(false);

            var text = result.StdOut;

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
            try
            {
                var result = await _runner.RunAsync(_tools.IdeviceInfoPath, args, ct: ct).ConfigureAwait(false);
                var value = result.StdOut.Trim();
                if (IsValidEmail(value))
                    return value;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try the next probe */ }
        }

        // Last resort: try to read the Apple Account plist via AFC (requires
        // a trusted pair and libimobiledevice's ideviceenterrecovery/afc tool).
        // We shell out to ideviceinfo asking for the whole iTunes domain and parse
        // the text output for any line containing '@'.
        try
        {
            var dump = await _runner.RunAsync(
                _tools.IdeviceInfoPath,
                new[] { "-u", udid, "-q", "com.apple.mobile.iTunes" },
                ct: ct).ConfigureAwait(false);

            foreach (var line in dump.StdOut.Split('\n'))
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
        try
        {
            var result = await _runner.RunAsync(
                _tools.IdeviceInfoPath,
                new[] { "-u", udid, "-q", "com.apple.mobile.battery", "-k", "BatteryCurrentCapacity" },
                ct: ct).ConfigureAwait(false);
            return int.TryParse(result.StdOut.Trim(), out var level) ? level : -1;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return -1;
        }
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
        _ when productType.StartsWith("iPad", StringComparison.Ordinal) => "iPad",
        _ when productType.StartsWith("iPhone", StringComparison.Ordinal) => "iPhone",
        _ => productType,
    };

    public async ValueTask DisposeAsync() => await StopMonitoringAsync().ConfigureAwait(false);
}
