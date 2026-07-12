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
    private readonly List<QueueItem> _items = new();
    private CancellationTokenSource? _cts;

    public QueueService(DownloadService download, InstallService install, CatalogService catalog)
    {
        _download = download;
        _install = install;
        _catalog = catalog;
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
            // ---- Stage 1: Checking (local cache + account license) ----
            SetStage(item, QueueStage.Checking, "Checking local files and license");

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
                SetStage(item, QueueStage.Licensing, "Obtaining license for Apple ID");
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

            // ---- Stage 3: Downloading (skipped when IPA is already local) ----
            if (!item.App.IsDownloaded || item.App.LocalIpaPath is null)
            {
                SetStage(item, QueueStage.Downloading, "Downloading IPA");

                var progress = new Progress<DownloadProgress>(p =>
                {
                    item.StageProgress = p.Percent;
                    item.DownloadedBytes = p.DownloadedBytes;
                    item.TotalBytes = p.TotalBytes;
                    item.DownloadSpeedBps = p.SpeedBps;
                    item.StatusDetail = $"Downloading {p.Percent:0}%";
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
            SetStage(item, QueueStage.Installing, "Waiting for device…");

            var installProgress = new Progress<InstallProgress>(p =>
            {
                item.StageProgress = p.Percent;
                item.StatusDetail = $"{p.Status} {p.Percent:0}%";
                Notify(item);
            });

            var installResult = await _install.InstallAsync(
                item.TargetDevice.Udid, item.App.LocalIpaPath!, installProgress, ct).ConfigureAwait(false);

            if (!installResult.Success)
            {
                Fail(item, installResult.Error ?? "Installation failed");
                return;
            }

            item.App.IsInstalledOnDevice = true;
            item.CompletedAt = DateTimeOffset.Now;
            SetStage(item, QueueStage.Done, "Installed");
            item.StageProgress = 100;
            Notify(item);
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
