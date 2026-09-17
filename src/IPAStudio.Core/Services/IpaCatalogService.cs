using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>A folder the user named and added as a library of local .ipa files.</summary>
public sealed class IpaCatalog
{
    /// <summary>Stable identity, so a rename cannot orphan the entry.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>What the user called it ("Денис", "Рабочие").</summary>
    public string Name { get; set; } = "";

    public string Folder { get; set; } = "";

    /// <summary>
    /// Archives found by the last scan, kept so the page opens instantly.
    ///
    /// Persisted rather than rebuilt on startup because scanning reads inside every archive in
    /// the folder; on a library of a few hundred that is seconds of work, and the user asked for
    /// it to happen only when they press Refresh.
    /// </summary>
    public List<IpaCatalogItem> Items { get; set; } = new();

    /// <summary>When the folder was last read, for the "scanned ..." line on the page.</summary>
    public DateTimeOffset? ScannedAt { get; set; }
}

/// <summary>One .ipa inside a catalog, as the last scan saw it.</summary>
public sealed class IpaCatalogItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string BundleId { get; init; } = "";
    public string Version { get; init; } = "";
    public long SizeBytes { get; init; }
    public string? IconPath { get; init; }

    /// <summary>
    /// Archive timestamp at scan time. Together with the length this is what tells a rebuilt
    /// file from an untouched one, so a replaced IPA is not served from a stale entry.
    /// </summary>
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>
    /// App Store id from the archive's own store metadata, null for an archive that did not
    /// come from the store.
    /// </summary>
    public long? StoreId { get; init; }

    /// <summary>
    /// Which generation of the scanner produced this entry. Entries written before a field was
    /// introduced carry 0, which is how an unchanged file is re-read exactly once instead of
    /// keeping a blank the scanner could now fill — and, just as importantly, how an archive
    /// that legitimately has no store id avoids being reopened on every single scan.
    /// </summary>
    public int ScanVersion { get; init; }
}

/// <summary>
/// Named folders of local .ipa files, for installing without going through the App Store.
///
/// The list is user data, not a cache: the names exist nowhere but here, so it is written to
/// its own file and survives clearing the caches.
/// </summary>
public sealed class IpaCatalogService
{
    private readonly ToolLocator _tools;

    /// <summary>
    /// Bump whenever the scanner learns to read a new field, so archives listed by an older
    /// build are re-read once instead of keeping a blank forever.
    /// </summary>
    private const int CurrentScanVersion = 1;

    /// <summary>
    /// Serialises writes. Two catalogs can be scanned at once, and both finish by saving the
    /// whole file — without this, one would truncate the other's entry.
    /// </summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private List<IpaCatalog>? _catalogs;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IpaCatalogService(ToolLocator tools) => _tools = tools;

    /// <summary>Catalogs in display order, loaded from disk on first use.</summary>
    public IReadOnlyList<IpaCatalog> Catalogs
    {
        get
        {
            _catalogs ??= Load();
            return _catalogs;
        }
    }

    public IpaCatalog Add(string name, string folder)
    {
        _catalogs ??= Load();

        var catalog = new IpaCatalog
        {
            Name = string.IsNullOrWhiteSpace(name) ? DefaultName(folder) : name.Trim(),
            Folder = folder,
        };

        _catalogs.Add(catalog);
        Save();
        return catalog;
    }

    /// <summary>
    /// The scanned .ipa carrying this bundle id, newest version first, or null when no library
    /// holds it.
    ///
    /// Exists for the direct-download page, which could previously name an app only from the
    /// store or from a phone that happened to be plugged in. Neither answers for a delisted app
    /// with no device attached - yet the archives already scanned on this machine carry the
    /// name, version and icon, and were simply never consulted, so the page showed the bare
    /// number the user had typed.
    /// </summary>
    public IpaCatalogItem? FindLocal(string? bundleId, long storeId = 0)
    {
        var hasBundle = !string.IsNullOrWhiteSpace(bundleId);
        if (!hasBundle && storeId <= 0) return null;

        _catalogs ??= Load();

        return _catalogs
            .SelectMany(c => c.Items)
            // Either identifier is enough. The store id matters most: an app entered as a bare
            // number has no bundle id yet, and that is precisely the case with nothing else to
            // match on.
            .Where(i => (hasBundle && string.Equals(i.BundleId, bundleId, StringComparison.OrdinalIgnoreCase))
                     || (storeId > 0 && i.StoreId == storeId))
            // Several libraries may hold the same app at different versions; the newest archive
            // is the one whose name and artwork are least likely to be out of date.
            .OrderByDescending(i => i.ModifiedAt)
            .FirstOrDefault();
    }

    public void Remove(string id)
    {
        _catalogs ??= Load();

        var found = _catalogs.FirstOrDefault(c => c.Id == id);
        if (found is null) return;

        _catalogs.Remove(found);
        Save();
    }

