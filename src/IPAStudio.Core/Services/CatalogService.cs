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
    /// The catalog as the user sees it: the bundled list plus anything they added by
    /// hand from the download screen.
    ///
    /// User entries win on a duplicate id so a hand-added app keeps the name the user
    /// saw when adding it.
    /// </summary>
    public IReadOnlyList<AppEntry> LoadCatalog()
    {
        var entries = LoadBundledCatalog().ToList();
        var user = LoadUserCatalog();
        if (user.Count == 0) return entries;

        var byId = new Dictionary<long, AppEntry>();
        foreach (var entry in entries) byId[entry.AppStoreId] = entry;
        foreach (var entry in user) byId[entry.AppStoreId] = entry;

        return byId.Values
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Reads the hand-added apps. Returns empty when absent or unreadable.</summary>
    public IReadOnlyList<AppEntry> LoadUserCatalog()
    {
        if (!File.Exists(_tools.UserCatalogFile)) return Array.Empty<AppEntry>();
        try
        {
            using var stream = File.OpenRead(_tools.UserCatalogFile);
            var stored = JsonSerializer.Deserialize<List<UserApp>>(stream, JsonOptions);
            if (stored is null) return Array.Empty<AppEntry>();

            return stored
                .Where(a => a.AppStoreId > 0 && !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => new AppEntry
                {
                    Name = a.Name!,
                    AppStoreId = a.AppStoreId,
                    BundleId = a.BundleId,
                    IconUrl = a.IconUrl,
                    IconUrlLarge = a.IconUrlLarge,
                    Category = a.Category,
                    LatestVersion = a.LatestVersion,
                    Developer = a.Developer,
                    FileSizeBytes = a.FileSizeBytes,
                    MinimumOsVersion = a.MinimumOsVersion,
                })
                .ToList();
        }
        catch
        {
            return Array.Empty<AppEntry>(); // Corrupt file: behave as if empty.
        }
    }

    /// <summary>True when the id is already in the catalog (bundled or hand-added).</summary>
    public bool IsInCatalog(long appStoreId)
        => LoadBundledCatalog().Any(e => e.AppStoreId == appStoreId)
           || LoadUserCatalog().Any(e => e.AppStoreId == appStoreId);

    /// <summary>
    /// Adds an app to the user catalog and pins its icon so it renders immediately.
    /// Returns false when it was already present.
    /// </summary>
    public async Task<bool> AddToUserCatalogAsync(AppEntry entry, CancellationToken ct = default)
    {
        if (IsInCatalog(entry.AppStoreId)) return false;

        _tools.EnsureFolders();

        var stored = LoadUserCatalog().Select(UserApp.From).ToList();
        stored.Add(UserApp.From(entry));

        await using (var stream = File.Create(_tools.UserCatalogFile))
        {
            await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, ct).ConfigureAwait(false);
        }

        // Pin the icon now, while we still hold the artwork URL from the lookup that
        // found this app. Without this the entry would sit icon-less until the next
        // full metadata refresh.
        await TryPinIconAsync(entry, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Downloads and pins one entry's icon. Failure is not an error.</summary>
    private async Task TryPinIconAsync(AppEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.IconUrl)) return;

        var path = Path.Combine(_tools.IconCacheFolder, $"{entry.AppStoreId}.png");
        try
        {
            if (!File.Exists(path))
            {
                var bytes = await _http.GetByteArrayAsync(entry.IconUrl, ct).ConfigureAwait(false);
                var temp = $"{path}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            entry.CachedIconPath = path;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* icon is cosmetic; the entry is already saved */ }
    }

    /// <summary>
    /// Applies the on-disk metadata cache to <paramref name="entries"/>.
    /// Returns true when a cache existed.
    /// </summary>
    public async Task<bool> ApplyCachedMetadataAsync(IReadOnlyList<AppEntry> entries, CancellationToken ct = default)
    {
        // Icons are pinned on disk under their own file name, so they survive a missing
        // or corrupt metadata cache. Attaching them first is what makes the catalog show
        // artwork on every launch: previously this ran inside the cache loop below, so a
        // missing catalog-cache.json returned early and the whole catalog rendered
        // icon-less even though every icon was already downloaded.
        AttachCachedIcons(entries);

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

            // The Lookup API answers per storefront: ids not sold in the queried
            // country are simply absent from the response. Querying only the default
            // (US) storefront therefore left apps published elsewhere with no
            // metadata and no size at all. Ids still unresolved after a storefront
            // are retried against the next one.
            var pending = new HashSet<long>(batch);

            foreach (var storefront in ItunesStorefront.Candidates)
            {
                if (pending.Count == 0) break;
                ct.ThrowIfCancellationRequested();

                try
                {
                    var url = $"https://itunes.apple.com/lookup?id={string.Join(',', pending)}&entity=software"
                              + ItunesStorefront.CountryParam(storefront);
                    using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

                    if (doc.RootElement.TryGetProperty("results", out var results))
                    {
                        foreach (var item in results.EnumerateArray())
                        {
                            if (!item.TryGetProperty("trackId", out var trackId)) continue;
                            var id = trackId.GetInt64();
                            if (!byId.TryGetValue(id, out var entry)) continue;
                            // Already filled from an earlier storefront.
                            if (!pending.Remove(id)) continue;

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
                    // Network hiccup on this storefront; fall through to the next and,
                    // failing that, on to the remaining batches.
                }
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

                            // Write to a private temp name and move into place. The catalog
                            // is loaded by more than one screen, so two refreshes can target
                            // the same icon at once; writing in place let them collide and
                            // one of them would give up, leaving that app icon-less. A move
                            // is atomic, so the loser simply overwrites with identical bytes.
                            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
                            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
                            File.Move(temp, path, overwrite: true);
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
    /// Looks up an app in the App Store by its Bundle ID using the iTunes Lookup API.
    /// Returns the found entries (usually 0 or 1).
    /// </summary>
    public Task<IReadOnlyList<AppEntry>> SearchByBundleIdAsync(
        string bundleId,
        CancellationToken ct = default)
        => LookupAsync($"bundleId={Uri.EscapeDataString(bundleId)}", bundleId, ct);

    /// <summary>
    /// Looks up an app by its numeric App Store id — the id a user gets from a store
    /// link (apps.apple.com/…/id389801252) or from the Share sheet.
    /// </summary>
    public Task<IReadOnlyList<AppEntry>> LookupByAppStoreIdAsync(
        long appStoreId,
        CancellationToken ct = default)
        => LookupAsync($"id={appStoreId}", appStoreId.ToString(), ct);

    /// <summary>
    /// Resolves whatever the user typed (bundle id, numeric id or store link) into
    /// catalog entries. Unrecognized input yields an empty list rather than a network call.
    /// </summary>
    public async Task<IReadOnlyList<AppEntry>> FindAsync(AppQuery query, CancellationToken ct = default)
    {
        // Typed explicitly: the arms are IReadOnlyList and AppEntry[], and leaving the
        // compiler to reconcile them is a needless way to break the build.
        IReadOnlyList<AppEntry> found = query.Kind switch
        {
            AppQueryKind.BundleId   => await SearchByBundleIdAsync(query.BundleId!, ct).ConfigureAwait(false),
            AppQueryKind.AppStoreId => await LookupByAppStoreIdAsync(query.AppStoreId, ct).ConfigureAwait(false),
            _                       => Array.Empty<AppEntry>(),
        };

        if (found.Count > 0) return found;

        // Nothing in any storefront. That is not the same as "no such app": the lookup API
        // lists only what is currently on sale, so an app pulled from sale, limited to a
        // storefront, or never listed publicly comes back empty here while the App Store
        // still serves it to an Apple ID that owns it. Reporting it as non-existent was
        // wrong for exactly the apps a user is most likely to be rescuing.
        //
        // So the identifier the user gave is carried forward as a provisional entry and the
        // download is allowed to be the judge — it talks to the authenticated store, which
        // is the only thing that actually knows. A numeric id is enough on its own; a bundle
        // id makes ipatool resolve it through the store, which may still fail, but failing
        // at the download says something true instead of guessing beforehand.
        var provisional = ProvisionalEntry(query);
        return provisional is null ? found : new[] { provisional };
    }

    /// <summary>
    /// An entry standing in for an app the public catalog does not list, built only from
    /// what the user typed. Null when the query names nothing identifiable.
    /// </summary>
    private static AppEntry? ProvisionalEntry(AppQuery query) => query.Kind switch
    {
        AppQueryKind.BundleId => new AppEntry
        {
            // No name is known, so the identifier is shown rather than inventing one.
            Name = query.BundleId!,
            AppStoreId = 0,
            BundleId = query.BundleId,
            IsProvisional = true,
        },
        AppQueryKind.AppStoreId => new AppEntry
        {
            Name = query.AppStoreId.ToString(),
            AppStoreId = query.AppStoreId,
            IsProvisional = true,
        },
        _ => null,
    };

    /// <summary>
    /// Shared iTunes Lookup call. <paramref name="queryParam"/> is the already-escaped
    /// selector ("bundleId=..." or "id=..."); <paramref name="fallbackName"/> is used as
    /// the display name when Apple returns an entry without one.
    /// </summary>
    private async Task<IReadOnlyList<AppEntry>> LookupAsync(
        string queryParam,
        string fallbackName,
        CancellationToken ct = default)
    {
        var results = new List<AppEntry>();

        // Per-storefront API: an app absent from the queried country yields no rows,
        // so stopping at the default (US) storefront made region-limited apps look
        // like they simply don't exist. First storefront with a hit wins.
        foreach (var storefront in ItunesStorefront.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var url = $"https://itunes.apple.com/lookup?{queryParam}&entity=software"
                          + ItunesStorefront.CountryParam(storefront);
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("results", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (!item.TryGetProperty("trackId", out var trackId)) continue;
                        var name = GetString(item, "trackName") ?? GetString(item, "trackCensoredName") ?? fallbackName;
                        var entry = new AppEntry
                        {
                            Name = name,
                            AppStoreId = trackId.GetInt64(),
                            BundleId = GetString(item, "bundleId"),
                            Category = GetString(item, "primaryGenreName"),
                            LatestVersion = GetString(item, "version"),
                            Developer = GetString(item, "sellerName"),
                            IconUrl = GetString(item, "artworkUrl100"),
                            MinimumOsVersion = GetString(item, "minimumOsVersion"),
                        };
                        if (item.TryGetProperty("fileSizeBytes", out var size))
                            entry.FileSizeBytes = size.ValueKind == JsonValueKind.String
                                ? long.TryParse(size.GetString(), out var parsed) ? parsed : null
                                : size.GetInt64();
                        results.Add(entry);
                    }
                }

                if (results.Count > 0) return results;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* network failure on this storefront — try the next */ }
        }

        return results;
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
            // Keeping both copies of a re-download means one app can now own several
            // files ("App_123_1.0.ipa", "App_123_1.0 (2).ipa", …). Enumeration order is
            // not defined, so picking the first match could hand the installer an older
            // build; always take the most recently written one.
            var match = files
                .Where(f => Path.GetFileNameWithoutExtension(f)
                    .Contains($"_{entry.AppStoreId}", StringComparison.Ordinal))
                .OrderByDescending(SafeLastWriteUtc)
                .FirstOrDefault();
            entry.IsDownloaded = match is not null;
            entry.LocalIpaPath = match;
        }
    }

    /// <summary>
    /// Last write time, or <see cref="DateTime.MinValue"/> if the file vanished between
    /// the directory listing and this call (a download finishing concurrently, say).
    /// Sorting must never throw.
    /// </summary>
    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Points every entry at its pinned icon file, when one has already been downloaded.
    ///
    /// Reads the icon directory once instead of probing per entry, and is safe to call
    /// before any network work: icons are cached by App Store id, so a name or metadata
    /// change never invalidates them.
    /// </summary>
    public void AttachCachedIcons(IReadOnlyList<AppEntry> entries)
    {
        if (!Directory.Exists(_tools.IconCacheFolder)) return;

        var onDisk = new Dictionary<long, string>();
        foreach (var file in Directory.EnumerateFiles(_tools.IconCacheFolder, "*.png"))
        {
            if (long.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
                onDisk[id] = file;
        }
        if (onDisk.Count == 0) return;

        foreach (var entry in entries)
        {
            if (entry.CachedIconPath is null && onDisk.TryGetValue(entry.AppStoreId, out var path))
                entry.CachedIconPath = path;
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

    /// <summary>On-disk shape of a hand-added app.</summary>
    private sealed class UserApp
    {
        public string? Name { get; set; }
        public long AppStoreId { get; set; }
        public string? BundleId { get; set; }
        public string? IconUrl { get; set; }
        public string? IconUrlLarge { get; set; }
        public string? Category { get; set; }
        public string? LatestVersion { get; set; }
        public string? Developer { get; set; }
        public long? FileSizeBytes { get; set; }
        public string? MinimumOsVersion { get; set; }

        public static UserApp From(AppEntry e) => new()
        {
            Name = e.Name,
            AppStoreId = e.AppStoreId,
            BundleId = e.BundleId,
            IconUrl = e.IconUrl,
            IconUrlLarge = e.IconUrlLarge,
            Category = e.Category,
            LatestVersion = e.LatestVersion,
            Developer = e.Developer,
            FileSizeBytes = e.FileSizeBytes,
            MinimumOsVersion = e.MinimumOsVersion,
        };
    }

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
