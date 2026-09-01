using System.IO;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Services;

/// <summary>Periodically downloads the newest signed IPSW for subscribed devices.</summary>
public sealed class FirmwareAutoUpdateService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly FirmwareCatalogService _catalog;
    private readonly FirmwareDownloadService _downloads;
    private readonly OperationService _operations;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private System.Threading.Timer? _timer;
    private CancellationTokenSource _cts = new();

    public FirmwareAutoUpdateService(SettingsService settings, FirmwareCatalogService catalog,
        FirmwareDownloadService downloads, OperationService operations)
    {
        _settings = settings;
        _catalog = catalog;
        _downloads = downloads;
        _operations = operations;
        _settings.Changed += (_, _) => Schedule();
    }

    public void Start() => Schedule(runSoon: true);

    private void Schedule(bool runSoon = false)
    {
        var hours = Math.Clamp(_settings.Current.FirmwareCheckIntervalHours, 1, 168);
        _timer?.Dispose();
        _timer = new System.Threading.Timer(async _ => await CheckAsync(), null,
            runSoon ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(hours), TimeSpan.FromHours(hours));
    }

    private async Task CheckAsync()
    {
        if (!await _checkGate.WaitAsync(0)) return;
        try
        {
            var subscriptions = _settings.Current.FirmwareSubscriptions.ToList();
            foreach (var subscription in subscriptions)
            {
                if (_cts.IsCancellationRequested) break;
                try
                {
                    var details = await _catalog.GetDeviceAsync(subscription.Identifier, _cts.Token);
                    var latest = details.Firmwares.Where(f => f.Signed)
                        .OrderByDescending(f => f.ReleaseDate ?? f.UploadDate).FirstOrDefault();
                    if (latest is null || latest.BuildId == subscription.LastBuildId) continue;

                    var device = new FirmwareDevice { Identifier = subscription.Identifier, Name = subscription.DeviceName };
                    var folder = _settings.Current.FirmwareFolder ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "IPA Studio", "Firmwares");
                    // The timer runs on a pool thread; ObservableCollection and bound
                    // operation properties must only be touched by the WPF dispatcher.
                    var op = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        _operations.Start(new Operation(OperationKind.Firmware, Page.Firmware,
                            Loc.Get("L.Firmware.Operation"), $"{device.Name} {latest.Version}")));
                    var reporter = new Progress<FirmwareDownloadProgress>(p =>
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            op.Progress = p.Percent;
                            op.Detail = $"{p.Downloaded / 1024d / 1024d:F0} / {p.Total / 1024d / 1024d:F0} MB";
                        }));
                    try
                    {
                        var path = await _downloads.DownloadAsync(device, latest, folder,
                            Math.Clamp(_settings.Current.FirmwareDownloadThreads, 1, 8), reporter, _cts.Token);
                        var oldPath = subscription.LastFilePath;
                        subscription.LastBuildId = latest.BuildId;
                        subscription.LastFilePath = path;
                        _settings.Save();
                        if (!string.IsNullOrWhiteSpace(oldPath) && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(oldPath)) File.Delete(oldPath);
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            op.Finish(OperationState.Done, Loc.Get("L.Firmware.Done")));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            op.Finish(OperationState.Failed, ex.Message));
                    }
                }
                catch { /* one unavailable model must not stop the remaining subscriptions */ }
            }
        }
        finally { _checkGate.Release(); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _checkGate.Dispose();
    }
}