    public void Rename(string id, string name)
    {
        _catalogs ??= Load();

        var found = _catalogs.FirstOrDefault(c => c.Id == id);
        if (found is null || string.IsNullOrWhiteSpace(name)) return;

        found.Name = name.Trim();
        Save();
    }

    /// <summary>
    /// Rereads a catalog's folder and replaces its contents.
    ///
    /// Entries whose length and timestamp are unchanged keep their previous metadata instead of
    /// being read again — that is what makes a Refresh over a large library quick, since the
    /// expensive part is opening each archive.
    /// </summary>
    public async Task<IpaCatalog?> ScanAsync(string id, CancellationToken ct = default)
    {
        _catalogs ??= Load();

        var catalog = _catalogs.FirstOrDefault(c => c.Id == id);
        if (catalog is null) return null;

        if (!Directory.Exists(catalog.Folder))
        {
            AppLog.Warn($"IPA catalog '{catalog.Name}' points at a missing folder: {catalog.Folder}");
            catalog.Items.Clear();
            catalog.ScannedAt = DateTimeOffset.Now;
            Save();
            return catalog;
        }

        var known = catalog.Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

        // Off the UI thread: this opens and partially decompresses every archive it has not
        // seen before.
        var scanned = await Task.Run(() => ScanFolder(catalog.Folder, known, ct), ct)
                                .ConfigureAwait(false);

        catalog.Items = scanned;
        catalog.ScannedAt = DateTimeOffset.Now;
        await SaveAsync().ConfigureAwait(false);

        AppLog.Info($"IPA catalog '{catalog.Name}': {scanned.Count} archive(s) in {catalog.Folder}");
        return catalog;
    }

    private List<IpaCatalogItem> ScanFolder(
        string folder, Dictionary<string, IpaCatalogItem> known, CancellationToken ct)
    {
        var found = new List<IpaCatalogItem>();

        IEnumerable<string> files;
        try
        {
            // Top level only. Recursing would sweep up whatever the user keeps in subfolders,
            // and "the folder I chose" is the more predictable rule.
            files = Directory.EnumerateFiles(folder, "*.ipa", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not list '{folder}': {ex.Message}");
            return found;
        }

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }

            var stamp = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            // Unchanged file: reuse what the last scan learned rather than reopening it. The
            // scanner generation is part of the test, so entries written before the store id was
            // collected are re-read once and then left alone again.
            if (known.TryGetValue(path, out var previous) &&
                previous.SizeBytes == info.Length &&
                previous.ModifiedAt == stamp &&
                previous.ScanVersion >= CurrentScanVersion &&
                !string.IsNullOrEmpty(previous.BundleId))
            {
                found.Add(previous);
                continue;
            }

            var meta = IpaMetadata.Read(path, _tools.LocalIpaIconCacheFolder);

            found.Add(new IpaCatalogItem
            {
                Path = path,
                Name = meta.Name,
                // An unreadable archive still gets listed, so a file the user can see in the
                // folder does not silently vanish from the page — just with these blank.
                BundleId = meta.BundleId ?? "",
                Version = meta.Version ?? "",
                SizeBytes = info.Length,
                IconPath = meta.IconPath,
                ModifiedAt = stamp,
                StoreId = meta.StoreId,
                ScanVersion = CurrentScanVersion,
            });
        }

        // Alphabetical, as asked. Ordinal-ignore-case would put every Cyrillic name after every
        // Latin one; the culture-aware comparison interleaves them the way a person expects.
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return found;
    }

    /// <summary>Folder's own name, used when the user leaves the name box empty.</summary>
    private static string DefaultName(string folder)
    {
        try
        {
            var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf;
        }
        catch
        {
            return folder;
        }
    }

    // ───────────────────────────────── persistence ─────────────────────────────────

    private List<IpaCatalog> Load()
    {
        var path = _tools.IpaCatalogsFile;

        try
        {
            if (!File.Exists(path)) return new List<IpaCatalog>();

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<IpaCatalog>>(json, Json);
            if (loaded is null) return new List<IpaCatalog>();

            // A folder the user has since deleted stays in the list: dropping it silently would
            // lose the name they gave it, and the page can say the folder is missing instead.
            foreach (var catalog in loaded)
                catalog.Items ??= new List<IpaCatalogItem>();

            return loaded;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not read IPA catalogs: {ex.Message}");
            return new List<IpaCatalog>();
        }
    }

    private void Save()
    {
        try
        {
            _saveLock.Wait();
            WriteFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not save IPA catalogs: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            WriteFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not save IPA catalogs: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void WriteFile()
    {
        if (_catalogs is null) return;

        var path = _tools.IpaCatalogsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written beside the target and moved into place, so a crash mid-write cannot leave a
        // half-finished file where the catalog names used to be.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_catalogs, Json));
        File.Move(temp, path, overwrite: true);
    }
}
