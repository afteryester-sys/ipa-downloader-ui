using System.IO;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Services;

public sealed record FirmwareAutoUpdateStatus(
    string Identifier,
    bool IsChecking,
    DateTimeOffset? LastCheckUtc,
    DateTimeOffset? NextCheckUtc,
    string? LatestVersion,
    string? Error);

/// <summary>Periodically downloads the newest signed IPSW for explicitly enabled devices.</summary>
public sealed class FirmwareAutoUpdateService : IDisposable
{
    private const string AutoUpdateFailureKey = "L.Firmware.AutoUpdateFailed";
    private readonly SettingsService _settings;
    private readonly FirmwareCatalogService _catalog;
    private readonly FirmwareDownloadService _downloads;
    private readonly OperationService _operations;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _scheduleLock = new();
    private System.Threading.Timer? _timer;
    private readonly CancellationTokenSource _cts = new();
    private string _scheduleFingerprint = "";
    private bool _started;

    public event EventHandler<FirmwareAutoUpdateStatus>? StatusChanged;

    public FirmwareAutoUpdateService(SettingsService settings, FirmwareCatalogService catalog,
        FirmwareDownloadService downloads, OperationService operations)
    {
        _settings = settings;
        _catalog = catalog;
        _downloads = downloads;
        _operations = operations;
        _settings.Changed += OnSettingsChanged;
    }

    public void Start()
    {
        _started = true;
        Schedule(runSoon: true, force: true);
    }

    public void Reschedule(bool runSoon = false)
    {
        if (_started) Schedule(runSoon, force: true);
    }

