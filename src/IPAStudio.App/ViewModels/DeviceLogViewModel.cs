using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Live log straight off the iPhone.
///
/// The application log can only say that an install returned success; when the app then
/// fails to launch, the reason is stated on the device and nowhere else. This view exposes
/// that log so a failure can be read rather than guessed at, and saved to a file to be
/// sent on.
/// </summary>
public sealed partial class DeviceLogViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Recovery attempts past which the store is clearly refusing rather than working on it.
    /// Three, because a genuine grant arrives on the first or second try, and each refused
    /// attempt in the reported log followed the previous one within seconds.
    /// </summary>
    private const int RecoveryAttemptsMeaningRefused = 3;

    private readonly DeviceService _devices;
    private readonly AuthService _auth;
    private readonly DeviceSyslogService _syslog = new();

    /// <summary>
    /// Coalesces UI refreshes onto a fixed cadence. A busy device emits hundreds of lines a
    /// second and each refresh rebuilds up to 4000 rows, so refreshing per batch of arrivals
    /// left the dispatcher rebuilding the list continuously and the window stopped responding.
    /// Queueing work per arrival cannot fix that on its own: the arrivals never stop, so the
    /// queue is never empty. A timer bounds the work instead, at four rebuilds a second.
    /// </summary>
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Lines arrived since the last rebuild.</summary>
    private bool _dirty;

    public ObservableCollection<Device> Devices { get; } = new();
    public ObservableCollection<SyslogLine> Lines { get; } = new();

    [ObservableProperty]
    private Device? _selectedDevice;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>
    /// When on (the default), only install and launch related lines are kept. Off shows
    /// the raw firehose, which is occasionally necessary and usually unreadable.
    /// </summary>
    [ObservableProperty]
    private bool _installAndLaunchOnly = true;

    /// <summary>
    /// Set once the device has reported a FairPlay decrypt failure. Drives the banner
    /// that explains the white-screen-then-quit symptom, since that message is the whole
    /// point of opening this window.
    /// </summary>
    [ObservableProperty]
    private bool _fairPlayDetected;

    /// <summary>
    /// Advice shown in that banner. Held as text rather than bound to a fixed string, because
    /// what is worth saying depends on which accounts turn out to be involved.
    /// </summary>
    [ObservableProperty]
    private string _fairPlayDetail = "";

    [ObservableProperty]
    private string _hintText = "";

    public DeviceLogViewModel(DeviceService devices, AuthService auth)
    {
        _devices = devices;
        _auth = auth;

        foreach (var d in _devices.ConnectedDevices) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();

        _devices.DeviceConnected += OnDeviceConnected;
        _devices.DeviceDisconnected += OnDeviceDisconnected;

        _syslog.LinesAdded += OnLinesAdded;
        _syslog.StatusChanged += OnStatusChanged;

        // Runs for the window's lifetime: a tick with nothing new costs a single flag check,
        // and starting it per capture would need the same stopping logic on every exit path.
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();

        StatusText = Devices.Count == 0
            ? Loc.Get("L.DeviceLogs.NoDevice")
            : Loc.Get("L.DeviceLogs.Idle");
    }

    partial void OnInstallAndLaunchOnlyChanged(bool value)
    {
        // Applies to lines that arrive from now on. Re-filtering what is already collected
        // is not possible: filtered-out lines were never stored, so switching the filter
        // off cannot retroactively recover them.
        _syslog.Filter = value ? SyslogFilter.InstallAndLaunch : SyslogFilter.Everything;
        HintText = Loc.Get("L.DeviceLogs.FilterChanged");
    }

    [RelayCommand]
    private void Start()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            StatusText = Loc.Get("L.DeviceLogs.NoDevice");
            return;
        }

        _syslog.Filter = InstallAndLaunchOnly ? SyslogFilter.InstallAndLaunch : SyslogFilter.Everything;
        _syslog.Start(device.Udid);
        AppLog.Info($"Device log capture started for {device.Name}");
        HintText = Loc.Get("L.DeviceLogs.Hint.Reproduce");
    }

    [RelayCommand]
    private void Stop()
    {
        _syslog.Stop();
        IsStreaming = false;
        StatusText = Loc.Get("L.DeviceLogs.Stopped");
        AppLog.Info("Device log capture stopped");
    }

    [RelayCommand]
    private void ClearLines()
    {
        _syslog.Clear();
        Lines.Clear();
        FairPlayDetected = false;
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            var text = _syslog.SnapshotText();
            Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text);
            Flash(Loc.Get("L.Logs.Copied"));
        }
        catch
        {
            Flash(Loc.Get("L.Common.CopyFailed"));
        }
    }

    /// <summary>
    /// Writes the capture next to the application logs. Saving to a file rather than only
    /// offering "copy" matters here because these captures run to thousands of lines,
    /// which is past the point where pasting into a chat window is practical.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(AppLog.FilePath) ?? Path.GetTempPath();
            var name = $"device-syslog-{DateTime.Now:yyyy-MM-dd-HHmmss}.log";
            var path = Path.Combine(dir, name);

            var header =
                $"Device: {SelectedDevice?.Name} ({SelectedDevice?.Model}) iOS {SelectedDevice?.OsVersion}" +
                Environment.NewLine +
                $"Filter: {(InstallAndLaunchOnly ? "install & launch" : "everything")}" +
                Environment.NewLine +
                new string('-', 60) + Environment.NewLine;

            File.WriteAllText(path, header + _syslog.SnapshotText());
            AppLog.Info($"Device log saved to {path}");

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch { /* revealing the file is a convenience, not the point */ }

            Flash(Loc.Get("L.DeviceLogs.Saved"));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not save device log: {ex.Message}");
            Flash(Loc.Get("L.DeviceLogs.SaveFailed"));
        }
    }

    // ---- plumbing ----------------------------------------------------------------

    private void OnStatusChanged(bool running, string status)
    {
        Dispatch(() =>
        {
            IsStreaming = running;
            StatusText = running ? Loc.Get("L.DeviceLogs.Streaming") : status;
        });
    }

    private void OnLinesAdded() => _dirty = true;

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        if (!_dirty) return;
        _dirty = false;

        Dispatch(() =>
        {
            var snapshot = _syslog.Snapshot();

            // Rebuilt wholesale rather than appending deltas: the service keeps a bounded
            // ring buffer, so old lines fall off the front and an append-only view would
            // drift out of step with it.
            Lines.Clear();
            foreach (var line in snapshot) Lines.Add(line);

            // Recovery on its own is enough to raise this: it only happens because a launch
            // was refused. A rejection logged during the install counts too, and arrives
            // before the app has even been tapped.
            if (_syslog.SawFairPlayFailure || _syslog.SawLicenceRecovery || _syslog.SawSinfRejectedAtInstall)
            {
                var first = !FairPlayDetected;
                FairPlayDetected = true;

                // Recomputed on every refresh rather than once: the advice depends on how
                // many times recovery has been tried, and that only becomes clear with time.
                FairPlayDetail = BuildFairPlayDetail();

                if (first)
                    AppLog.Warn(
                        "Device log reported a FairPlay licence rejection — the device holds no usable " +
                        $"licence for the app (device account: {_syslog.DeviceAccount ?? "not stated"}, " +
                        $"downloaded as: {_auth.CurrentAccount?.Email ?? "not signed in"}, " +
                        $"rejected during install: {(_syslog.SawSinfRejectedAtInstall ? "yes" : "no")}, " +
                        $"recovery attempts: {_syslog.LicenceRecoveryAttempts})");
            }
        });
    }

    /// <summary>
    /// What to advise about the rejected licence. The generic advice is to sign in with the
    /// account the app was downloaded under, which is useless to someone who believes they
    /// already have - so when both accounts are known they are named instead, and the two
    /// cases get different instructions because only one of them is fixable on the phone.
    /// </summary>
    private string BuildFairPlayDetail()
    {
        var onDevice = _syslog.DeviceAccount;
        var downloadedAs = _auth.CurrentAccount?.Email;

        var accountsDiffer = !string.IsNullOrEmpty(onDevice)
                          && !string.IsNullOrEmpty(downloadedAs)
                          && !string.Equals(onDevice, downloadedAs, StringComparison.OrdinalIgnoreCase);

        // A mismatch outranks everything else: recovery cannot succeed for an account the
        // licence does not belong to, so telling someone to wait would be a lie.
        if (accountsDiffer)
            return string.Format(Loc.Get("L.DeviceLogs.FairPlay.OtherAccount"), downloadedAs, onDevice);

        // Recovery asks the store for a licence for the account the phone is signed in to, so
        // a run of attempts means the store keeps saying no and waiting will not change that.
        // Named separately from a single attempt, which is still worth waiting for.
        if (_syslog.LicenceRecoveryAttempts >= RecoveryAttemptsMeaningRefused)
            return string.IsNullOrEmpty(onDevice)
                ? Loc.Get("L.DeviceLogs.FairPlay.RecoveryRefused")
                : string.Format(Loc.Get("L.DeviceLogs.FairPlay.RecoveryRefusedFor"), onDevice);

        // The phone is already fetching the licence, so the App Store detour is unnecessary.
        if (_syslog.SawLicenceRecovery)
            return Loc.Get("L.DeviceLogs.FairPlay.Recovering");

        if (string.IsNullOrEmpty(onDevice) || string.IsNullOrEmpty(downloadedAs))
            return Loc.Get("L.DeviceLogs.FairPlay.Detail");

        return string.Format(Loc.Get("L.DeviceLogs.FairPlay.SameAccount"), onDevice);
    }

    private void OnDeviceConnected(object? sender, Device device)
    {
        Dispatch(() =>
        {
            if (Devices.All(d => d.Udid != device.Udid)) Devices.Add(device);
            SelectedDevice ??= device;
            if (!IsStreaming) StatusText = Loc.Get("L.DeviceLogs.Idle");
        });
    }

    private void OnDeviceDisconnected(object? sender, Device device)
    {
        Dispatch(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.Udid == device.Udid);
            if (existing is not null) Devices.Remove(existing);
            if (SelectedDevice?.Udid == device.Udid) SelectedDevice = Devices.FirstOrDefault();
        });
    }

    private static void Dispatch(Action action)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(action);
    }

    private async void Flash(string message)
    {
        HintText = message;
        try { await Task.Delay(2500); } catch { }
        if (HintText == message) HintText = "";
    }

    public void Dispose()
    {
        _devices.DeviceConnected -= OnDeviceConnected;
        _devices.DeviceDisconnected -= OnDeviceDisconnected;
        _syslog.LinesAdded -= OnLinesAdded;
        _syslog.StatusChanged -= OnStatusChanged;
        _syslog.Dispose();
    }
}
