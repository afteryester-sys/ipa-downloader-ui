using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using IPAStudio.Core.Diagnostics;

namespace IPAStudio.Core.Tools;

/// <summary>
/// What an .ipa file says about itself: display name, bundle id, version and artwork.
/// </summary>
/// <param name="Path">Full path of the archive this was read from.</param>
/// <param name="Name">
/// Display name, falling back to the file name — a row in a list must always have a label,
/// and plenty of archives (re-signed, decrypted, hand-built) carry no readable metadata.
/// </param>
/// <param name="BundleId">
/// Bundle identifier, or null when the archive does not disclose one. Null matters: it is the
/// difference between "this app is not installed" and "we cannot tell", and the two must not
/// be confused when an install is being verified.
/// </param>
/// <param name="Version">Short version string ("7.1.2"), when stated.</param>
/// <param name="IconPath">
/// Extracted artwork on disk, or null when the archive has none we can display.
/// </param>
/// <param name="SizeBytes">Size of the archive itself.</param>
/// <param name="StoreId">
/// App Store (Adam) id from the archive's store metadata, or null for an archive that never
/// came from the store. This is what lets an app typed in as a bare number be recognised from a
/// local library, since a number can be matched against nothing else.
/// </param>
public sealed record IpaInfo(
    string Path,
    string Name,
    string? BundleId,
    string? Version,
    string? IconPath,
    long SizeBytes,
    long? StoreId = null);

/// <summary>
/// Reads the metadata of an .ipa without unpacking it.
///
/// Only the zip central directory and a couple of small members are touched, so a folder of
/// several hundred archives can be listed in about the time it takes to stat them — which is
/// what makes scanning a folder on demand practical at all.
///
/// Every failure yields a usable result rather than an exception: an archive that cannot be
/// read still appears in the list under its file name. A folder of IPAs is user data of
/// unknown provenance, and one damaged file must not take the whole listing down with it.
/// </summary>
public static class IpaMetadata
{
    /// <summary>
    /// Reads <paramref name="ipaPath"/>. When <paramref name="iconCacheFolder"/> is given,
    /// the artwork is extracted there and reused on later scans.
    /// </summary>
    public static IpaInfo Read(string ipaPath, string? iconCacheFolder = null)
    {
        var fallbackName = System.IO.Path.GetFileNameWithoutExtension(ipaPath);
        long size = 0;
        DateTime stamp = default;
        try
        {
            var info = new FileInfo(ipaPath);
            size = info.Length;
            stamp = info.LastWriteTimeUtc;
        }
        catch { /* size and stamp are only used for the cache key and the subtitle */ }

        // An icon already extracted for this exact file is reused without opening the zip:
        // the name carries the archive's own timestamp and length, so a replaced file misses
        // the cache instead of showing the previous app's artwork.
        var cachedIcon = iconCacheFolder is null ? null : CachedIconPath(iconCacheFolder, ipaPath, size, stamp);
        var iconAlreadyThere = cachedIcon is not null && File.Exists(cachedIcon);

        try
        {
            using var zip = ZipFile.OpenRead(ipaPath);

            // The App Store's own metadata comes first: it is plain XML at the archive root
            // and states the name as the store shows it, which is the name the user recognises.
            // Info.plist is authoritative for the bundle id but often gives a terse internal
            // name ("Mail" for what the store calls something longer).
            string? name = null, bundleId = null, version = null;
            long? storeId = null;

            var itunes = zip.GetEntry("iTunesMetadata.plist");
            if (itunes is not null && ReadPlist(itunes) is Dictionary<string, object?> meta)
            {
                name = FirstString(meta, "itemName", "playlistName");
                bundleId = FirstString(meta, "softwareVersionBundleId", "bundleId");
                version = FirstString(meta, "bundleShortVersionString", "shortVersionString");
                // Same key spellings the install path already has to accept.
                storeId = FirstLong(meta, "itemId", "item-id", "storeItemIdentifier");
            }

            // Info.plist of the app bundle itself. Read even when the store metadata answered,
            // because the bundle id decides whether an install can be verified and this is the
            // one place that always has it.
            var bundleRoot = FindBundleRoot(zip);
            Dictionary<string, object?>? info = null;
            if (bundleRoot is not null &&
                zip.GetEntry(bundleRoot + "Info.plist") is { } plist &&
                ReadPlist(plist) is Dictionary<string, object?> parsed)
            {
                info = parsed;
                bundleId ??= FirstString(info, "CFBundleIdentifier");
                name ??= FirstString(info, "CFBundleDisplayName", "CFBundleName");
                version ??= FirstString(info, "CFBundleShortVersionString", "CFBundleVersion");
            }

            var icon = iconAlreadyThere
                ? cachedIcon
                : cachedIcon is null ? null : ExtractIcon(zip, bundleRoot, info, cachedIcon);

            return new IpaInfo(
                ipaPath,
                string.IsNullOrWhiteSpace(name) ? fallbackName : name!.Trim(),
                string.IsNullOrWhiteSpace(bundleId) ? null : bundleId!.Trim(),
                string.IsNullOrWhiteSpace(version) ? null : version!.Trim(),
                icon,
                size,
                storeId);
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not read IPA metadata from '{ipaPath}': {ex.Message}");
            return new IpaInfo(ipaPath, fallbackName, null, null,
                              iconAlreadyThere ? cachedIcon : null, size);
        }
    }

