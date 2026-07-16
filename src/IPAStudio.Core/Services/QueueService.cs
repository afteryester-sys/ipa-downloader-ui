using System.IO;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Orchestrates the multi-app install pipeline:
///   Pending -> Checking (local IPA + license) -> [Licensing] -> [Downloading] -> Installing -> Done/Failed
///
/// Downloads run in parallel (up to <see cref="MaxParallelDownloads"/>); installs onto
/// the device are serialized by <see cref="InstallService"/>. Every state change raises
/// <see cref="ItemChanged"/> so the UI can animate stage transitions and progress.
/// </summary>
public sealed class QueueService
{
    public int MaxParallelDownloads { get; set; } = 3;

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

        try
        {
            List<QueueItem> pending;
            lock (_items) pending = _items.Where(i => i.Stage == QueueStage.Pending).ToList();

            // Downloads (+ checks + licensing) run in parallel; the install step inside
            // ProcessItemAsync is serialized by InstallService's device lock.
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = ct },
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

            // ---- Stage 1: Checking (local cache + account license) ----
            SetStage(item, QueueStage.Checking, "Проверка лицензии…");

            _catalog.RefreshDownloadedFlags(new[] { item.App });

            if (item.App.License == LicenseState.Unknown
                || item.App.License == LicenseState.CheckFailed
                || item.App.License == LicenseState.SessionExpired)
            {
                item.App.License = await _download.CheckLicenseAsync(item.App.AppStoreId, ct).ConfigureAwait(false);
                Notify(item);
            }

            // Session expired -> inform the UI so it redirects to the login screen.
            if (item.App.License == LicenseState.SessionExpired)
            {
                Fail(item, DownloadService.SessionExpiredMessage);
                SessionExpired?.Invoke(this, EventArgs.Empty);
                return;
            }

            // ---- Stage 2: Licensing (obtain license when the account doesn't own it) ----
            if (item.App.License == LicenseState.NotOwned)
            {
                SetStage(item, QueueStage.Licensing, "Получение лицензии…");
                var (ok, error) = await _download.PurchaseAsync(item.App.AppStoreId, ct).ConfigureAwait(false);
                if (!ok)
                {
                    if (error == DownloadService.SessionExpiredMessage)
                        SessionExpired?.Invoke(this, EventArgs.Empty);
                    Fail(item, error ?? "Failed to obtain license");
                    return;
                }
                item.App.License = LicenseState.Owned;
                Notify(item);
            }

            // ---- Stage 3: Downloading (skipped when IPA is already local or mode = install-only) ----
            var skipDownload = _settings.InstallMode == InstallMode.InstallExistingOnly
                || item.App.IsDownloaded && item.App.LocalIpaPath is not null;

            if (!skipDownload)
            {
                SetStage(item, QueueStage.Downloading, "Preparing download…");

                var progress = new Progress<DownloadProgress>(p =>
                {
                    item.StageProgress = p.Percent;
                    item.DownloadedBytes = p.DownloadedBytes;
                    item.TotalBytes = p.TotalBytes;
                    item.DownloadSpeedBps = p.SpeedBps;
                    // Build a rich status line that always shows something meaningful.
                    if (p.TotalBytes > 0 && p.Percent > 0.1)
                    {
                        var eta = p.SpeedBps > 0
                            ? FormatEta((long)((p.TotalBytes - p.DownloadedBytes) / p.SpeedBps))
                            : null;
                        item.StatusDetail = eta is not null
                            ? $"{p.Percent:0.0}% · {FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)} · {eta}"
                            : $"{p.Percent:0.0}% · {FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}";
                    }
                    else if (p.DownloadedBytes > 0)
                    {
                        item.StatusDetail = $"Загрузка {FormatBytes(p.DownloadedBytes)}…";
                    }
                    else
                    {
                        item.StatusDetail = "Подготовка загрузки…";
                    }
                    Notify(item);
                });

                var result = await _download.DownloadAsync(item.App, autoPurchase: true, progress, ct).ConfigureAwait(false);
                if (!result.Success || result.IpaPath is null)
                {
                    if (result.Error == DownloadService.SessionExpiredMessage)
                        SessionExpired?.Invoke(this, EventArgs.Empty);
                    Fail(item, result.Error ?? "Download failed");
                    return;
                }

                item.App.IsDownloaded = true;
                item.App.LocalIpaPath = result.IpaPath;
            }

            // ---- Stage 4: Installing (serialized on the device) ----
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
        item.StatusDetail = detail;
        Notify(item);
    }

    private void Fail(QueueItem item, string error)
    {
        item.Stage = QueueStage.Failed;
        item.ErrorMessage = error;
        item.StatusDetail = "Error";
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
        QueueStage.Checking => 0.05,
        QueueStage.Licensing => 0.10,
        QueueStage.Downloading => 0.10 + item.StageProgress / 100.0 * 0.60,
        QueueStage.Installing => 0.70 + item.StageProgress / 100.0 * 0.30,
        QueueStage.Done => 1,
        QueueStage.Failed => 1,
        QueueStage.Cancelled => 1,
        _ => 0,
    };
}
