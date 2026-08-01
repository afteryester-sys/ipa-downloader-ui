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
    private readonly DeviceService _devices;
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

    [ObservableProperty]
    private string _hintText = "";

    public DeviceLogViewModel(DeviceService devices)
    {
        _devices = devices;

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

            if (_syslog.SawFairPlayFailure && !FairPlayDetected)
            {
                FairPlayDetected = true;
                AppLog.Warn("Device log reported a FairPlay decrypt failure — the device holds no usable licence for the app");
            }
        });
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
