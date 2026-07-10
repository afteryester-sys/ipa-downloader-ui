using System.Reflection;
using System.Text.Json;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Loads the bundled catalog (Apps_ID_List.txt from the IPA_Downloader repo,
/// ~570 apps in "Name: AppStoreID" format) and enriches it with metadata and
/// icons from the public iTunes Lookup API. Results are cached on disk so the
/// catalog appears instantly on subsequent launches.
/// </summary>
public sealed class CatalogService
{
    private const string ResourceName = "IPAStudio.Core.Resources.Apps_ID_List.txt";
    private const int LookupBatchSize = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ToolLocator _tools;
    private readonly HttpClient _http;

    public CatalogService(ToolLocator tools, HttpClient http)
    {
        _tools = tools;
        _http = http;
    }

    /// <summary>Raised for each batch of apps whose metadata was refreshed.</summary>
    public event EventHandler<IReadOnlyList<AppEntry>>? MetadataUpdated;

    /// <summary>
    /// Parses the embedded catalog file into bare entries (name + ID), sorted by name.
    /// </summary>
    public IReadOnlyList<AppEntry> LoadBundledCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream);

        var entries = new List<AppEntry>();
        while (reader.ReadLine() is { } line)
        {
            var idx = line.LastIndexOf(':');
            if (idx <= 0) continue;

            var name = line[..idx].Trim();
            if (!long.TryParse(line[(idx + 1)..].Trim(), out var id)) continue;

            entries.Add(new AppEntry { Name = name, AppStoreId = id });
        }

        return entries
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Applies the on-disk metadata cache to <paramref name="entries"/>.
    /// Returns true when a cache existed.
    /// </summary>
    public async Task<bool> ApplyCachedMetadataAsync(IReadOnlyList<AppEntry> entries, CancellationToken ct = default)
    {
        if (!File.Exists(_tools.CatalogCacheFile)) return false;

        try
        {
            await using var stream = File.OpenRead(_tools.CatalogCacheFile);
            var cache = await JsonSerializer
                .DeserializeAsync<Dictionary<long, CachedMeta>>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            if (cache is null) return false;

            foreach (var entry in entries)
            {
                if (!cache.TryGetValue(entry.AppStoreId, out var meta)) continue;
                meta.ApplyTo(entry);
                var iconPath = Path.Combine(_tools.IconCacheFolder, $"{entry.AppStoreId}.png");
                if (File.Exists(iconPath)) entry.CachedIconPath = iconPath;
            }
            return true;
        }
        catch
        {
            return false; // Corrupt cache; will be rebuilt on next refresh.
        }
    }

    /// <summary>
    /// Refreshes metadata for all entries from the iTunes Lookup API in batches of 100,
    /// downloads missing icons into the local cache, and persists the metadata cache.
    /// </summary>
    public async Task RefreshMetadataAsync(
        IReadOnlyList<AppEntry> entries,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        _tools.EnsureFolders();
        var byId = entries.ToDictionary(e => e.AppStoreId);
        var ids = entries.Select(e => e.AppStoreId).ToList();
        var processed = 0;

        for (var offset = 0; offset < ids.Count; offset += LookupBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(offset).Take(LookupBatchSize).ToList();
            var updated = new List<AppEntry>();

            try
            {
                var url = $"https://itunes.apple.com/lookup?id={string.Join(',', batch)}&entity=software";
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        if (!item.TryGetProperty("trackId", out var trackId)) continue;
                        if (!byId.TryGetValue(trackId.GetInt64(), out var entry)) continue;

                        entry.BundleId = GetString(item, "bundleId");
                        entry.IconUrl = GetString(item, "artworkUrl100");
                        entry.IconUrlLarge = GetString(item, "artworkUrl512");
                        entry.Category = GetString(item, "primaryGenreName");
                        entry.LatestVersion = GetString(item, "version");
                        entry.Developer = GetString(item, "sellerName");
                        entry.MinimumOsVersion = GetString(item, "minimumOsVersion");
                        if (item.TryGetProperty("fileSizeBytes", out var size))
                            entry.FileSizeBytes = size.ValueKind == JsonValueKind.String
                                ? long.TryParse(size.GetString(), out var parsed) ? parsed : null
                                : size.GetInt64();

                        updated.Add(entry);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Network hiccup for this batch; continue with remaining batches.
            }

            // Download missing icons for this batch (small parallelism).
            await Parallel.ForEachAsync(
                updated.Where(e => e.IconUrl is not null && e.CachedIconPath is null),
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (entry, token) =>
                {
                    var path = Path.Combine(_tools.IconCacheFolder, $"{entry.AppStoreId}.png");
                    if (!File.Exists(path))
                    {
                        try
                        {
                            var bytes = await _http.GetByteArrayAsync(entry.IconUrl!, token).ConfigureAwait(false);
                            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { return; }
                    }
                    entry.CachedIconPath = path;
                }).ConfigureAwait(false);

            processed += batch.Count;
            progress?.Report((double)processed / ids.Count * 100);
            if (updated.Count > 0)
                MetadataUpdated?.Invoke(this, updated);
        }

        await SaveCacheAsync(entries, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks entries whose IPA already exists in the local Apps folder.
    /// File name convention (same as the original project): Name_AppID_Version.ipa
    /// </summary>
    public void RefreshDownloadedFlags(IReadOnlyList<AppEntry> entries)
    {
        _tools.EnsureFolders();
        var files = Directory.EnumerateFiles(_tools.AppsFolder, "*.ipa").ToList();

        foreach (var entry in entries)
        {
            var match = files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f)
                    .Contains($"_{entry.AppStoreId}", StringComparison.Ordinal));
            entry.IsDownloaded = match is not null;
            entry.LocalIpaPath = match;
        }
    }

    private async Task SaveCacheAsync(IReadOnlyList<AppEntry> entries, CancellationToken ct)
    {
        var cache = entries
            .Where(e => e.BundleId is not null)
            .ToDictionary(e => e.AppStoreId, CachedMeta.From);

        await using var stream = File.Create(_tools.CatalogCacheFile);
        await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, ct).ConfigureAwait(false);
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class CachedMeta
    {
        public string? BundleId { get; set; }
        public string? IconUrl { get; set; }
        public string? IconUrlLarge { get; set; }
        public string? Category { get; set; }
        public string? LatestVersion { get; set; }
        public string? Developer { get; set; }
        public long? FileSizeBytes { get; set; }
        public string? MinimumOsVersion { get; set; }

        public static CachedMeta From(AppEntry e) => new()
        {
            BundleId = e.BundleId,
            IconUrl = e.IconUrl,
            IconUrlLarge = e.IconUrlLarge,
            Category = e.Category,
            LatestVersion = e.LatestVersion,
            Developer = e.Developer,
            FileSizeBytes = e.FileSizeBytes,
            MinimumOsVersion = e.MinimumOsVersion,
        };

        public void ApplyTo(AppEntry e)
        {
            e.BundleId = BundleId;
            e.IconUrl = IconUrl;
            e.IconUrlLarge = IconUrlLarge;
            e.Category = Category;
            e.LatestVersion = LatestVersion;
            e.Developer = Developer;
            e.FileSizeBytes = FileSizeBytes;
            e.MinimumOsVersion = MinimumOsVersion;
        }
    }
}
