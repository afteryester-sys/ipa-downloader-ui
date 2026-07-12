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

    /// <summary>Lists every photo and video in the Camera Roll.</summary>
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

                    var remotePath = $"{albumPath}/{name}";
                    var info = ReadFileInfo(afc, client, remotePath);
                    if (info.IsDirectory) continue;

                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    items.Add(new PhotoItem
                    {
                        RemotePath = remotePath,
                        FileName = name,
                        Album = album,
                        SizeBytes = info.Size,
                        IsVideo = VideoExtensions.Contains(ext),
                        ModifiedUtc = info.Modified,
                    });
                }
            }

            return items
                .OrderByDescending(i => i.ModifiedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(i => i.FileName)
                .ToList();
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

    private static (bool IsDirectory, long Size, DateTimeOffset? Modified) ReadFileInfo(
        IAfcApi afc, AfcClientHandle client, string path)
    {
        if (afc.afc_get_file_info(client, path, out ReadOnlyCollection<string> info) != AfcError.Success || info is null)
            return (false, 0, null);

        long size = 0;
        var isDir = false;
        DateTimeOffset? modified = null;

        for (var i = 0; i + 1 < info.Count; i += 2)
        {
            var key = info[i];
            var value = info[i + 1];
            switch (key)
            {
                case "st_size" when long.TryParse(value, out var s): size = s; break;
                case "st_ifmt": isDir = value == "S_IFDIR"; break;
                case "st_mtime" when long.TryParse(value, out var ns):
                    // libimobiledevice reports nanoseconds since the Unix epoch.
                    modified = DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000);
                    break;
            }
        }

        return (isDir, size, modified);
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
