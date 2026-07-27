using System.IO;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Orchestrates the multi-app install pipeline:
///   Pending -> Checking (local IPA) -> Downloading -> [Licensing on demand] -> Installing -> Done/Failed
///
/// IMPORTANT (performance): the pipeline no longer probes the Apple ID license before
/// downloading. ipatool has no read-only "is it owned" query, so the old
/// CheckLicenseAsync call actually ran <c>purchase</c> — a full Apple authentication
/// handshake — and then <c>download --purchase</c> ran the very same handshake again.
/// Every app paid 2-3 handshakes (5-20 s of pure latency) before a single byte moved.
/// Now we go straight to <c>download --purchase</c>, which is idempotent and acquires
/// the license itself, and only fall back to an explicit purchase if the download
/// actually reports a licensing problem.
///
/// Downloads run in parallel (up to <see cref="MaxParallelDownloads"/>) with a small
/// stagger between starts; installs onto the device are serialized by
/// <see cref="InstallService"/>. Every state change raises <see cref="ItemChanged"/>
/// so the UI can animate stage transitions and progress.
/// </summary>
public sealed class QueueService
{
    /// <summary>
    /// Concurrent downloads.
    ///
    /// Raising this is the one lever that genuinely increases aggregate throughput.
    /// Apple's CDN shapes each individual connection, so a single stream frequently
    /// tops out well below the available line rate — a link that delivers 8 MB/s in
    /// total may give only 3 MB/s on one connection. Running several transfers at
    /// once recovers the difference.
    ///
    /// This is safe now only because the Apple authentication handshakes are
    /// serialized inside <see cref="DownloadService"/>: concurrency multiplies the
    /// byte streams without multiplying the auth load that Apple throttles on.
    ///
    /// Note this speeds up a *queue*, not a single app. One app still moves at
    /// single-stream speed.
    /// </summary>
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>
    /// Delay inserted before each download start after the first, so the Apple
    /// authentication handshakes do not collide.
    /// </summary>
    private static readonly TimeSpan StartStagger = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private int _launched;

    private readonly DownloadService _download;
    private readonly InstallService _install;
    private readonly CatalogService _catalog;
    private readonly SettingsService _settings;
    private readonly List<QueueItem> _items = new();
    private CancellationTokenSource? _cts;

    public QueueService(DownloadService download, InstallService install, CatalogService catalog, SettingsService settings)
    {
        _download = download;
        _install = install;
        _catalog = catalog;
        _settings = settings;
    }

    public IReadOnlyList<QueueItem> Items
    {
        get { lock (_items) return _items.ToList(); }
    }

    public bool IsRunning { get; private set; }

    /// <summary>Raised whenever an item's stage, progress or detail changes.</summary>
    public event EventHandler<QueueItem>? ItemChanged;

    /// <summary>Raised when the whole queue finishes (all items terminal).</summary>
    public event EventHandler? QueueCompleted;

    /// <summary>Raised when any ipatool command reports that the session has expired.
    /// The UI should redirect the user to the login screen.</summary>
    public event EventHandler? SessionExpired;

    /// <summary>Overall queue progress, 0-100 (equal weight per item).</summary>
    public double OverallProgress
    {
        get
        {
            lock (_items)
            {
                if (_items.Count == 0) return 0;
                var total = 0.0;
                foreach (var item in _items)
                    total += ItemProgressShare(item);
                return total / _items.Count * 100;
            }
        }
    }

    /// <summary>Builds a new queue for the given apps and device. Clears previous items.</summary>
    public void Build(IEnumerable<AppEntry> apps, Device device)
    {
        lock (_items)
        {
            _items.Clear();
            foreach (var app in apps)
                _items.Add(new QueueItem { App = app, TargetDevice = device });
        }
    }

