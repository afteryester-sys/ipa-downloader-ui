using System.Globalization;
using System.Text;
using System.Xml.Linq;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Native;
using IPAStudio.Core.Tools;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.iDevice;
using iMobileDevice.NotificationProxy;

namespace IPAStudio.Core.Services;

/// <summary>
/// Implements native Apple Tethered Photo Synchronization via AFC staging and
/// the AirTraffic protocol (interfacing with AirTrafficHost.dll and iOS atc daemon).
/// Creates native synced photo albums on connected iOS devices.
/// </summary>
public sealed class AirTrafficPhotoSyncService
{
    private const uint ChunkSize = 256 * 1024; // 256 KiB per AFC chunk

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".bmp", ".tif", ".tiff",
        ".mov", ".mp4", ".m4v", ".avi"
    };

    public bool IsAvailable => AirTrafficHostApi.IsAvailable;

    public static bool IsSupportedMedia(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// Synchronizes a batch of local photos and videos into a native album on the iOS device.
    /// </summary>
    public Task<AirTrafficSyncResult> SyncPhotosAsync(
        string udid,
        IReadOnlyList<string> localFiles,
        string albumName = "IPA Studio",
        IProgress<PhotoTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(udid);
                ArgumentNullException.ThrowIfNull(localFiles);

                var validFiles = localFiles
                    .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f) && IsSupportedMedia(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (validFiles.Count == 0)
                {
                    return new AirTrafficSyncResult
                    {
                        Copied = 0,
                        Total = localFiles.Count,
                        Success = false,
                        AlbumName = albumName,
                        ErrorMessage = "No valid media files found to synchronize."
                    };
                }

                var stagedNames = new List<string>(validFiles.Count);
                var stagedAssets = new List<StagedAssetEntry>(validFiles.Count);

                using var session = OpenAfcSession(udid);
                var afc = session.Afc;
                var client = session.Client;

                // 1. Ensure required directory hierarchy on device
                EnsureDirectory(afc, client, "/Airlock");
                EnsureDirectory(afc, client, "/Airlock/Photo");
                EnsureDirectory(afc, client, "/DCIM");
                EnsureDirectory(afc, client, "/DCIM/100APPLE");
                EnsureDirectory(afc, client, "/Photos");
                EnsureDirectory(afc, client, "/Photos/Sync");
                EnsureDirectory(afc, client, "/Photos/Sync/100SYNCD");
                EnsureDirectory(afc, client, "/PhotoData");
                EnsureDirectory(afc, client, "/PhotoData/Sync");
                EnsureDirectory(afc, client, "/PhotoData/Sync/100SYNCD");

                // 2. Resolve Library UUID and DCIM camera roll target directory
                var libraryUuid = ReadExistingLibraryUuid(afc, client) ?? Guid.NewGuid().ToString().ToUpperInvariant();
                var dcimFolder = ResolveDcimImportFolder(afc, client);
                var takenDcimNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (afc.afc_read_directory(client, dcimFolder, out var existingDcim) == AfcError.Success && existingDcim is not null)
                {
                    foreach (var name in existingDcim) takenDcimNames.Add(name);
                }

                // 3. Stage each media file into /PhotoData/Sync/100SYNCD/<UUID>.<ext> and mirror across sync & DCIM directories
                var done = 0;
                foreach (var localPath in validFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(localPath);
                    var ext = Path.GetExtension(localPath).ToLowerInvariant();
                    var assetUuid = Guid.NewGuid().ToString().ToUpperInvariant();
                    var remoteFileName = $"{assetUuid}{ext}";
                    var primaryRemotePath = $"/PhotoData/Sync/100SYNCD/{remoteFileName}";

                    progress?.Report(new PhotoTransferProgress(done, validFiles.Count, fileName));

                    var fileInfo = new FileInfo(localPath);
                    var fileSize = fileInfo.Length;

                    ulong handle = 0;
                    var openErr = afc.afc_file_open(client, primaryRemotePath, AfcFileMode.FopenWronly, ref handle);
                    if (openErr != AfcError.Success)
                    {
                        // Fallback to Airlock if PhotoData/Sync isn't directly writable on older firmware
                        primaryRemotePath = $"/Airlock/Photo/{remoteFileName}";
                        openErr = afc.afc_file_open(client, primaryRemotePath, AfcFileMode.FopenWronly, ref handle);
                        if (openErr != AfcError.Success)
                        {
                            // Fallback directly to DCIM Camera Roll
                            var directDcimPath = NextDcimImportPath(dcimFolder, ext, takenDcimNames);
                            primaryRemotePath = directDcimPath;
                            openErr = afc.afc_file_open(client, primaryRemotePath, AfcFileMode.FopenWronly, ref handle);
                            if (openErr != AfcError.Success)
                            {
                                throw new IOException($"AFC failed to create '{primaryRemotePath}' ({openErr}). Unlock device and confirm trust.");
                            }
                        }
                    }

                    var writeCompleted = false;
                    try
                    {
                        using var stream = File.OpenRead(localPath);
                        var buffer = new byte[ChunkSize];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            uint written = 0;
                            var chunk = read == buffer.Length ? buffer : buffer[..read];
                            var writeErr = afc.afc_file_write(client, handle, chunk, (uint)read, ref written);
                            if (writeErr != AfcError.Success || written != (uint)read)
                            {
                                throw new IOException($"AFC write failure on '{fileName}' ({writeErr}).");
                            }
                        }
                        writeCompleted = true;
                    }
                    finally
                    {
                        afc.afc_file_close(client, handle);
                        if (!writeCompleted)
                        {
                            // Clean up partially written file
                            try { afc.afc_remove_path(client, primaryRemotePath); } catch { }
                        }
                    }

                    // Verify file size on device
                    var remoteSize = ReadFileSize(afc, client, primaryRemotePath);
                    if (remoteSize != fileSize)
                    {
                        AppLog.Warn($"AirTraffic: size mismatch for '{primaryRemotePath}': remote={remoteSize}, expected={fileSize}");
                        try { afc.afc_remove_path(client, primaryRemotePath); } catch { }
                        continue;
                    }

                    // Mirror/hardlink to all standard iOS photo sync locations so assetsd and atc can both access it
                    LinkOrCopyAfcFile(afc, client, primaryRemotePath, $"/Photos/Sync/100SYNCD/{remoteFileName}", localPath);
                    LinkOrCopyAfcFile(afc, client, primaryRemotePath, $"/PhotoData/Sync/{remoteFileName}", localPath);
                    LinkOrCopyAfcFile(afc, client, primaryRemotePath, $"/Photos/Sync/{remoteFileName}", localPath);
                    LinkOrCopyAfcFile(afc, client, primaryRemotePath, $"/Airlock/Photo/{remoteFileName}", localPath);

                    // Mirror to native DCIM Camera Roll so photos immediately appear in Recents / Gallery
                    if (!primaryRemotePath.StartsWith("/DCIM", StringComparison.OrdinalIgnoreCase))
                    {
                        var dcimPath = NextDcimImportPath(dcimFolder, ext, takenDcimNames);
                        LinkOrCopyAfcFile(afc, client, primaryRemotePath, dcimPath, localPath);
                    }

                    var (w, h) = ProbeDimensions(localPath);
                    var uti = GetUtiForExtension(ext);
                    var dateCreated = fileInfo.CreationTimeUtc;

                    stagedAssets.Add(new StagedAssetEntry(assetUuid, fileName, remoteFileName, uti, fileSize, dateCreated, w, h));
                    stagedNames.Add(fileName);
                    done++;
                }

                if (done == 0)
                {
                    return new AirTrafficSyncResult
                    {
                        Copied = 0,
                        Total = validFiles.Count,
                        Success = false,
                        AlbumName = albumName,
                        ErrorMessage = "No files were successfully written to the device."
                    };
                }

                progress?.Report(new PhotoTransferProgress(done, validFiles.Count, "Committing manifests..."));

                // 4. Generate and commit sync manifests (mirroring to /Photos/Sync and /PhotoData/Sync for cross-iOS compatibility)
                var existingLibrary = ReadExistingPhotoLibraryPlist(afc, client);
                var mergedLibraryPlist = GeneratePhotoLibraryPlist(libraryUuid, albumName, stagedAssets, existingLibrary);
                var currentLibPlist = GenerateCurrentLibraryUuidPlist(libraryUuid);
                var syncInfoPlist = GenerateSyncInfoPlist(libraryUuid);

                WriteAfcTextFile(afc, client, "/Photos/Sync/CurrentLibraryUUID.plist", currentLibPlist);
                WriteAfcTextFile(afc, client, "/Photos/Sync/PhotoLibrary.plist", mergedLibraryPlist);
                WriteAfcTextFile(afc, client, "/Photos/Sync/syncInfo.plist", syncInfoPlist);

                // Mirror to /PhotoData/Sync and /PhotoData as well
                try
                {
                    WriteAfcTextFile(afc, client, "/PhotoData/Sync/CurrentLibraryUUID.plist", currentLibPlist);
                    WriteAfcTextFile(afc, client, "/PhotoData/Sync/PhotoLibrary.plist", mergedLibraryPlist);
                    WriteAfcTextFile(afc, client, "/PhotoData/Sync/syncInfo.plist", syncInfoPlist);
                    WriteAfcTextFile(afc, client, "/PhotoData/syncInfo.plist", syncInfoPlist);
                }
                catch { /* best effort mirror */ }

                AppLog.Info($"AirTraffic: staged {done} items, manifests committed for library {libraryUuid}");

                // 5. Trigger sync via AirTrafficHost (isolated subprocess in GUI app)
                var athNotified = false;
                if (AirTrafficHostApi.IsAvailable)
                {
                    progress?.Report(new PhotoTransferProgress(done, validFiles.Count, "Triggering device sync..."));
                    try
                    {
                        athNotified = AirTrafficHostApi.TriggerSyncIsolated(udid, "Photos", libraryUuid);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"AirTraffic: TriggerSync failed: {ex.Message}");
                    }
                }
                else
                {
                    AppLog.Info("AirTraffic: native engine not found; relying on AFC manifests and notification proxy");
                }

                // 6. Post standard iTunes sync notifications via NotificationProxy
                PostSyncNotifications(udid);

                progress?.Report(new PhotoTransferProgress(done, validFiles.Count, string.Empty));

                return new AirTrafficSyncResult
                {
                    Copied = done,
                    Total = validFiles.Count,
                    AirTrafficNotified = athNotified,
                    Success = true,
                    AlbumName = albumName,
                    StagedFiles = stagedNames
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"AirTraffic: photo sync exception for {udid}: {ex.Message}");
                return new AirTrafficSyncResult
                {
                    Copied = 0,
                    Total = localFiles?.Count ?? 0,
                    Success = false,
                    AlbumName = albumName,
                    ErrorMessage = ex.Message
                };
            }
        }, ct);

    private static void EnsureDirectory(IAfcApi afc, AfcClientHandle client, string path)
    {
        afc.afc_make_directory(client, path);
    }

    private static string ResolveDcimImportFolder(IAfcApi afc, AfcClientHandle client)
    {
        var best = string.Empty;
        if (afc.afc_read_directory(client, "/DCIM", out var albums) == AfcError.Success && albums is not null)
        {
            foreach (var album in albums)
            {
                if (album is "." or "..") continue;
                if (album.Length != 8 || !album.EndsWith("APPLE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!album[..3].All(char.IsAsciiDigit)) continue;
                if (string.Compare(album, best, StringComparison.OrdinalIgnoreCase) > 0) best = album;
            }
        }

        if (best.Length > 0) return $"/DCIM/{best}";

        const string fallback = "/DCIM/100APPLE";
        EnsureDirectory(afc, client, fallback);
        return fallback;
    }

    private static string NextDcimImportPath(string dcimFolder, string ext, HashSet<string> taken)
    {
        if (string.IsNullOrEmpty(ext)) ext = ".JPG";
        ext = ext.ToUpperInvariant();

        for (var n = 9000; n < 9999; n++)
        {
            var candidate = $"IMG_{n}{ext}";
            if (taken.Add(candidate)) return $"{dcimFolder}/{candidate}";
        }

        var unique = $"IMG_{DateTime.UtcNow.Ticks % 10000:D4}{ext}";
        taken.Add(unique);
        return $"{dcimFolder}/{unique}";
    }

    private static string? ReadExistingLibraryUuid(IAfcApi afc, AfcClientHandle client)
    {
        string[] candidates =
        {
            "/Photos/Sync/CurrentLibraryUUID.plist",
            "/PhotoData/syncInfo.plist",
            "/PhotoData/Sync/CurrentLibraryUUID.plist"
        };

        foreach (var path in candidates)
        {
            try
            {
                var text = ReadAfcTextFile(afc, client, path);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var doc = XDocument.Parse(text);
                var dict = doc.Root?.Element("dict");
                if (dict == null) continue;

                var elements = dict.Elements().ToList();
                for (var i = 0; i < elements.Count - 1; i++)
                {
                    if (elements[i].Name == "key" && elements[i].Value == "LibraryUUID")
                    {
                        var val = elements[i + 1].Value;
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    private static XDocument? ReadExistingPhotoLibraryPlist(IAfcApi afc, AfcClientHandle client)
    {
        string[] candidates =
        {
            "/Photos/Sync/PhotoLibrary.plist",
            "/PhotoData/Sync/PhotoLibrary.plist"
        };

        foreach (var path in candidates)
        {
            try
            {
                var text = ReadAfcTextFile(afc, client, path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return XDocument.Parse(text);
                }
            }
            catch { }
        }

        return null;
    }

    private static string GenerateCurrentLibraryUuidPlist(string libraryUuid) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>LibraryUUID</key>
            <string>{libraryUuid}</string>
        </dict>
        </plist>
        """;

    private static string GenerateSyncInfoPlist(string libraryUuid) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>LibraryUUID</key>
            <string>{libraryUuid}</string>
            <key>SyncVersion</key>
            <integer>1</integer>
        </dict>
        </plist>
        """;

    /// <summary>
    /// Generates or merges the PhotoLibrary.plist manifest while strictly preserving all existing
    /// albums and assets on the device.
    /// </summary>
    public static string GeneratePhotoLibraryPlist(
        string libraryUuid,
        string albumName,
        IReadOnlyList<StagedAssetEntry> newAssets,
        XDocument? existingDoc = null)
    {
        XDocument doc;
        XElement rootDict;
        XElement albumsArray;
        XElement assetsArray;

        if (existingDoc?.Root?.Element("dict") is { } existingDict)
        {
            doc = new XDocument(existingDoc);
            rootDict = doc.Root!.Element("dict")!;

            // Ensure or update LibraryUUID
            var libUuidVal = GetDictString(rootDict, "LibraryUUID");
            if (string.IsNullOrEmpty(libUuidVal))
            {
                SetDictString(rootDict, "LibraryUUID", libraryUuid);
            }

            albumsArray = GetOrCreateDictArray(rootDict, "Albums");
            assetsArray = GetOrCreateDictArray(rootDict, "Assets");
        }
        else
        {
            rootDict = new XElement("dict");
            SetDictString(rootDict, "LibraryUUID", libraryUuid);

            albumsArray = new XElement("array");
            rootDict.Add(new XElement("key", "Albums"));
            rootDict.Add(albumsArray);

            assetsArray = new XElement("array");
            rootDict.Add(new XElement("key", "Assets"));
            rootDict.Add(assetsArray);

            doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
                new XElement("plist", new XAttribute("version", "1.0"), rootDict));
        }

        // Find or create album in albumsArray
        XElement? targetAlbumDict = null;
        foreach (var alb in albumsArray.Elements("dict"))
        {
            var name = GetDictString(alb, "AlbumName");
            if (string.Equals(name, albumName, StringComparison.OrdinalIgnoreCase))
            {
                targetAlbumDict = alb;
                break;
            }
        }

        var newAssetUuids = newAssets.Select(a => a.AssetUuid).ToList();

        if (targetAlbumDict != null)
        {
            // Existing album: preserve AlbumUUID and existing AssetUUIDs, append new assets
            var albumAssetArray = GetOrCreateDictArray(targetAlbumDict, "AssetUUIDs");
            var existingUuids = albumAssetArray.Elements("string").Select(s => s.Value).ToList();
            foreach (var uuid in newAssetUuids)
            {
                if (!existingUuids.Contains(uuid, StringComparer.OrdinalIgnoreCase))
                {
                    albumAssetArray.Add(new XElement("string", uuid));
                    existingUuids.Add(uuid);
                }
            }

            var keyAsset = GetDictString(targetAlbumDict, "KeyAssetUUID");
            if (string.IsNullOrEmpty(keyAsset) && existingUuids.Count > 0)
            {
                SetDictString(targetAlbumDict, "KeyAssetUUID", existingUuids[0]);
            }
        }
        else
        {
            // New album: add alongside any existing albums
            targetAlbumDict = new XElement("dict");
            var albUuid = Guid.NewGuid().ToString().ToUpperInvariant();
            var keyAsset = newAssetUuids.FirstOrDefault() ?? Guid.NewGuid().ToString().ToUpperInvariant();

            SetDictString(targetAlbumDict, "AlbumUUID", albUuid);
            SetDictString(targetAlbumDict, "AlbumName", albumName);
            SetDictString(targetAlbumDict, "KeyAssetUUID", keyAsset);

            var albumAssetArray = new XElement("array");
            foreach (var uuid in newAssetUuids)
            {
                albumAssetArray.Add(new XElement("string", uuid));
            }
            targetAlbumDict.Add(new XElement("key", "AssetUUIDs"));
            targetAlbumDict.Add(albumAssetArray);

            albumsArray.Add(targetAlbumDict);
        }

        // Add new assets to Assets array (avoiding duplicates by AssetUUID)
        var existingAssetUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetDict in assetsArray.Elements("dict"))
        {
            var u = GetDictString(assetDict, "AssetUUID");
            if (!string.IsNullOrEmpty(u)) existingAssetUuids.Add(u);
        }

        foreach (var a in newAssets)
        {
            if (existingAssetUuids.Contains(a.AssetUuid)) continue;

            var aDict = new XElement("dict");
            SetDictString(aDict, "AssetUUID", a.AssetUuid);
            SetDictString(aDict, "OriginalFilename", a.OriginalFilename);
            SetDictString(aDict, "Filename", a.Filename);
            SetDictString(aDict, "Directory", "100SYNCD");
            SetDictString(aDict, "UTI", a.Uti);
            SetDictInteger(aDict, "FileSize", a.FileSize);
            SetDictDate(aDict, "DateCreated", a.DateCreated);

            if (a.Width > 0 && a.Height > 0)
            {
                SetDictInteger(aDict, "Width", a.Width);
                SetDictInteger(aDict, "Height", a.Height);
            }

            assetsArray.Add(aDict);
            existingAssetUuids.Add(a.AssetUuid);
        }

        // Return serialized XML with Apple Plist declaration & doctype
        var sb = new StringBuilder();
        using (var writer = new Utf8StringWriter(sb))
        {
            doc.Save(writer, SaveOptions.None);
        }
        var xml = sb.ToString();

        // Ensure Apple DOCTYPE is present if not already added by doc.Save
        if (!xml.Contains("<!DOCTYPE plist"))
        {
            var header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n";
            if (xml.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                var idx = xml.IndexOf("?>", StringComparison.Ordinal);
                if (idx != -1) xml = xml[(idx + 2)..].TrimStart();
            }
            xml = header + xml;
        }

        return xml;
    }

    private static string? GetDictString(XElement dict, string key)
    {
        var elements = dict.Elements().ToList();
        for (var i = 0; i < elements.Count - 1; i++)
        {
            if (elements[i].Name == "key" && elements[i].Value == key && elements[i + 1].Name == "string")
                return elements[i + 1].Value;
        }
        return null;
    }

    private static void SetDictString(XElement dict, string key, string value)
    {
        var elements = dict.Elements().ToList();
        for (var i = 0; i < elements.Count - 1; i++)
        {
            if (elements[i].Name == "key" && elements[i].Value == key)
            {
                if (elements[i + 1].Name == "string")
                {
                    elements[i + 1].Value = value;
                    return;
                }
            }
        }
        dict.Add(new XElement("key", key));
        dict.Add(new XElement("string", value));
    }

    private static void SetDictInteger(XElement dict, string key, long value)
    {
        var elements = dict.Elements().ToList();
        for (var i = 0; i < elements.Count - 1; i++)
        {
            if (elements[i].Name == "key" && elements[i].Value == key)
            {
                if (elements[i + 1].Name == "integer")
                {
                    elements[i + 1].Value = value.ToString(CultureInfo.InvariantCulture);
                    return;
                }
            }
        }
        dict.Add(new XElement("key", key));
        dict.Add(new XElement("integer", value.ToString(CultureInfo.InvariantCulture)));
    }

    private static void SetDictDate(XElement dict, string key, DateTime value)
    {
        var dateStr = value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        dict.Add(new XElement("key", key));
        dict.Add(new XElement("date", dateStr));
    }

    private static XElement GetOrCreateDictArray(XElement dict, string key)
    {
        var elements = dict.Elements().ToList();
        for (var i = 0; i < elements.Count - 1; i++)
        {
            if (elements[i].Name == "key" && elements[i].Value == key && elements[i + 1].Name == "array")
                return elements[i + 1];
        }
        var arr = new XElement("array");
        dict.Add(new XElement("key", key));
        dict.Add(arr);
        return arr;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder sb) : base(sb) { }
        public override Encoding Encoding => Encoding.UTF8;
    }

    public static string GetUtiForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "public.jpeg",
        ".png" => "public.png",
        ".heic" => "public.heic",
        ".heif" => "public.heif",
        ".gif" => "com.compuserve.gif",
        ".bmp" => "com.microsoft.bmp",
        ".tif" or ".tiff" => "public.tiff",
        ".mov" => "com.apple.quicktime-movie",
        ".mp4" => "public.mpeg-4",
        ".m4v" => "com.apple.m4v-video",
        ".avi" => "public.avi",
        _ => "public.image"
    };

    public static (int Width, int Height) ProbeDimensions(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png")
            {
                using var fs = File.OpenRead(path);
                var header = new byte[24];
                if (fs.Read(header, 0, header.Length) == header.Length)
                {
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    {
                        var w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                        var h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                        return (w, h);
                    }
                }
            }
            else if (ext is ".jpg" or ".jpeg")
            {
                using var fs = File.OpenRead(path);
                var b1 = fs.ReadByte();
                var b2 = fs.ReadByte();
                if (b1 == 0xFF && b2 == 0xD8) // SOI
                {
                    while (fs.Position < fs.Length)
                    {
                        int markerPrefix = fs.ReadByte();
                        while (markerPrefix == 0xFF)
                        {
                            markerPrefix = fs.ReadByte();
                        }
                        int marker = markerPrefix;
                        if (marker is -1 or 0xD9 or 0xDA) break; // EOF or EOI or SOS

                        var lenHi = fs.ReadByte();
                        var lenLo = fs.ReadByte();
                        if (lenHi == -1 || lenLo == -1) break;
                        var markerLen = (lenHi << 8) | lenLo;

                        if (marker is 0xC0 or 0xC1 or 0xC2) // SOF0, SOF1, SOF2
                        {
                            fs.ReadByte(); // precision
                            var hHi = fs.ReadByte();
                            var hLo = fs.ReadByte();
                            var wHi = fs.ReadByte();
                            var wLo = fs.ReadByte();
                            if (hHi != -1 && hLo != -1 && wHi != -1 && wLo != -1)
                            {
                                var h = (hHi << 8) | hLo;
                                var w = (wHi << 8) | wLo;
                                return (w, h);
                            }
                            break;
                        }

                        if (markerLen > 2)
                        {
                            fs.Seek(markerLen - 2, SeekOrigin.Current);
                        }
                    }
                }
            }
        }
        catch { }
        return (0, 0);
    }

    private static long ReadFileSize(IAfcApi afc, AfcClientHandle client, string path)
    {
        if (afc.afc_get_file_info(client, path, out var info) != AfcError.Success || info == null)
            return -1;

        for (var i = 0; i + 1 < info.Count; i += 2)
        {
            if (info[i] == "st_size" && long.TryParse(info[i + 1], out var size))
                return size;
        }
        return -1;
    }

    private static string? ReadAfcTextFile(IAfcApi afc, AfcClientHandle client, string remotePath)
    {
        ulong handle = 0;
        if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
            return null;

        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[64 * 1024];
            uint read;
            do
            {
                read = 0;
                if (afc.afc_file_read(client, handle, buf, (uint)buf.Length, ref read) != AfcError.Success) break;
                if (read > 0) ms.Write(buf, 0, (int)read);
            }
            while (read > 0);

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        finally
        {
            afc.afc_file_close(client, handle);
        }
    }

    private static void WriteAfcTextFile(IAfcApi afc, AfcClientHandle client, string remotePath, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ulong handle = 0;
        if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenWronly, ref handle) != AfcError.Success)
        {
            throw new IOException($"AFC failed to open '{remotePath}' for writing.");
        }

        try
        {
            uint written = 0;
            var err = afc.afc_file_write(client, handle, bytes, (uint)bytes.Length, ref written);
            if (err != AfcError.Success || written != (uint)bytes.Length)
            {
                throw new IOException($"AFC write failure writing to '{remotePath}' ({err}).");
            }
        }
        finally
        {
            afc.afc_file_close(client, handle);
        }
    }

    private static void LinkOrCopyAfcFile(IAfcApi afc, AfcClientHandle client, string sourceRemotePath, string destRemotePath, string localFallbackPath)
    {
        try
        {
            var linkErr = afc.afc_make_link(client, AfcLinkType.Hardlink, sourceRemotePath, destRemotePath);
            if (linkErr == AfcError.Success) return;
        }
        catch { }

        try
        {
            WriteAfcLocalFile(afc, client, localFallbackPath, destRemotePath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"AirTraffic: mirror fallback error on '{destRemotePath}': {ex.Message}");
        }
    }

    private static void WriteAfcLocalFile(IAfcApi afc, AfcClientHandle client, string localPath, string remotePath)
    {
        var parent = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent))
        {
            EnsureDirectory(afc, client, parent);
        }

        ulong handle = 0;
        var openErr = afc.afc_file_open(client, remotePath, AfcFileMode.FopenWronly, ref handle);
        if (openErr != AfcError.Success)
        {
            AppLog.Warn($"AirTraffic: cannot open destination '{remotePath}' for writing ({openErr})");
            return;
        }

        try
        {
            using var stream = File.OpenRead(localPath);
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                uint written = 0;
                var chunk = read == buffer.Length ? buffer : buffer[..read];
                var writeErr = afc.afc_file_write(client, handle, chunk, (uint)read, ref written);
                if (writeErr != AfcError.Success || written != (uint)read)
                {
                    AppLog.Warn($"AirTraffic: write failure on '{remotePath}' ({writeErr})");
                    break;
                }
            }
        }
        finally
        {
            afc.afc_file_close(client, handle);
        }
    }

    private static void PostSyncNotifications(string udid)
    {
        string[] notifications =
        [
            "com.apple.itunes-mobdev.syncWillStart",
            "com.apple.itunes-mobdev.syncDidStart",
            "com.apple.mobile.data_sync",
            "com.apple.mobile.data_sync.domain_changed",
            "com.apple.mobile.sync_data_class",
            "com.apple.mobile.tethered_sync",
            "com.apple.mobile.lockdown.device_name_changed",
            "com.apple.itunes-mobdev.syncDidFinish",
        ];

        try
        {
            NativeDevice.EnsureLoaded();
            var np = LibiMobileDevice.Instance.NotificationProxy;
            if (NativeDevice.Open(udid, out var device) != iDeviceError.Success) return;

            using (device)
            {
                if (np.np_client_start_service(device, out var client, "IPAStudio") != NotificationProxyError.Success)
                    return;

                using (client)
                {
                    foreach (var n in notifications)
                    {
                        np.np_post_notification(client, n);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"AirTraffic: failed to post sync notifications: {ex.Message}");
        }
    }

    private static AfcSession OpenAfcSession(string udid)
    {
        NativeDevice.EnsureLoaded();
        var idevice = LibiMobileDevice.Instance.iDevice;
        var afc = LibiMobileDevice.Instance.Afc;

        NativeDevice.Open(udid, out var deviceHandle).ThrowOnError();
        try
        {
            afc.afc_client_start_service(deviceHandle, out var afcHandle, "IPAStudio").ThrowOnError();
            return new AfcSession(afc, deviceHandle, afcHandle);
        }
        catch
        {
            deviceHandle.Dispose();
            throw;
        }
    }

    private sealed class AfcSession : IDisposable
    {
        public IAfcApi Afc { get; }
        public AfcClientHandle Client { get; }
        private readonly iDeviceHandle _device;

        public AfcSession(IAfcApi afc, iDeviceHandle device, AfcClientHandle client)
        {
            Afc = afc;
            _device = device;
            Client = client;
        }

        public void Dispose()
        {
            try { Client.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
        }
    }
}

public readonly record struct StagedAssetEntry(
    string AssetUuid,
    string OriginalFilename,
    string Filename,
    string Uti,
    long FileSize,
    DateTime DateCreated,
    int Width,
    int Height);
