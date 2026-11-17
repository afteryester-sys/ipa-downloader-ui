using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Resolves real local application icons from the catalog cache, user catalog, local IPA libraries,
/// and device icon caches. If an icon is not present on disk, returns null so the UI
/// can display a uniform default application icon.
/// </summary>
public static class AppIconResolver
{
    private static readonly object Lock = new();
    private static Dictionary<string, string>? _bundleToStoreId;
    private static Dictionary<string, string>? _nameToStoreId;
    private static DateTime _lastCacheRead = DateTime.MinValue;

    /// <summary>
    /// Attempts to find a real icon image file on disk for the given bundle ID or app name.
    /// </summary>
    public static string? ResolveIconPath(string? bundleId, string? appName, ToolLocator? tools = null)
    {
        tools ??= new ToolLocator();

        // 1. Try resolving via bundleId in catalog cache / user catalog
        if (!string.IsNullOrWhiteSpace(bundleId))
        {
            var trimmedBundle = bundleId.Trim();

            // Direct check if bundleId is numeric store ID (e.g. "6756371742")
            if (long.TryParse(trimmedBundle, out _))
            {
                var directIcon = Path.Combine(tools.IconCacheFolder, $"{trimmedBundle}.png");
                if (File.Exists(directIcon)) return directIcon;
            }

            var iconFromCatalog = TryGetIconFromCatalogCache(trimmedBundle, tools);
            if (!string.IsNullOrEmpty(iconFromCatalog) && File.Exists(iconFromCatalog))
            {
                return iconFromCatalog;
            }

            // 2. Try resolving from device icon cache
            var iconFromDevice = TryGetIconFromDeviceCache(trimmedBundle, tools);
            if (!string.IsNullOrEmpty(iconFromDevice) && File.Exists(iconFromDevice))
            {
                return iconFromDevice;
            }

            // 3. Try resolving from local IPA libraries (ipa-catalogs.json)
            var iconFromIpa = TryGetIconFromIpaCatalogs(trimmedBundle, tools);
            if (!string.IsNullOrEmpty(iconFromIpa) && File.Exists(iconFromIpa))
            {
                return iconFromIpa;
            }
        }

        // 4. Try resolving via app name if bundle ID didn't hit
        if (!string.IsNullOrWhiteSpace(appName))
        {
            var trimmedName = appName.Trim();

            var iconFromCatalogByName = TryGetIconFromCatalogCacheByName(trimmedName, tools);
            if (!string.IsNullOrEmpty(iconFromCatalogByName) && File.Exists(iconFromCatalogByName))
            {
                return iconFromCatalogByName;
            }

            var iconByName = TryGetIconFromIpaCatalogsByName(trimmedName, tools);
            if (!string.IsNullOrEmpty(iconByName) && File.Exists(iconByName))
            {
                return iconByName;
            }
        }

        return null;
    }

    private static string? TryGetIconFromCatalogCache(string bundleId, ToolLocator tools)
    {
        lock (Lock)
        {
            EnsureCatalogIndexLoaded(tools);
            if (_bundleToStoreId != null && _bundleToStoreId.TryGetValue(bundleId, out var storeId))
            {
                var iconFile = Path.Combine(tools.IconCacheFolder, $"{storeId}.png");
                if (File.Exists(iconFile))
                {
                    return iconFile;
                }
            }
        }
        return null;
    }

    private static string? TryGetIconFromCatalogCacheByName(string appName, ToolLocator tools)
    {
        lock (Lock)
        {
            EnsureCatalogIndexLoaded(tools);
            if (_nameToStoreId != null && _nameToStoreId.TryGetValue(appName, out var storeId))
            {
                var iconFile = Path.Combine(tools.IconCacheFolder, $"{storeId}.png");
                if (File.Exists(iconFile))
                {
                    return iconFile;
                }
            }
        }
        return null;
    }