    // ───────────────────────────────── artwork ─────────────────────────────────

    /// <summary>
    /// Writes the archive's artwork to <paramref name="destination"/> and returns it, or null
    /// when the archive has nothing displayable.
    /// </summary>
    private static string? ExtractIcon(
        ZipArchive zip, string? bundleRoot, Dictionary<string, object?>? info, string destination)
    {
        // "iTunesArtwork" is the store's copy: an ordinary PNG or JPEG, full size, and present
        // in every archive the App Store hands out. Preferred over the bundle's own icons
        // because those are usually run through Apple's PNG variant (see below).
        var candidates = new List<ZipArchiveEntry>();
        foreach (var candidate in new[] { "iTunesArtwork", "iTunesArtwork@2x" })
            if (zip.GetEntry(candidate) is { } art) candidates.Add(art);

        if (bundleRoot is not null)
            candidates.AddRange(BundleIconEntries(zip, bundleRoot, info));

        foreach (var entry in candidates)
        {
            var bytes = ReadEntry(entry, maxBytes: 8 * 1024 * 1024);
            if (bytes is null || bytes.Length < 64) continue;

            // Icons inside an .app are normally CgBI — Apple's own PNG variant, which no Windows
            // decoder reads. Converting is not optional polish: it is the format nearly every
            // App Store build ships, so without this branch almost no local IPA would show an
            // icon at all. A file that will not convert is skipped, and the caller falls back
            // to its letter tile.
            if (IsCgBiPng(bytes))
            {
                bytes = CgBiPng.ToStandardPng(bytes) ?? Array.Empty<byte>();
                if (bytes.Length == 0) continue;
            }

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
                return destination;
            }
            catch (Exception ex)
            {
                AppLog.Info($"Could not cache IPA icon: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Icon files of the app bundle, largest first. Names come from Info.plist when it lists
    /// them, since the bundle also holds settings and notification artwork that would otherwise
    /// win on being merely present.
    /// </summary>
    private static IEnumerable<ZipArchiveEntry> BundleIconEntries(
        ZipArchive zip, string bundleRoot, Dictionary<string, object?>? info)
    {
        var declared = DeclaredIconNames(info);

        return zip.Entries
            .Where(e =>
            {
                if (!e.FullName.StartsWith(bundleRoot, StringComparison.OrdinalIgnoreCase)) return false;

                // Bundle root only: nested bundles carry their own icons, which are not this app's.
                var relative = e.FullName[bundleRoot.Length..];
                if (relative.Contains('/')) return false;
                if (!relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;

                return declared.Count == 0
                    ? relative.Contains("icon", StringComparison.OrdinalIgnoreCase)
                    : declared.Any(d => relative.StartsWith(d, StringComparison.OrdinalIgnoreCase));
            })
            // Bigger file, bigger icon: the bundle names sizes inconsistently
            // ("AppIcon60x60@2x", "Icon-83.5@2x"), but length orders them reliably enough.
            .OrderByDescending(e => e.Length);
    }

    /// <summary>Icon base names declared in Info.plist, in either of the two layouts Apple uses.</summary>
    private static List<string> DeclaredIconNames(Dictionary<string, object?>? info)
    {
        var names = new List<string>();
        if (info is null) return names;

        // Modern layout: CFBundleIcons › CFBundlePrimaryIcon › CFBundleIconFiles.
        foreach (var key in new[] { "CFBundleIcons", "CFBundleIcons~ipad" })
        {
            if (info.TryGetValue(key, out var icons) &&
                icons is Dictionary<string, object?> iconDict &&
                iconDict.TryGetValue("CFBundlePrimaryIcon", out var primary) &&
                primary is Dictionary<string, object?> primaryDict)
            {
                if (primaryDict.TryGetValue("CFBundleIconFiles", out var files) && files is List<object?> list)
                    names.AddRange(list.OfType<string>());

                if (primaryDict.TryGetValue("CFBundleIconName", out var single) && single is string one)
                    names.Add(one);
            }
        }

        // Pre-iOS 5 layout, still emitted by plenty of build tooling.
        if (info.TryGetValue("CFBundleIconFiles", out var flat) && flat is List<object?> flatList)
            names.AddRange(flatList.OfType<string>());

        if (info.TryGetValue("CFBundleIconFile", out var older) && older is string olderName)
            names.Add(olderName);

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => System.IO.Path.GetFileNameWithoutExtension(n).Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether this PNG is Apple's CgBI variant, which no Windows decoder reads.</summary>
    private static bool IsCgBiPng(byte[] bytes)
    {
        // The chunk, when present, is the first one after the 8-byte signature: length (4),
        // then the type at offset 12.
        if (bytes.Length < 16) return false;
        if (bytes[0] != 0x89 || bytes[1] != 'P' || bytes[2] != 'N' || bytes[3] != 'G') return false;
        return bytes[12] == 'C' && bytes[13] == 'g' && bytes[14] == 'B' && bytes[15] == 'I';
    }

    /// <summary>
    /// Stable per-archive icon file name. Includes length and timestamp so replacing an IPA
    /// with a different app under the same file name cannot inherit the old artwork.
    /// </summary>
    private static string CachedIconPath(string folder, string ipaPath, long size, DateTime stamp)
    {
        var key = $"{ipaPath.ToLowerInvariant()}|{size}|{stamp.Ticks}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return System.IO.Path.Combine(folder, $"ipa-{hash}.img");
    }

    // ───────────────────────────────── plists ─────────────────────────────────

    /// <summary>
    /// Parses a plist member in either encoding Apple ships: the binary form used inside app
    /// bundles, and the XML form used for the store metadata at the archive root.
    /// </summary>
    private static object? ReadPlist(ZipArchiveEntry entry)
    {
        var bytes = ReadEntry(entry, maxBytes: 4 * 1024 * 1024);
        if (bytes is null || bytes.Length == 0) return null;

        if (BinaryPlist.LooksBinary(bytes)) return BinaryPlist.Parse(bytes);

        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            var root = XDocument.Parse(text).Root?.Elements().FirstOrDefault();
            return root is null ? null : FromXml(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>XML plist element to the same shapes <see cref="BinaryPlist"/> produces.</summary>
    private static object? FromXml(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "dict":
                // Case-insensitive to match the binary reader, so callers need only one spelling.
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                string? key = null;
                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName == "key") { key = child.Value; continue; }
                    if (key is null) continue;
                    dict[key] = FromXml(child);
                    key = null;
                }
                return dict;

            case "array":
                return element.Elements().Select(FromXml).ToList();

            case "true":  return true;
            case "false": return false;

            case "integer":
                return long.TryParse(element.Value, NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out var number)
                    ? number
                    : null;

            case "real":
                return double.TryParse(element.Value, NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out var real)
                    ? real
                    : null;

            case "data":
                try { return Convert.FromBase64String(element.Value); } catch { return null; }

            default:
                return element.Value;
        }
    }

    /// <summary>First of <paramref name="keys"/> present as a non-empty string.</summary>
    /// <summary>
    /// First key holding a whole number. Written separately from <see cref="FirstString"/>
    /// because the store id arrives as a plist integer in some archives and as a string in
    /// others, and either spelling has to answer.
    /// </summary>
    private static long? FirstLong(Dictionary<string, object?> dict, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!dict.TryGetValue(key, out var value) || value is null) continue;

            switch (value)
            {
                case long l when l > 0: return l;
                case int i when i > 0: return i;
                // Converted rather than cast: a plist can hold this as a real, and the value is
                // an identifier, so anything fractional is not one.
                case double d when d > 0 && d == Math.Floor(d): return (long)d;
                case string s when long.TryParse(
                    s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0:
                    return parsed;
            }
        }

        return null;
    }

    private static string? FirstString(Dictionary<string, object?> dict, params string[] keys)
    {
        foreach (var key in keys)
            if (dict.TryGetValue(key, out var value) &&
                value is string text &&
                !string.IsNullOrWhiteSpace(text))
                return text;

        return null;
    }

    /// <summary>
    /// Shortest "Payload/Name.app/" in the archive, which is the app itself — anything longer
    /// is a framework, extension or watch app nested inside it.
    /// </summary>
    private static string? FindBundleRoot(ZipArchive zip)
    {
        string? best = null;
        const string payload = "Payload/";

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(payload, StringComparison.OrdinalIgnoreCase)) continue;

            var appIdx = entry.FullName.IndexOf(".app/", payload.Length, StringComparison.OrdinalIgnoreCase);
            if (appIdx < 0) continue;

            var root = entry.FullName[..(appIdx + ".app/".Length)];
            if (best is null || root.Length < best.Length) best = root;
        }

        return best;
    }

    /// <summary>
    /// Contents of a zip member, or null when it is absent, unreadable, or larger than
    /// <paramref name="maxBytes"/>. The cap is deliberate: the declared length of a zip entry
    /// is attacker-controlled, and this runs over files the user merely happened to have.
    /// </summary>
    private static byte[]? ReadEntry(ZipArchiveEntry entry, long maxBytes)
    {
        try
        {
            if (entry.Length > maxBytes) return null;

            using var stream = entry.Open();
            using var buffer = new MemoryStream();

            // Bounded by the declared length rather than trusting it: a lying header stops the
            // copy instead of filling memory.
            var window = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(window, 0, window.Length)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(window, 0, read);
            }

            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
