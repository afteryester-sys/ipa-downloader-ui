using System.Collections.ObjectModel;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.HouseArrest;
using iMobileDevice.iDevice;

namespace IPAStudio.Core.Services;

/// <summary>An installed app whose Documents directory is exposed by Apple File Sharing.</summary>
public sealed record FileSharingApp(string BundleId, string Name);

/// <summary>Verified progress for a file copied into an application's Documents directory.</summary>
public sealed record FileSharingProgress(string FileName, long BytesWritten, long TotalBytes)
{
    public double Percent => TotalBytes > 0 ? BytesWritten * 100d / TotalBytes : 0;
}

/// <summary>
/// Transfers arbitrary files through Apple's supported House Arrest / AFC document-sharing
/// channel. Unlike writing into the phone's media partition, VendDocuments gives us the
/// sandbox of one opted-in application and therefore works without touching private databases.
/// </summary>
public sealed class FileSharingService
{
    private const uint ChunkSize = 256 * 1024;
    private readonly InstallService _install;

    public FileSharingService(InstallService install) => _install = install;

    /// <summary>
    /// Returns only applications whose Documents container can actually be opened. Metadata
    /// flags are advisory and frequently absent on recent iOS, so a real VendDocuments probe is
    /// the source of truth. Probes are sequential because lockdown services on one USB session
    /// become unreliable when several are opened concurrently.
    /// </summary>
    public async Task<IReadOnlyList<FileSharingApp>> GetAvailableAppsAsync(
        string udid, CancellationToken ct = default)
    {
        var installed = await _install.GetInstalledAppsAsync(udid, ct).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<FileSharingApp>>(() =>
        {
            var result = new List<FileSharingApp>();
            foreach (var app in installed)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var session = OpenDocuments(udid, app.BundleId);
                    result.Add(new FileSharingApp(app.BundleId, app.Name));
                }
                catch (Exception ex)
                {
                    AppLog.Info($"File sharing: {app.BundleId} rejected VendDocuments ({ex.Message})");
                }
            }

            AppLog.Info($"File sharing: {result.Count} of {installed.Count} apps expose Documents");
            return result.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a local file and verifies the resulting size on-device. The temporary suffix keeps
    /// apps from observing a partial document; cancellation or any short write removes it.
    /// </summary>
    public Task<string> UploadAsync(
        string udid,
        FileSharingApp app,
        string localPath,
        IProgress<FileSharingProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!File.Exists(localPath)) throw new FileNotFoundException("File not found", localPath);

            using var session = OpenDocuments(udid, app.BundleId);
            var afc = session.Afc;
            var client = session.Client;
            var info = new FileInfo(localPath);
            var safeName = SafeFileName(info.Name);
            var destination = UniquePath(afc, client, "/Documents", safeName);
            var temporary = destination + ".ipastudio-part";
            ulong handle = 0;

            try
            {
                afc.afc_file_open(client, temporary, AfcFileMode.FopenWronly, ref handle).ThrowOnError();
                using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[ChunkSize];
                long writtenTotal = 0;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;

                    var offset = 0;
                    while (offset < read)
                    {
                        var slice = offset == 0 && read == buffer.Length
                            ? buffer
                            : buffer.AsSpan(offset, read - offset).ToArray();
                        uint written = 0;
                        afc.afc_file_write(client, handle, slice, (uint)slice.Length, ref written).ThrowOnError();
                        if (written == 0) throw new IOException("The device accepted no data");
                        offset += checked((int)written);
                        writtenTotal += written;
                    }

                    progress?.Report(new FileSharingProgress(info.Name, writtenTotal, info.Length));
                }

                afc.afc_file_close(client, handle).ThrowOnError();
                handle = 0;
                afc.afc_rename_path(client, temporary, destination).ThrowOnError();

                var remoteSize = RemoteSize(afc, client, destination);
                if (remoteSize != info.Length)
                {
                    TryRemove(afc, client, destination);
                    throw new IOException($"Size verification failed ({remoteSize} of {info.Length} bytes)");
                }

                AppLog.Info($"File sharing: verified {info.Length} bytes in {app.BundleId}");
                return destination;
            }
            catch
            {
                if (handle != 0) afc.afc_file_close(client, handle);
                TryRemove(afc, client, temporary);
                throw;
            }
        }, ct);

    private static DocumentsSession OpenDocuments(string udid, string bundleId)
    {
        NativeDevice.EnsureLoaded();
        var lib = LibiMobileDevice.Instance;
        var openError = NativeDevice.Open(udid, out var device);
        if (openError != iDeviceError.Success)
            throw new InvalidOperationException($"Could not open device: {openError}");
        try
        {
            lib.HouseArrest.house_arrest_client_start_service(device, out var house, "IPAStudio").ThrowOnError();
            try
            {
                lib.HouseArrest.house_arrest_send_command(house, "VendDocuments", bundleId).ThrowOnError();
                lib.HouseArrest.house_arrest_get_result(house, out var response).ThrowOnError();
                response.Dispose();
                lib.HouseArrest.afc_client_new_from_house_arrest_client(house, out var afc).ThrowOnError();
                return new DocumentsSession(lib.Afc, device, house, afc);
            }
            catch
            {
                house.Dispose();
                throw;
            }
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static string UniquePath(IAfcApi afc, AfcClientHandle client, string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 0; i < 10_000; i++)
        {
            var candidate = $"{folder}/{stem}{(i == 0 ? "" : $" ({i})")}{extension}";
            if (afc.afc_get_file_info(client, candidate, out _) != AfcError.Success) return candidate;
        }
        throw new IOException("Could not choose a free destination name");
    }

    private static long RemoteSize(IAfcApi afc, AfcClientHandle client, string path)
    {
        afc.afc_get_file_info(client, path, out ReadOnlyCollection<string> values).ThrowOnError();
        for (var i = 0; i + 1 < values.Count; i += 2)
            if (values[i] == "st_size" && long.TryParse(values[i + 1], out var size)) return size;
        return -1;
    }

    private static string SafeFileName(string name)
    {
        var chars = name.Select(ch => ch is '/' or '\\' || char.IsControl(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(result) ? "document" : result.Length <= 180 ? result : result[..180];
    }

    private static void TryRemove(IAfcApi afc, AfcClientHandle client, string path)
    {
        try { afc.afc_remove_path(client, path); } catch { }
    }

    private sealed class DocumentsSession : IDisposable
    {
        public IAfcApi Afc { get; }
        public AfcClientHandle Client { get; }
        private readonly iMobileDevice.iDevice.iDeviceHandle _device;
        private readonly HouseArrestClientHandle _house;

        public DocumentsSession(IAfcApi afcApi, iMobileDevice.iDevice.iDeviceHandle device,
            HouseArrestClientHandle house, AfcClientHandle client)
        {
            Afc = afcApi;
            _device = device;
            _house = house;
            Client = client;
        }

        public void Dispose()
        {
            Client.Dispose();
            _house.Dispose();
            _device.Dispose();
        }
    }
}