    private static void EnsureCatalogIndexLoaded(ToolLocator tools)
    {
        try
        {
            var cacheFile = tools.CatalogCacheFile;
            var userCatFile = tools.UserCatalogFile;

            var cacheStamp = File.Exists(cacheFile) ? new FileInfo(cacheFile).LastWriteTimeUtc : DateTime.MinValue;
            var userCatStamp = File.Exists(userCatFile) ? new FileInfo(userCatFile).LastWriteTimeUtc : DateTime.MinValue;
            var maxStamp = cacheStamp > userCatStamp ? cacheStamp : userCatStamp;

            if (_bundleToStoreId != null && _nameToStoreId != null && maxStamp <= _lastCacheRead)
            {
                return;
            }

            var bundleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Index 1: catalog-cache.json (storeId -> { BundleId, ... })
            if (File.Exists(cacheFile))
            {
                using var stream = File.OpenRead(cacheFile);
                using var doc = JsonDocument.Parse(stream);

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var storeId = prop.Name;
                    if (prop.Value.TryGetProperty("BundleId", out var bundleElem) ||
                        prop.Value.TryGetProperty("bundleId", out bundleElem))
                    {
                        var bid = bundleElem.GetString();
                        if (!string.IsNullOrEmpty(bid))
                        {
                            bundleMap[bid] = storeId;
                        }
                    }

                    if (prop.Value.TryGetProperty("Name", out var nameElem) ||
                        prop.Value.TryGetProperty("name", out nameElem))
                    {
                        var name = nameElem.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            nameMap[name] = storeId;
                        }
                    }
                }
            }

            // Index 2: user-apps.json ([ { AppStoreId, BundleId, Name, ... } ])
            if (File.Exists(userCatFile))
            {
                using var stream = File.OpenRead(userCatFile);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        string? storeIdStr = null;
                        if (item.TryGetProperty("AppStoreId", out var idElem))
                        {
                            storeIdStr = idElem.GetInt64().ToString();
                        }

                        if (!string.IsNullOrEmpty(storeIdStr))
                        {
                            if (item.TryGetProperty("BundleId", out var bElem))
                            {
                                var bid = bElem.GetString();
                                if (!string.IsNullOrEmpty(bid)) bundleMap[bid] = storeIdStr;
                            }

                            if (item.TryGetProperty("Name", out var nElem))
                            {
                                var name = nElem.GetString();
                                if (!string.IsNullOrEmpty(name)) nameMap[name] = storeIdStr;
                            }
                        }
                    }
                }
            }

            _bundleToStoreId = bundleMap;
            _nameToStoreId = nameMap;
            _lastCacheRead = maxStamp;
        }
        catch
        {
            // Non-fatal
        }
    }

    private static string? TryGetIconFromDeviceCache(string bundleId, ToolLocator tools)
    {
        try
        {
            if (!Directory.Exists(tools.DeviceIconCacheFolder)) return null;

            var sanitized = SanitizeCacheKey(bundleId);

            // Direct check in subdirectories
            foreach (var udidDir in Directory.EnumerateDirectories(tools.DeviceIconCacheFolder))
            {
                var candidate = Path.Combine(udidDir, $"{bundleId}.png");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                if (!string.Equals(sanitized, bundleId, StringComparison.OrdinalIgnoreCase))
                {
                    var candSanitized = Path.Combine(udidDir, $"{sanitized}.png");
                    if (File.Exists(candSanitized))
                    {
                        return candSanitized;
                    }
                }
            }
        }
        catch
        {
            // Non-fatal
        }
        return null;
    }

    private static string? TryGetIconFromIpaCatalogs(string bundleId, ToolLocator tools)
    {
        try
        {
            var catFile = tools.IpaCatalogsFile;
            if (!File.Exists(catFile)) return null;

            using var stream = File.OpenRead(catFile);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var catElem in doc.RootElement.EnumerateArray())
            {
                if (catElem.TryGetProperty("Items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemsElem.EnumerateArray())
                    {
                        if (item.TryGetProperty("BundleId", out var bElem) &&
                            string.Equals(bElem.GetString(), bundleId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.TryGetProperty("IconPath", out var iconElem))
                            {
                                var path = iconElem.GetString();
                                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                                {
                                    return path;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Non-fatal
        }
        return null;
    }

    private static string? TryGetIconFromIpaCatalogsByName(string appName, ToolLocator tools)
    {
        try
        {
            var catFile = tools.IpaCatalogsFile;
            if (!File.Exists(catFile)) return null;

            using var stream = File.OpenRead(catFile);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var catElem in doc.RootElement.EnumerateArray())
            {
                if (catElem.TryGetProperty("Items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemsElem.EnumerateArray())
                    {
                        if (item.TryGetProperty("Name", out var nElem) &&
                            string.Equals(nElem.GetString(), appName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.TryGetProperty("IconPath", out var iconElem))
                            {
                                var path = iconElem.GetString();
                                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                                {
                                    return path;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Non-fatal
        }
        return null;
    }

    private static string SanitizeCacheKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "unknown";
        var builder = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }
        return builder.ToString();
    }
}
