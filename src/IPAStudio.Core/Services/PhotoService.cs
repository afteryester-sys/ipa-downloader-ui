using System.Collections.ObjectModel;
using IPAStudio.Core.Models;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.iDevice;

namespace IPAStudio.Core.Services;

/// <summary>
/// Provides access to the device Camera Roll (the DCIM folder) over the AFC
/// protocol using the managed libimobiledevice bindings. Supports listing,
/// exporting (device -> PC) and importing (PC -> device) of photos and videos.
///
/// Note: AFC only exposes the Camera Roll (DCIM). Synced albums and the Photos
/// library database are not reachable this way, so items are grouped by their
/// DCIM sub-folder (e.g. "100APPLE"), which is the closest album-like grouping
/// available without a full device backup.
/// </summary>
public sealed class PhotoService
{
    private static readonly string[] VideoExtensions = { ".mov", ".mp4", ".m4v", ".avi" };

    /// <summary>
    /// Extensions we treat as Camera Roll media. Recognising media by extension lets the
    /// listing skip a per-file AFC stat: it decides what to show from the directory entry
    /// alone. It also filters out the non-media files iOS leaves in DCIM (.AAE edit
    /// sidecars, Thumbs.db, .MISC) which the old size-based pass happily listed as 0-byte
    /// entries.
    /// </summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Stills
        ".heic", ".heif", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".dng", ".cr2", ".nef", ".arw",
        // Video
        ".mov", ".mp4", ".m4v", ".avi",
    };

    /// <summary>
    /// How many files to stat before reporting progress. Batching keeps a 5 000 photo
    /// roll from marshalling 5 000 separate UI updates, which cost far more than the
    /// stats themselves.
    /// </summary>
    private const int MetadataBatchSize = 64;

    private const uint ChunkSize = 1024 * 256; // 256 KiB per AFC read/write.

    private static bool _nativeLoaded;
    private static readonly object NativeLock = new();

    private static void EnsureNativeLoaded()
    {
        if (_nativeLoaded) return;
        lock (NativeLock)
        {
            if (_nativeLoaded) return;
            NativeLibraries.Load();
            _nativeLoaded = true;
        }
    }

    /// <summary>Opens an AFC session to the device; caller must dispose the result.</summary>
    private static AfcSession OpenSession(string udid)
    {
        EnsureNativeLoaded();

        var idevice = LibiMobileDevice.Instance.iDevice;
        var afc = LibiMobileDevice.Instance.Afc;

        idevice.idevice_new(out var deviceHandle, udid).ThrowOnError();
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

    /// <summary>
    /// Lists every photo and video in the Camera Roll.
    ///
    /// Deliberately does NOT read per-file size or date: that needs one AFC round-trip
    /// per file, and on a large roll those thousands of sequential round-trips were the
    /// entire reason the screen sat empty for many seconds. Here the cost is one
    /// directory read per album, so the grid can be populated almost immediately.
    /// Call <see cref="FillMetadataAsync"/> afterwards to fill in sizes and dates.
    ///
    /// Because no stat is performed, items are ordered by file name descending. iOS
    /// assigns DCIM names sequentially (IMG_0001, IMG_0002, …), so this closely matches
    /// newest-first and avoids the list visibly reshuffling once real dates arrive.
    /// </summary>
    public Task<IReadOnlyList<PhotoItem>> ListCameraRollAsync(string udid, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<PhotoItem>>(() =>
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var items = new List<PhotoItem>();

            // DCIM holds one or more sub-folders (100APPLE, 101APPLE, ...).
            if (afc.afc_read_directory(client, "/DCIM", out var albums) != AfcError.Success || albums is null)
                return items;

            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                if (album is "." or "..") continue;

                var albumPath = $"/DCIM/{album}";
                if (afc.afc_read_directory(client, albumPath, out var files) != AfcError.Success || files is null)
                    continue;

                foreach (var name in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (name is "." or "..") continue;

                    // Extension check replaces the old stat-based directory test: a
                    // nested folder never carries a media extension, so it drops out
                    // here without costing a round-trip.
                    var ext = Path.GetExtension(name);
                    if (string.IsNullOrEmpty(ext) || !MediaExtensions.Contains(ext)) continue;

                    items.Add(new PhotoItem
                    {
                        RemotePath = $"{albumPath}/{name}",
                        FileName = name,
                        Album = album,
                        IsVideo = VideoExtensions.Contains(ext.ToLowerInvariant()),
                    });
                }
            }

            items.Sort(static (a, b) => string.Compare(b.FileName, a.FileName, StringComparison.OrdinalIgnoreCase));
            return items;
        }, ct);

    /// <summary>
    /// Fetches size and last-modified date for the given items and reports them in
    /// batches, so the caller can fill the UI in progressively while this runs.
    ///
    /// Each batch contains only newly-stated items. Values are handed back through the
    /// progress callback rather than written into the items here: <see cref="PhotoItem"/>
    /// is read by the UI thread, and <c>DateTimeOffset?</c> is too wide to assign
    /// atomically, so a background write could be observed half-updated. Letting the
    /// caller apply them on its own thread keeps that safe.
    /// </summary>
    public Task FillMetadataAsync(
        string udid,
        IReadOnlyList<PhotoItem> items,
        IProgress<IReadOnlyList<PhotoMetadata>> progress,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (items.Count == 0) return;

            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var batch = new List<PhotoMetadata>(MetadataBatchSize);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                var info = ReadFileInfo(afc, client, item.RemotePath);
                batch.Add(new PhotoMetadata(item, info.Size, info.Modified));

                if (batch.Count >= MetadataBatchSize)
                {
                    progress.Report(batch);
                    batch = new List<PhotoMetadata>(MetadataBatchSize);
                }
            }

            if (batch.Count > 0) progress.Report(batch);
        }, ct);

    /// <summary>Copies the selected items from the device to a local folder.</summary>
    public Task<int> ExportAsync(
        string udid,
        IReadOnlyList<PhotoItem> items,
        string destinationFolder,
        IProgress<PhotoTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            Directory.CreateDirectory(destinationFolder);

            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var done = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new PhotoTransferProgress(done, items.Count, item.FileName));

                var localPath = MakeUniquePath(Path.Combine(destinationFolder, item.FileName));

                ulong handle = 0;
                if (afc.afc_file_open(client, item.RemotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                    continue;

                try
                {
                    using var output = File.Create(localPath);
                    var buffer = new byte[ChunkSize];
                    uint read;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        read = 0;
                        var err = afc.afc_file_read(client, handle, buffer, ChunkSize, ref read);
                        if (err != AfcError.Success) break;
                        if (read > 0) output.Write(buffer, 0, (int)read);
                    }
                    while (read > 0);
                }
                finally
                {
                    afc.afc_file_close(client, handle);
                }

                done++;
            }

            progress?.Report(new PhotoTransferProgress(done, items.Count, ""));
            return done;
        }, ct);

    /// <summary>Copies local files onto the device Camera Roll (DCIM).</summary>
    public Task<int> ImportAsync(
        string udid,
        IReadOnlyList<string> localFiles,
        IProgress<PhotoTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            // Import into a dedicated DCIM sub-folder so files land in the Camera Roll area.
            const string targetDir = "/DCIM/900IPAST";
            afc.afc_make_directory(client, targetDir); // ignore error if it already exists

            var done = 0;
            foreach (var local in localFiles)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(local)) continue;

                var name = Path.GetFileName(local);
                progress?.Report(new PhotoTransferProgress(done, localFiles.Count, name));

                var remotePath = $"{targetDir}/{name}";
                ulong handle = 0;
                if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenWronly, ref handle) != AfcError.Success)
                    continue;

                try
                {
                    using var input = File.OpenRead(local);
                    var buffer = new byte[ChunkSize];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        uint written = 0;
                        var chunk = read == buffer.Length ? buffer : buffer[..read];
                        if (afc.afc_file_write(client, handle, chunk, (uint)read, ref written) != AfcError.Success)
                            break;
                    }
                }
                finally
                {
                    afc.afc_file_close(client, handle);
                }

                done++;
            }

            progress?.Report(new PhotoTransferProgress(done, localFiles.Count, ""));
            return done;
        }, ct);

    /// <summary>
    /// Reads multiple files in a single AFC session, returning their raw bytes up to
    /// <paramref name="maxBytesEach"/> each. Much faster than opening a new session per
    /// file, because the USB/lockdown handshake happens only once.
    /// </summary>
    public Task<Dictionary<string, byte[]>> ReadFilesAsync(
        string udid,
        IReadOnlyList<string> remotePaths,
        long maxBytesEach,
        CancellationToken ct = default)
        => Task.Run<Dictionary<string, byte[]>>(() =>
        {
            var results = new Dictionary<string, byte[]>(remotePaths.Count);
            if (remotePaths.Count == 0) return results;

            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            foreach (var remotePath in remotePaths)
            {
                ct.ThrowIfCancellationRequested();

                ulong handle = 0;
                if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                    continue;

                try
                {
                    using var ms = new MemoryStream();
                    var buffer = new byte[ChunkSize];
                    uint read;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        read = 0;
                        if (afc.afc_file_read(client, handle, buffer, ChunkSize, ref read) != AfcError.Success) break;
                        if (read > 0) ms.Write(buffer, 0, (int)read);
                        if (maxBytesEach > 0 && ms.Length >= maxBytesEach) break;
                    }
                    while (read > 0);
                    results[remotePath] = ms.ToArray();
                }
                finally
                {
                    afc.afc_file_close(client, handle);
                }
            }

            return results;
        }, ct);

    /// <summary>Reads one media file fully into memory (used for thumbnails/preview).</summary>
    public Task<byte[]?> ReadFileAsync(string udid, string remotePath, long maxBytes, CancellationToken ct = default)
        => Task.Run<byte[]?>(() =>
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            ulong handle = 0;
            if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                return null;

            try
            {
                using var ms = new MemoryStream();
                var buffer = new byte[ChunkSize];
                uint read;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    read = 0;
                    if (afc.afc_file_read(client, handle, buffer, ChunkSize, ref read) != AfcError.Success) break;
                    if (read > 0) ms.Write(buffer, 0, (int)read);
                    if (maxBytes > 0 && ms.Length >= maxBytes) break;
                }
                while (read > 0);
                return ms.ToArray();
            }
            finally
            {
                afc.afc_file_close(client, handle);
            }
        }, ct);

    /// <summary>
    /// Reads size and modification date for one path. Costs a round-trip to the device,
    /// so callers should avoid it on the path that first populates the grid.
    /// </summary>
    private static (long Size, DateTimeOffset? Modified) ReadFileInfo(
        IAfcApi afc, AfcClientHandle client, string path)
    {
        if (afc.afc_get_file_info(client, path, out ReadOnlyCollection<string> info) != AfcError.Success || info is null)
            return (0, null);

        long size = 0;
        DateTimeOffset? modified = null;

        for (var i = 0; i + 1 < info.Count; i += 2)
        {
            var key = info[i];
            var value = info[i + 1];
            switch (key)
            {
                case "st_size" when long.TryParse(value, out var s): size = s; break;
                case "st_mtime" when long.TryParse(value, out var ns):
                    // libimobiledevice reports nanoseconds since the Unix epoch.
                    modified = DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000);
                    break;
            }
        }

        return (size, modified);
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Bundles the native handles for one AFC session.</summary>
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
            // Disposing the safe handles frees the native client/device for us
            // (afc_client_free takes a raw pointer, so we don't call it directly).
            try { Client.Dispose(); } catch { /* best effort */ }
            try { _device.Dispose(); } catch { /* best effort */ }
        }
    }
}

/// <summary>Progress for a photo export/import operation.</summary>
public readonly record struct PhotoTransferProgress(int Completed, int Total, string CurrentFile);

/// <summary>
/// Size and date fetched for one Camera Roll item, delivered separately from the item
/// itself so the owner of the UI thread decides when to apply it.
/// </summary>
public readonly record struct PhotoMetadata(PhotoItem Item, long SizeBytes, DateTimeOffset? ModifiedUtc);