    /// <summary>
    /// Builds a queue from IPA files already on disk (Direct IPA install mode).
    /// These items skip Checking/Licensing/Downloading and go straight to Installing.
    /// The install is independent of the signed-in Apple ID.
    /// </summary>
    public void BuildFromIpaFiles(IEnumerable<string> ipaPaths, Device device)
    {
        lock (_items)
        {
            _items.Clear();
            foreach (var path in ipaPaths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var app = new AppEntry
                {
                    Name = name,
                    AppStoreId = 0,
                    LocalIpaPath = path,
                    IsDownloaded = true,
                };
                _items.Add(new QueueItem
                {
                    App = app,
                    TargetDevice = device,
                    IsDirectIpaInstall = true,
                });
            }
        }
    }

    /// <summary>Starts processing the queue. No-op when already running.</summary>
    public async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Interlocked.Exchange(ref _launched, 0);

        // A previous run's "apply to all" answer for existing files must not silently
        // govern this one.
        _download.ResetFileConflictScope();

        try
        {
            List<QueueItem> pending;
            lock (_items) pending = _items.Where(i => i.Stage == QueueStage.Pending).ToList();

            // Downloads run in parallel; the install step inside ProcessItemAsync is
            // serialized by InstallService's device lock.
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelDownloads), CancellationToken = ct },
                async (item, token) => await ProcessItemAsync(item, token).ConfigureAwait(false)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_items)
            {
                foreach (var item in _items.Where(i => !IsTerminal(i.Stage)))
                {
                    item.Stage = QueueStage.Cancelled;
                    Notify(item);
                }
            }
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            QueueCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Requests cancellation of all in-flight work.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Retries a single failed item (used by the per-item "Retry" button).</summary>
    public async Task RetryAsync(QueueItem item)
    {
        if (item.Stage != QueueStage.Failed && item.Stage != QueueStage.Cancelled) return;
        item.RetryCount++;
        item.Stage = QueueStage.Pending;
        item.ErrorMessage = null;
        item.StageProgress = 0;
        Notify(item);
        await ProcessItemAsync(item, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        QueueCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Staggers download starts so concurrent items do not perform their Apple
    /// authentication handshakes at the same instant (which gets throttled).
    /// The first item is never delayed.
    /// </summary>
    private async Task StaggerStartAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Increment(ref _launched) > 1)
                await Task.Delay(StartStagger, ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task ProcessItemAsync(QueueItem item, CancellationToken ct)
    {
        item.StartedAt = DateTimeOffset.Now;
        try
        {
            // ---- Direct IPA install (file-picker mode): skip all store stages ----
            if (item.IsDirectIpaInstall)
            {
                if (string.IsNullOrEmpty(item.App.LocalIpaPath) || !File.Exists(item.App.LocalIpaPath))
                {
                    Fail(item, $"Файл IPA не найден: {item.App.LocalIpaPath}");
                    return;
                }
                await RunInstallStageAsync(item, item.App.LocalIpaPath!, ct).ConfigureAwait(false);
                return;
            }

            // ---- Stage 1: Checking (local cache only — no network, no Apple calls) ----
            SetStage(item, QueueStage.Checking, "Проверка локальных файлов…");

            _catalog.RefreshDownloadedFlags(new[] { item.App });

            // Ownership learned from a previous successful download in an earlier
            // session, so the app picker can show the right badge immediately.
            if (item.App.License is LicenseState.Unknown or LicenseState.CheckFailed
                && _settings.IsKnownOwned(item.App.AppStoreId))
            {
                item.App.License = LicenseState.Owned;
            }
            Notify(item);

            // ---- Stage 2: Downloading (skipped when IPA is already local or mode = install-only) ----
            var skipDownload = _settings.InstallMode == InstallMode.InstallExistingOnly
                || item.App.IsDownloaded && item.App.LocalIpaPath is not null;

            if (!skipDownload)
            {
                if (!await RunDownloadStageAsync(item, ct).ConfigureAwait(false))
                    return;
            }

            // ---- Stage 3: Installing (serialized on the device) ----
            // Skip install when the user only wants to download the IPA.
            if (_settings.InstallMode == InstallMode.DownloadOnly)
            {
                item.CompletedAt = DateTimeOffset.Now;
                SetStage(item, QueueStage.Done, "Загружено (установка пропущена)");
                item.StageProgress = 100;
                Notify(item);
                return;
            }

            await RunInstallStageAsync(item, item.App.LocalIpaPath!, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Stage = QueueStage.Cancelled;
            item.IsConnecting = false;
            item.IsFinalizing = false;
            item.StatusDetail = "Cancelled";
            Notify(item);
            throw;
        }
        catch (Exception ex)
        {
            Fail(item, ex.Message);
        }
    }

    /// <summary>
    /// Runs the download stage. Returns false when the item has been failed and the
    /// pipeline must stop.
    /// </summary>
    private async Task<bool> RunDownloadStageAsync(QueueItem item, CancellationToken ct)
    {
        SetStage(item, QueueStage.Downloading, "Соединение с App Store…");
        item.IsConnecting = true;
        Notify(item);

        await StaggerStartAsync(ct).ConfigureAwait(false);

        var progress = BuildDownloadProgress(item);

        var result = await _download
            .DownloadAsync(item.App, autoPurchase: true, progress, ct: ct)
            .ConfigureAwait(false);

        // The Apple ID does not own the app: ipatool's implicit --purchase was not
        // enough (paid app, or a store-side hiccup). Do the explicit purchase now —
        // this is the ONLY path that pays a second handshake, and only when needed.
        if (!result.Success && DownloadService.IsLicenseError(result.Error))
        {
            SetStage(item, QueueStage.Licensing, "Получение лицензии…");
            var (ok, licenseError) = await _download.PurchaseAsync(item.App.AppStoreId, ct).ConfigureAwait(false);
            if (!ok)
            {
                if (licenseError == DownloadService.SessionExpiredMessage)
                    SessionExpired?.Invoke(this, EventArgs.Empty);
                Fail(item, licenseError ?? "Failed to obtain license");
                return false;
            }

            item.App.License = LicenseState.Owned;
            _settings.MarkOwned(item.App.AppStoreId);

            SetStage(item, QueueStage.Downloading, "Соединение с App Store…");
            item.IsConnecting = true;
            Notify(item);

            result = await _download
                .DownloadAsync(item.App, autoPurchase: true, BuildDownloadProgress(item), ct: ct)
                .ConfigureAwait(false);
        }

        item.IsConnecting = false;

        if (!result.Success || result.IpaPath is null)
        {
            if (result.Error == DownloadService.SessionExpiredMessage)
                SessionExpired?.Invoke(this, EventArgs.Empty);
            Fail(item, result.Error ?? "Download failed");
            return false;
        }

        item.App.IsDownloaded = true;
        item.App.LocalIpaPath = result.IpaPath;

        // A successful download proves the Apple ID owns the app. Persist it so the
        // next session shows the correct badge without any network round-trip.
        item.App.License = LicenseState.Owned;
        _settings.MarkOwned(item.App.AppStoreId);

        return true;
    }

    /// <summary>
    /// Builds the progress sink that turns <see cref="DownloadProgress"/> into the
    /// UI-facing fields on <paramref name="item"/>.
    ///
    /// The status line always says something concrete and moving — during the Apple
    /// handshake it shows a live elapsed counter instead of a static "preparing",
    /// so it is obvious that the download is alive and not stuck.
    /// </summary>
    private IProgress<DownloadProgress> BuildDownloadProgress(QueueItem item) =>
        new Progress<DownloadProgress>(p =>
        {
            item.StageProgress = p.Percent;
            item.DownloadedBytes = p.DownloadedBytes;
            item.TotalBytes = p.TotalBytes;
            item.DownloadSpeedBps = p.Phase == DownloadPhase.Transferring ? p.SpeedBps : 0;
            item.IsFinalizing = p.Finalizing;
            item.IsConnecting = p.Connecting;

            var retrySuffix = p.Attempt > 1 ? $" · попытка {p.Attempt}" : "";

            if (p.Connecting)
            {
                // No bytes yet: authentication / keychain / anisette. Show elapsed time
                // so a slow link reads as "working", not "frozen".
                var seconds = (int)p.Elapsed.TotalSeconds;
                item.StatusDetail = seconds >= 2
                    ? $"Соединение с App Store… {seconds} с{retrySuffix}"
                    : $"Соединение с App Store…{retrySuffix}";
            }
            else if (p.Finalizing)
            {
                // Bytes are in; ipatool is repackaging / injecting the license.
                item.StatusDetail = $"Упаковка и подпись… ({FormatBytes(p.DownloadedBytes)})";
            }
            else if (p.TotalBytes > 0)
            {
                var eta = p.SpeedBps > 0
                    ? FormatEta((long)((p.TotalBytes - p.DownloadedBytes) / p.SpeedBps))
                    : null;
                var speed = p.SpeedBps > 0 ? $" · {FormatBytes((long)p.SpeedBps)}/с" : "";
                item.StatusDetail = string.IsNullOrEmpty(eta)
                    ? $"{p.Percent:0.0}% · {FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}{speed}{retrySuffix}"
                    : $"{p.Percent:0.0}% · {FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}{speed} · {eta}{retrySuffix}";
            }
            else
            {
                // Total unknown: report real bytes rather than a fabricated percentage.
                // This happens when the app has no App Store catalog entry in any
                // storefront (a delisted app), so its size cannot be known until the
                // transfer ends. Say so explicitly — otherwise a missing total plus an
                // animated bar looks like the download is broken. The size is cached
                // once the download completes, so a later run shows a real percentage.
                // Wording matters here: the number shown is bytes downloaded so far, and
                // "размер неизвестен" next to it read as if that number were meaningless.
                // Label it explicitly instead, so the running total and the unknown total
                // can't be confused for each other.
                var speed = p.SpeedBps > 0 ? $" · {FormatBytes((long)p.SpeedBps)}/с" : "";
                item.StatusDetail =
                    $"Скачано {FormatBytes(p.DownloadedBytes)}{speed} · всего неизвестно{retrySuffix}";
            }

            Notify(item);
        });

    /// <summary>
    /// Runs the install stage for a given IPA path, updating <paramref name="item"/>
    /// progress and stage. Shared by the normal pipeline and the direct IPA mode.
    /// </summary>
    private async Task RunInstallStageAsync(QueueItem item, string ipaPath, CancellationToken ct)
    {
        SetStage(item, QueueStage.Installing, "Ожидание устройства…");

        var installProgress = new Progress<InstallProgress>(p =>
        {
            // Map install stages to sub-ranges so the bar is never stuck at 0:
            //   Copying      → 3-9 %
            //   Installing N → 10-90 % (proportional to ideviceinstaller output)
            //   Complete     → 100 %
            var displayPct = p.Status switch
            {
                "Copying"  => Math.Max(3.0, p.Percent),
                "Complete" => 100.0,
                _          => Math.Max(10.0, p.Percent),
            };
            item.StageProgress = displayPct;
            item.StatusDetail = p.Percent > 0
                ? $"{p.Status} {p.Percent:0}%"
                : p.Status;
            Notify(item);
        });

        var installResult = await _install.InstallAsync(
            item.TargetDevice.Udid, ipaPath, installProgress, ct).ConfigureAwait(false);

        if (!installResult.Success)
        {
            Fail(item, HumanizeInstallError(installResult.Error ?? "Installation failed"));
            return;
        }

        item.App.IsInstalledOnDevice = true;
        item.CompletedAt = DateTimeOffset.Now;
        SetStage(item, QueueStage.Done, "Готово");
        item.StageProgress = 100;
        Notify(item);
    }

    /// <summary>
    /// Translates raw ideviceinstaller error strings into human-readable messages
    /// that explain what the user should do.
    /// </summary>
    private static string HumanizeInstallError(string raw)
    {
        var lower = raw.ToLowerInvariant();

        if (lower.Contains("applicationverificationfailed") || lower.Contains("verification failed"))
            return "Ошибка верификации приложения. IPA повреждён или подпись недействительна.";

        if (lower.Contains("installedappdevcertrevoked") || lower.Contains("certrevoked") || lower.Contains("revoked"))
            return "Сертификат подписи отозван. Используйте другой IPA.";

        if (lower.Contains("deviceosdataversionincompatible") || lower.Contains("incompatible"))
            return "IPA несовместим с версией iOS на устройстве.";

        if (lower.Contains("applicationalreadyinstalled"))
            return "Это приложение уже установлено на устройстве.";

        if (lower.Contains("bundleidentifieralreadyinuse") || lower.Contains("bundle id"))
            return "Bundle ID уже занят другим приложением.";

        if (lower.Contains("devicedisconnected") || lower.Contains("connection to the host"))
            return "Устройство отключилось во время установки. Подключите снова и повторите.";

        if (lower.Contains("installdaemon") || lower.Contains("connection refused"))
            return "Служба установки на устройстве не отвечает. Перезагрузите устройство.";

        if (lower.Contains("missingentitlement"))
            return "IPA использует entitlements, требующие платного Apple Developer аккаунта.";

        if (lower.Contains("not purchased") || lower.Contains("9610") || lower.Contains("license"))
            return "Это приложение не было куплено на текущем Apple ID. Попробуйте установить IPA напрямую через режим 'Установить IPA из файла'.";

        if (lower.Contains("authenticate"))
            return "Ошибка аутентификации. Проверьте подключение и разблокируйте устройство.";

        return raw;
    }

    private void SetStage(QueueItem item, QueueStage stage, string detail)
    {
        item.Stage = stage;
        item.StageProgress = 0;
        item.IsFinalizing = false;
        item.IsConnecting = false;
        item.StatusDetail = detail;
        Notify(item);
    }

    private void Fail(QueueItem item, string error)
    {
        item.Stage = QueueStage.Failed;
        item.ErrorMessage = error;
        item.StatusDetail = "Error";
        item.IsConnecting = false;
        item.IsFinalizing = false;
        item.CompletedAt = DateTimeOffset.Now;
        Notify(item);
    }

    private void Notify(QueueItem item) => ItemChanged?.Invoke(this, item);

    private static bool IsTerminal(QueueStage stage)
        => stage is QueueStage.Done or QueueStage.Failed or QueueStage.Cancelled;

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:0.0} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:0.0} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:0.0} KB";
        return $"{bytes} B";
    }

    /// <summary>Formats remaining seconds as human-readable ETA (e.g. "~2 мин 30 с" or "~45 с").</summary>
    private static string FormatEta(long seconds)
    {
        if (seconds <= 0 || seconds > 3600 * 24) return "";
        if (seconds >= 3600) return $"~{seconds / 3600} ч {(seconds % 3600) / 60} мин";
        if (seconds >= 60)   return $"~{seconds / 60} мин {seconds % 60} с";
        return $"~{seconds} с";
    }

    /// <summary>Weight of a single item toward overall progress (0..1).</summary>
    private static double ItemProgressShare(QueueItem item) => item.Stage switch
    {
        QueueStage.Pending => 0,
        QueueStage.Checking => 0.02,
        QueueStage.Licensing => 0.08,
        QueueStage.Downloading => 0.05 + item.StageProgress / 100.0 * 0.65,
        QueueStage.Installing => 0.70 + item.StageProgress / 100.0 * 0.30,
        QueueStage.Done => 1,
        QueueStage.Failed => 1,
        QueueStage.Cancelled => 1,
        _ => 0,
    };
}