    public async Task CheckNowAsync(string? identifier = null)
    {
        await CheckAsync(identifier, waitForTurn: true).ConfigureAwait(false);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_started) Schedule(runSoon: false, force: false);
    }

    private void Schedule(bool runSoon, bool force)
    {
        lock (_scheduleLock)
        {
            var enabled = _settings.Current.FirmwareSubscriptions
                .Where(s => s.AutoUpdateEnabled)
                .OrderBy(s => s.Identifier, StringComparer.Ordinal)
                .ToList();
            var hours = Math.Clamp(_settings.Current.FirmwareCheckIntervalHours, 1, 168);
            var fingerprint = $"{hours}|{string.Join('|', enabled.Select(s => s.Identifier))}";
            if (!force && fingerprint == _scheduleFingerprint) return;

            _scheduleFingerprint = fingerprint;
            _timer?.Dispose();
            _timer = null;

            foreach (var disabled in _settings.Current.FirmwareSubscriptions.Where(s => !s.AutoUpdateEnabled))
                disabled.NextCheckUtc = null;

            if (enabled.Count == 0)
            {
                _settings.Save();
                PublishAllStatuses();
                return;
            }

            var due = runSoon ? TimeSpan.FromSeconds(10) : TimeSpan.FromHours(hours);
            var next = DateTimeOffset.UtcNow.Add(due);
            foreach (var subscription in enabled) subscription.NextCheckUtc = next;
            _settings.Save();
            PublishAllStatuses();

            _timer = new System.Threading.Timer(
                _ => _ = RunScheduledCheckAsync(),
                null,
                due,
                TimeSpan.FromHours(hours));
        }
    }

    private async Task RunScheduledCheckAsync()
    {
        try
        {
            if (await CheckAsync(waitForTurn: false).ConfigureAwait(false)
                && !_cts.IsCancellationRequested)
                UpdateNextCheckTimes();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Warn($"Firmware auto-update worker failed: {ex.Message}");
        }
    }

    private async Task<bool> CheckAsync(string? identifier = null, bool waitForTurn = false)
    {
        bool entered;
        try
        {
            if (waitForTurn)
            {
                await _checkGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                entered = true;
            }
            else
            {
                entered = await _checkGate.WaitAsync(0, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return false;
        }

        if (!entered) return false;
        try
        {
            var subscriptions = _settings.Current.FirmwareSubscriptions
                .Where(s => s.AutoUpdateEnabled
                    && (identifier is null || string.Equals(s.Identifier, identifier, StringComparison.Ordinal)))
                .ToList();

            foreach (var subscription in subscriptions)
            {
                if (_cts.IsCancellationRequested) break;
                RaiseStatus(subscription, isChecking: true);

                try
                {
                    var details = await _catalog.GetDeviceAsync(subscription.Identifier, _cts.Token)
                        .ConfigureAwait(false);
                    var latest = details.Firmwares
                        .Where(f => f.Signed)
                        .OrderByDescending(f => f.ReleaseDate ?? f.UploadDate)
                        .FirstOrDefault();

                    subscription.LastCheckUtc = DateTimeOffset.UtcNow;
                    subscription.LastFoundVersion = latest?.Version;
                    subscription.LastError = null;
                    _settings.Save();

                    // The user may have switched this subscription off while the catalog request
                    // was running. Do not begin a new transfer after that point.
                    if (!subscription.AutoUpdateEnabled
                        || latest is null
                        || latest.BuildId == subscription.LastBuildId)
                        continue;

                    var device = new FirmwareDevice
                    {
                        Identifier = subscription.Identifier,
                        Name = subscription.DeviceName,
                    };
                    var folder = _settings.Current.FirmwareFolder ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Downloads", "IPA Studio", "Firmwares");

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher is null) throw new InvalidOperationException("The application window is unavailable.");

                    var operation = await dispatcher.InvokeAsync(() =>
                        _operations.Start(new Operation(
                            OperationKind.Firmware,
                            Page.Firmware,
                            Loc.Get("L.Firmware.Operation"),
                            $"{device.Name} {latest.Version}")));

                    var reporter = new Progress<FirmwareDownloadProgress>(p =>
                        dispatcher.BeginInvoke(() =>
                        {
                            operation.Progress = p.Percent;
                            operation.Detail = $"{p.Downloaded / 1024d / 1024d:F0} / {p.Total / 1024d / 1024d:F0} MB";
                        }));

                    try
                    {
                        var path = await _downloads.DownloadAsync(
                            device,
                            latest,
                            folder,
                            Math.Clamp(_settings.Current.FirmwareDownloadThreads, 1, 8),
                            reporter,
                            _cts.Token).ConfigureAwait(false);

                        var oldPath = subscription.LastFilePath;
                        subscription.LastBuildId = latest.BuildId;
                        subscription.LastFilePath = path;
                        subscription.LastDownloadUtc = DateTimeOffset.UtcNow;
                        subscription.LastError = null;
                        _settings.Save();

                        if (!string.IsNullOrWhiteSpace(oldPath)
                            && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(oldPath))
                        {
                            try { File.Delete(oldPath); }
                            catch (Exception ex) { AppLog.Warn($"Firmware cleanup failed: {ex.Message}"); }
                        }

                        await dispatcher.InvokeAsync(() =>
                            operation.Finish(OperationState.Done, Loc.Get("L.Firmware.Done")));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        subscription.LastError = AutoUpdateFailureKey;
                        _settings.Save();
                        AppLog.Warn($"Firmware auto-update download failed for {subscription.Identifier}: {ex.Message}");
                        await dispatcher.InvokeAsync(() =>
                            operation.Finish(OperationState.Failed, Loc.Get(AutoUpdateFailureKey)));
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    subscription.LastCheckUtc = DateTimeOffset.UtcNow;
                    subscription.LastError = AutoUpdateFailureKey;
                    _settings.Save();
                    AppLog.Warn($"Firmware auto-update check failed for {subscription.Identifier}: {ex.Message}");
                }
                finally
                {
                    RaiseStatus(subscription, isChecking: false);
                }
            }

            return true;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private void UpdateNextCheckTimes()
    {
        var next = DateTimeOffset.UtcNow.AddHours(
            Math.Clamp(_settings.Current.FirmwareCheckIntervalHours, 1, 168));
        foreach (var subscription in _settings.Current.FirmwareSubscriptions)
            subscription.NextCheckUtc = subscription.AutoUpdateEnabled ? next : null;
        _settings.Save();
        PublishAllStatuses();
    }

    private void PublishAllStatuses()
    {
        foreach (var subscription in _settings.Current.FirmwareSubscriptions)
            RaiseStatus(subscription, isChecking: false);
    }

    private void RaiseStatus(FirmwareSubscription subscription, bool isChecking) =>
        StatusChanged?.Invoke(this, new FirmwareAutoUpdateStatus(
            subscription.Identifier,
            isChecking,
            subscription.LastCheckUtc,
            subscription.NextCheckUtc,
            subscription.LastFoundVersion,
            subscription.LastError));

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        lock (_scheduleLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        _cts.Cancel();
        // A timer callback may still be unwinding after cancellation. The process is exiting,
        // so leave these tiny primitives alive rather than disposing them under that callback.
    }
}
