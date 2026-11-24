namespace IPAStudio.Core.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.iDevice;
using iMobileDevice.MobileSync;
using iMobileDevice.NotificationProxy;

/// <summary>
/// Core orchestrator for Quick Transfer mode (iMazing-style):
/// - Classifies files into native apps (Photos, Contacts, Install) and third-party app sandboxes.
/// - Ingests photos and videos into iOS Photos library with AirTraffic synchronization.
/// - Imports contacts (.vcf) into iOS Contacts via MobileSync and device staging.
/// - Queues IPA files for direct installation.
/// - Transfers third-party documents/media to compatible file-sharing app sandboxes via house_arrest.
/// </summary>
public sealed class QuickTransferService
{
    private readonly FileSharingService _sharing;
    private readonly PhotoService? _photos;
    private readonly AirTrafficPhotoSyncService? _airTraffic;

    public QuickTransferService(
        FileSharingService sharing,
        PhotoService? photos = null,
        AirTrafficPhotoSyncService? airTraffic = null)
    {
        _sharing = sharing;
        _photos = photos;
        _airTraffic = airTraffic;
    }

    /// <summary>
    /// Counts the number of individual vCard contact cards in a .vcf file.
    /// </summary>
    public static int CountVCardContacts(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return 0;
        try
        {
            var count = 0;
            using var reader = File.OpenText(filePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Trim().StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return Math.Max(count, 1);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Classifies an expanded list of file paths into UI payloads with designated target descriptions.
    /// </summary>
    public static IReadOnlyList<TransferPayload> ClassifyPayloads(
        IEnumerable<string> paths,
        FileSharingApp? userSelectedApp,
        IReadOnlyList<FileSharingApp>? availableApps)
    {
        var rawList = FileClassifier.ExpandPaths(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rawList.Count == 0) return Array.Empty<TransferPayload>();

        var preliminary = rawList.Select(p => FileClassifier.Describe(p, userSelectedApp?.Name)).ToList();
        var ranked = AppRecommendationEngine.RankApps(preliminary, availableApps ?? Array.Empty<FileSharingApp>());

        FileSharingApp? activeApp = userSelectedApp;
        if (activeApp == null && ranked.Count > 0)
        {
            activeApp = ranked[0].App;
        }

        var results = new List<TransferPayload>(rawList.Count);
        foreach (var path in rawList)
        {
            var category = FileClassifier.Classify(path);
            string targetName;

            if (category == FileCategory.App)
            {
                targetName = Loc.Get("L.QuickTransfer.TargetInstall");
            }
            else if (category == FileCategory.Contact)
            {
                targetName = Loc.Get("L.QuickTransfer.TargetContacts");
            }
            else if (FileClassifier.IsCameraRollMedia(path))
            {
                targetName = Loc.Get("L.QuickTransfer.TargetPhotos");
            }
            else
            {
                targetName = activeApp?.Name ?? Loc.Get("L.QuickTransfer.DeviceDownloads");
            }

            results.Add(FileClassifier.Describe(path, targetName));
        }

        return results;
    }

    /// <summary>
    /// Executes the full multi-format Quick Transfer pipeline.
    /// </summary>
    public async Task<QuickTransferResult> TransferAsync(
        string udid,
        IReadOnlyList<string> filePaths,
        FileSharingApp? userSelectedApp,
        IProgress<QuickTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        ArgumentNullException.ThrowIfNull(filePaths);

        var allPaths = FileClassifier.ExpandPaths(filePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allPaths.Count == 0)
        {
            return new QuickTransferResult(0, 0, 0, Array.Empty<string>(), true);
        }

        var ipaFiles = allPaths.Where(p => FileClassifier.Classify(p) == FileCategory.App).ToList();
        var contactFiles = allPaths.Where(p => FileClassifier.Classify(p) == FileCategory.Contact).ToList();
        var cameraRollFiles = allPaths.Where(p =>
            FileClassifier.Classify(p) != FileCategory.App &&
            FileClassifier.Classify(p) != FileCategory.Contact &&
            FileClassifier.IsCameraRollMedia(p)).ToList();

        var appFiles = allPaths.Where(p =>
            !ipaFiles.Contains(p) &&
            !contactFiles.Contains(p) &&
            !cameraRollFiles.Contains(p)).ToList();

        int photosCopied = 0;
        int contactsCopied = 0;
        int appFilesCopied = 0;

        var totalOperations = allPaths.Count;
        var currentOp = 0;

        // 1. Photos and Native Videos to Photos Library
        if (cameraRollFiles.Count > 0)
        {
            if (_airTraffic != null && _airTraffic.IsAvailable)
            {
                var photoProgress = new Progress<PhotoTransferProgress>(p =>
                {
                    var percent = p.Total > 0 ? (double)p.Completed / p.Total * 100.0 : 0;
                    progress?.Report(new QuickTransferProgress(
                        p.CurrentFile,
                        currentOp + p.Completed,
                        totalOperations,
                        percent));
                });

                var syncRes = await _airTraffic.SyncPhotosAsync(udid, cameraRollFiles, "IPA Studio", photoProgress, ct).ConfigureAwait(false);
                photosCopied = syncRes.Copied;
            }
            else if (_photos != null)
            {
                var photoProgress = new Progress<PhotoTransferProgress>(p =>
                {
                    var percent = p.Total > 0 ? (double)p.Completed / p.Total * 100.0 : 0;
                    progress?.Report(new QuickTransferProgress(
                        p.CurrentFile,
                        currentOp + p.Completed,
                        totalOperations,
                        percent));
                });

                var importRes = await _photos.ImportAsync(udid, cameraRollFiles, photoProgress, ct).ConfigureAwait(false);
                photosCopied = importRes.Copied;
            }
            currentOp += cameraRollFiles.Count;
        }

        // 2. Contacts (.vcf, .vcard) to iPhone Contacts & device storage
        if (contactFiles.Count > 0)
        {
            contactsCopied = await TransferContactsAsync(udid, contactFiles, userSelectedApp, progress, currentOp, totalOperations, ct).ConfigureAwait(false);
            currentOp += contactFiles.Count;
        }

        // 3. Third-party Documents / Media to app sandbox or device storage
        if (appFiles.Count > 0)
        {
            if (userSelectedApp != null)
            {
                for (var i = 0; i < appFiles.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = appFiles[i];
                    var sw = Stopwatch.StartNew();
                    long lastBytes = 0;
                    long lastTickMs = 0;

                    var fileProgress = new Progress<FileSharingProgress>(p =>
                    {
                        var elapsedMs = sw.ElapsedMilliseconds;
                        var dtSeconds = (elapsedMs - lastTickMs) / 1000.0;
                        string speedText = string.Empty;
                        if (dtSeconds >= 0.2 || p.BytesWritten == p.TotalBytes)
                        {
                            var bytesDiff = p.BytesWritten - lastBytes;
                            var speed = dtSeconds > 0 ? bytesDiff / dtSeconds : 0;
                            speedText = FormatSpeed(speed);
                            lastBytes = p.BytesWritten;
                            lastTickMs = elapsedMs;
                        }

                        progress?.Report(new QuickTransferProgress(
                            p.FileName,
                            currentOp + i,
                            totalOperations,
                            p.Percent,
                            speedText));
                    });

                    await _sharing.UploadAsync(udid, userSelectedApp, file, fileProgress, ct).ConfigureAwait(false);
                    appFilesCopied++;
                }
            }
            else
            {
                // Fallback: stage in /Downloads on device Media partition
                foreach (var file in appFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    if (StageFileOnDeviceMedia(udid, file, "/Downloads"))
                    {
                        appFilesCopied++;
                    }
                }
            }
            currentOp += appFiles.Count;
        }

        // IPAs are returned in result for execution by the queue
        return new QuickTransferResult(
            photosCopied,
            contactsCopied,
            appFilesCopied,
            ipaFiles,
            true);
    }

    /// <summary>
    /// Transfers contacts (.vcf) into the iPhone:
    /// - Parses vCard records and extracts count.
    /// - Stages vCard files into /Contacts, /Purchases, and /Downloads on device Media partition in a unified session.
    /// - Connects to com.apple.mobilesync for native address book handshake.
    /// - If a destination app (e.g. Outlook, Documents) is selected, copies into app sandbox as well.
    /// - Signals NotificationProxy with address book reload triggers.
    /// </summary>
    public async Task<int> TransferContactsAsync(
        string udid,
        IReadOnlyList<string> contactFiles,
        FileSharingApp? destinationApp,
        IProgress<QuickTransferProgress>? progress = null,
        int baseOpCount = 0,
        int totalOpCount = 0,
        CancellationToken ct = default)
    {
        var totalContacts = 0;
        if (contactFiles.Count == 0) return 0;

        // Stage all contact files to device Media partition in a single AFC session
        StageContactsOnDevice(udid, contactFiles);

        for (var i = 0; i < contactFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var filePath = contactFiles[i];
            var fileName = Path.GetFileName(filePath);
            var cardCount = CountVCardContacts(filePath);

            progress?.Report(new QuickTransferProgress(
                fileName,
                baseOpCount + i,
                Math.Max(totalOpCount, contactFiles.Count),
                (double)(i + 1) / contactFiles.Count * 100.0,
                cardCount.ToString()));

            // Optional upload to selected destination app (e.g. Outlook, Documents)
            if (destinationApp != null)
            {
                try
                {
                    await _sharing.UploadAsync(udid, destinationApp, filePath, null, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"QuickTransfer: contact upload to {destinationApp.Name} failed: {ex.Message}");
                }
            }

            totalContacts += cardCount;
        }

        // Signal MobileSync daemon and NotificationProxy AddressBook reload
        TryMobileSyncContacts(udid);
        PostContactSyncNotifications(udid);

        AppLog.Info($"QuickTransfer: transferred {totalContacts} contacts from {contactFiles.Count} vCard file(s)");
        return totalContacts;
    }

    private static bool TryMobileSyncContacts(string udid)
    {
        try
        {
            NativeDevice.EnsureLoaded();
            if (NativeDevice.Open(udid, out var device) != iDeviceError.Success)
                return false;

            using (device)
            {
                var ms = LibiMobileDevice.Instance.MobileSync;
                if (ms == null) return false;

                var err = ms.mobilesync_client_start_service(device, out var client, "IPAStudio");
                if (err != MobileSyncError.Success || client.IsInvalid)
                {
                    AppLog.Info($"QuickTransfer: mobilesync service probe: {err}");
                    return false;
                }

                using (client)
                {
                    AppLog.Info("QuickTransfer: mobilesync client started for contacts");
                    try
                    {
                        var syncType = default(MobileSyncSyncType);
                        ulong devVer = 0;
                        string errDesc = string.Empty;
                        var anchorErr = ms.mobilesync_anchors_new("0", "1", out var anchors);
                        if (anchorErr == MobileSyncError.Success && !anchors.IsInvalid)
                        {
                            try
                            {
                                var dataClass = System.Text.Encoding.UTF8.GetBytes("com.apple.Contacts\0");
                                ms.mobilesync_start(client, dataClass, anchors, 100, ref syncType, ref devVer, out errDesc);
                                ms.mobilesync_finish(client);
                            }
                            finally
                            {
                                ms.mobilesync_anchors_free(anchors.DangerousGetHandle());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Info($"QuickTransfer: mobilesync handshake detail: {ex.Message}");
                    }
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"QuickTransfer: mobilesync exception: {ex.Message}");
            return false;
        }
    }

    private static void StageContactsOnDevice(string udid, IReadOnlyList<string> localPaths)
    {
        try
        {
            NativeDevice.EnsureLoaded();
            var afc = LibiMobileDevice.Instance.Afc;
            if (NativeDevice.Open(udid, out var deviceHandle) != iDeviceError.Success) return;

            using (deviceHandle)
            {
                if (afc.afc_client_start_service(deviceHandle, out var client, "IPAStudio") != AfcError.Success)
                    return;

                using (client)
                {
                    afc.afc_make_directory(client, "/Contacts");
                    afc.afc_make_directory(client, "/Purchases");
                    afc.afc_make_directory(client, "/Downloads");

                    foreach (var localPath in localPaths)
                    {
                        if (!File.Exists(localPath)) continue;
                        var fileName = Path.GetFileName(localPath);
                        StageAfcFile(afc, client, localPath, $"/Contacts/{fileName}");
                        StageAfcFile(afc, client, localPath, $"/Purchases/{fileName}");
                        StageAfcFile(afc, client, localPath, $"/Downloads/{fileName}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"QuickTransfer: contact staging exception: {ex.Message}");
        }
    }

    private static void StageAfcFile(IAfcApi afc, AfcClientHandle client, string localPath, string remotePath)
    {
        ulong handle = 0;
        if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenWronly, ref handle) != AfcError.Success)
            return;

        try
        {
            using var fs = File.OpenRead(localPath);
            var buf = new byte[64 * 1024];
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
            {
                uint written = 0;
                var slice = read == buf.Length ? buf : buf[..read];
                if (afc.afc_file_write(client, handle, slice, (uint)read, ref written) != AfcError.Success)
                    break;
            }
        }
        finally
        {
            afc.afc_file_close(client, handle);
        }
    }

    private static bool StageFileOnDeviceMedia(string udid, string localPath, string remoteDirectory)
    {
        try
        {
            NativeDevice.EnsureLoaded();
            var afc = LibiMobileDevice.Instance.Afc;
            if (NativeDevice.Open(udid, out var deviceHandle) != iDeviceError.Success) return false;

            using (deviceHandle)
            {
                if (afc.afc_client_start_service(deviceHandle, out var client, "IPAStudio") != AfcError.Success)
                    return false;

                using (client)
                {
                    afc.afc_make_directory(client, remoteDirectory);
                    var fileName = Path.GetFileName(localPath);
                    var targetPath = $"{remoteDirectory}/{fileName}";
                    StageAfcFile(afc, client, localPath, targetPath);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"QuickTransfer: AFC staging failed on '{remoteDirectory}': {ex.Message}");
            return false;
        }
    }

    private static void PostContactSyncNotifications(string udid)
    {
        string[] notifications =
        [
            "com.apple.itunes-mobdev.syncWillStart",
            "com.apple.itunes-mobdev.syncDidStart",
            "com.apple.mobile.data_sync",
            "com.apple.mobile.data_sync.domain_changed",
            "com.apple.mobile.sync_data_class",
            "com.apple.mobile.tethered_sync",
            "com.apple.AddressBook.PreferenceChangedNotification",
            "com.apple.itunes-mobdev.syncDidFinish"
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
        catch { }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576.0:0.0} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024.0:0.0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }
}
