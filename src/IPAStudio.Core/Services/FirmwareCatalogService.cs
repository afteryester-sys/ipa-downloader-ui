using System.Collections.Concurrent;
using System.Text.Json;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

public sealed class FirmwareCatalogService
{
    private const string ApiBase = "https://api.ipsw.me/v4";
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(4);
    private readonly ConcurrentDictionary<string, FirmwareDeviceDetails> _deviceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IPAStudio", "firmware-devices.json");
    private readonly string _deviceCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IPAStudio", "firmware-cache");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public FirmwareCatalogService(HttpClient http)
    {
        _http = http;
        try { Directory.CreateDirectory(_deviceCacheDir); } catch { }
    }

    public FirmwareDeviceDetails? GetCachedDevice(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        if (_deviceCache.TryGetValue(identifier, out var cached)) return cached;

        var diskPath = Path.Combine(_deviceCacheDir, $"{identifier}.json");
        if (File.Exists(diskPath))
        {
            try
            {
                var json = File.ReadAllText(diskPath);
                var details = JsonSerializer.Deserialize<FirmwareDeviceDetails>(json, JsonOptions);
                if (details is not null)
                {
                    _deviceCache[identifier] = details;
                    return details;
                }
            }
            catch { }
        }
        return null;
    }

    public async Task<IReadOnlyList<FirmwareDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                using var response = await _http.GetAsync($"{ApiBase}/devices", ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var devices = JsonSerializer.Deserialize<List<FirmwareDevice>>(json, JsonOptions) ?? new();
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                await File.WriteAllTextAsync(_cachePath, json, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
                return devices.OrderBy(d => d.Name).ThenBy(d => d.Identifier).ToList();
            }
            catch when (!ct.IsCancellationRequested && File.Exists(_cachePath))
            {
                var cached = await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
                return (JsonSerializer.Deserialize<List<FirmwareDevice>>(cached, JsonOptions) ?? new())
                    .OrderBy(d => d.Name).ThenBy(d => d.Identifier).ToList();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<FirmwareDeviceDetails> GetDeviceAsync(string identifier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identifier)) throw new ArgumentException("Device identifier is required.", nameof(identifier));

        // Fast path: if already cached in memory, return immediately if cancellation is requested
        if (_deviceCache.TryGetValue(identifier, out var inMem) && ct.IsCancellationRequested)
            return inMem;

        var diskPath = Path.Combine(_deviceCacheDir, $"{identifier}.json");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                using var response = await _http.GetAsync($"{ApiBase}/device/{Uri.EscapeDataString(identifier)}?type=ipsw", ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var details = JsonSerializer.Deserialize<FirmwareDeviceDetails>(json, JsonOptions)
                    ?? throw new InvalidDataException("IPSW API returned an empty device response.");

                _deviceCache[identifier] = details;
                try
                {
                    Directory.CreateDirectory(_deviceCacheDir);
                    await File.WriteAllTextAsync(diskPath, json, new System.Text.UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
                }
                catch { }

                return details;
            }
            catch (Exception)
            {
                // Fallback to in-memory or disk cache on any network/timeout/cancellation error
                var cached = GetCachedDevice(identifier);
                if (cached is not null) return cached;
                throw;
            }
        }
        finally { _gate.Release(); }
    }
}
