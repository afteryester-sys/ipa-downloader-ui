using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Resolves real local application icons from the catalog cache, local IPA libraries,
/// and device icon caches. If an icon is not present on disk, returns null so the UI
/// can display a uniform default application icon.
/// </summary>
public static class AppIconResolver
{
    private static readonly object Lock = new();
    private static Dictionary<string, string>? _bundleToStoreId;
    private static DateTime _lastCacheRead = DateTime.MinValue;

    /// <summary>
    /// Attempts to find a real icon image file on disk for the given bundle ID or app name.
    /// </summary>
    public static string? ResolveIconPath(string? bundleId, string? appName, ToolLocator? tools = null)
    {
        tools ??= new ToolLocator();

        // 1. Try resolving via bundleId in catalog cache
        if (!string.IsNullOrWhiteSpace(bundleId))
        {
            var iconFromCatalog = TryGetIconFromCatalogCache(bundleId.Trim(), tools);
            if (!string.IsNullOrEmpty(iconFromCatalog) && File.Exists(iconFromCatalog))
            {
                return iconFromCatalog;
            }

            // 2. Try resolving from device icon cache
            var iconFromDevice = TryGetIconFromDeviceCache(bundleId.Trim(), tools);
            if (!string.IsNullOrEmpty(iconFromDevice) && File.Exists(iconFromDevice))
            {
                return iconFromDevice;
            }

            // 3. Try resolving from local IPA libraries (ipa-catalogs.json)
            var iconFromIpa = TryGetIconFromIpaCatalogs(bundleId.Trim(), tools);
            if (!string.IsNullOrEmpty(iconFromIpa) && File.Exists(iconFromIpa))
            {
                return iconFromIpa;
            }
        }

        // 4. Try resolving via app name if bundle ID didn't hit
        if (!string.IsNullOrWhiteSpace(appName))
        {
            var iconByName = TryGetIconFromIpaCatalogsByName(appName.Trim(), tools);
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

    private static void EnsureCatalogIndexLoaded(ToolLocator tools)
    {
        try
        {
            var cacheFile = tools.CatalogCacheFile;
            if (!File.Exists(cacheFile)) return;

            var fileInfo = new FileInfo(cacheFile);
            if (_bundleToStoreId != null && fileInfo.LastWriteTimeUtc <= _lastCacheRead)
            {
                return;
            }

            using var stream = File.OpenRead(cacheFile);
            using var doc = JsonDocument.Parse(stream);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var storeId = prop.Name;
                if (prop.Value.TryGetProperty("BundleId", out var bundleElem))
                {
                    var bid = bundleElem.GetString();
                    if (!string.IsNullOrEmpty(bid))
                    {
                        map[bid] = storeId;
                    }
                }
            }

            _bundleToStoreId = map;
            _lastCacheRead = fileInfo.LastWriteTimeUtc;
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

            // Direct check in subdirectories
            foreach (var udidDir in Directory.EnumerateDirectories(tools.DeviceIconCacheFolder))
            {
                var candidate = Path.Combine(udidDir, $"{bundleId}.png");
                if (File.Exists(candidate))
                {
                    return candidate;
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
}
