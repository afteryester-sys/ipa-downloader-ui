using System.Text.Json;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

public sealed class FirmwareCatalogService
{
    private const string ApiBase = "https://api.ipsw.me/v4";
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(3);
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IPAStudio", "firmware-devices.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public FirmwareCatalogService(HttpClient http) => _http = http;

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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await _http.GetAsync($"{ApiBase}/device/{Uri.EscapeDataString(identifier)}?type=ipsw", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<FirmwareDeviceDetails>(stream, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("IPSW API returned an empty device response.");
        }
        finally { _gate.Release(); }
    }
}
