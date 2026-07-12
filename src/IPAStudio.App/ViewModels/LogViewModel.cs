using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Diagnostics;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Backing view-model for the detailed log window. Mirrors <see cref="AppLog"/> and
/// lets the user copy / open / clear the log so errors can be shared for support.
/// </summary>
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _statusText = "";

    public string LogFilePath => AppLog.FilePath;

    public LogViewModel()
    {
        Refresh();
        AppLog.Changed += OnLogChanged;
    }

    private void OnLogChanged()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() => Text = AppLog.Snapshot()));
    }

    [RelayCommand]
    private void Refresh() => Text = AppLog.Snapshot();

    [RelayCommand]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(string.IsNullOrEmpty(Text) ? " " : Text);
            Flash("Скопировано в буфер обмена");
        }
        catch
        {
            Flash("Не удалось скопировать");
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppLog.FilePath) { UseShellExecute = true });
        }
        catch
        {
            // Fall back to revealing the containing folder.
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{AppLog.FilePath}\"") { UseShellExecute = true });
            }
            catch { /* best effort */ }
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        AppLog.Clear();
        Refresh();
    }

    private async void Flash(string message)
    {
        StatusText = message;
        try { await Task.Delay(2000); } catch { }
        if (StatusText == message) StatusText = "";
    }

    public void Dispose() => AppLog.Changed -= OnLogChanged;
}
