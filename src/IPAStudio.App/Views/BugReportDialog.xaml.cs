using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Views;

/// <summary>
/// Dialog allowing users to compose and submit bug reports either directly to the
/// developer's website backend or via GitHub Issues. Includes sanitized OS, app,
/// connected iOS device model, and recent log excerpt.
/// </summary>
public partial class BugReportDialog : Window
{
    private readonly BugReportService _bugReportService;
    private readonly string _deviceSummary;
    private readonly CancellationTokenSource _cts = new();
    private bool _isSubmitting;
    private bool _isClosed;
    private int _activeOps;

    public string SystemSummaryText { get; }

    public BugReportDialog(
        string? initialSubject = null,
        string? initialDescription = null)
    {
        InitializeComponent();

        _bugReportService = App.Services?.GetService(typeof(BugReportService)) as BugReportService
            ?? new BugReportService();

        var deviceService = App.Services?.GetService(typeof(DeviceService)) as DeviceService;
        _deviceSummary = BugReportService.FormatDeviceSummary(deviceService?.ConnectedDevices);

        var version = BugReportService.GetAppVersion();
        var os = BugReportService.GetOsSummary();
        SystemSummaryText = string.IsNullOrWhiteSpace(_deviceSummary)
            ? $"IPA Studio v{version} • {os} • .NET {Environment.Version}"
            : $"IPA Studio v{version} • {os} • {_deviceSummary}";

        DataContext = this;

        if (!string.IsNullOrWhiteSpace(initialSubject))
        {
            SubjectBox.Text = initialSubject;
        }
        if (!string.IsNullOrWhiteSpace(initialDescription))
        {
            DescriptionBox.Text = initialDescription;
        }

        UpdatePlaceholders();
        LoadRecentLogs();
    }

    private void LoadRecentLogs()
    {
        try
        {
            var logs = AppLog.GetRecentLines(40);
            LogPreviewBox.Text = string.IsNullOrWhiteSpace(logs) ? Loc.Get("L.BugReport.EmptyLog") : logs;
            LogPreviewBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogPreviewBox.Text = ex.Message;
        }
    }

    private void UpdatePlaceholders()
    {
        SubjectPlaceholder.Visibility = string.IsNullOrEmpty(SubjectBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        DescriptionPlaceholder.Visibility = string.IsNullOrEmpty(DescriptionBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIncludeLogsChanged(object sender, RoutedEventArgs e)
    {
        DiagnosticsPanel.Visibility = IncludeLogsCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholders();
        StatusLabel.Text = string.Empty;
    }

    private string BuildFullReport() =>
        _bugReportService.BuildMarkdownReport(
            SubjectBox.Text,
            DescriptionBox.Text,
            IncludeLogsCheck.IsChecked == true,
            _deviceSummary,
            maxLogLines: 50);

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildFullReport());
            StatusLabel.Text = Loc.Get("L.BugReport.Copied");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private async void OnSendSiteClicked(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting) return;
        if (string.IsNullOrWhiteSpace(DescriptionBox.Text) && string.IsNullOrWhiteSpace(SubjectBox.Text))
        {
            StatusLabel.Text = Loc.Get("L.BugReport.NeedDescription");
            return;
        }

        _isSubmitting = true;
        _activeOps++;
        SendSiteButton.IsEnabled = false;
        SendGitHubButton.IsEnabled = false;
        StatusLabel.Text = Loc.Get("L.BugReport.SendingSite");

        try
        {
            try { Clipboard.SetText(BuildFullReport()); } catch { /* best effort backup copy */ }

            var res = await _bugReportService.SubmitToWebsiteAsync(
                SubjectBox.Text,
                DescriptionBox.Text,
                IncludeLogsCheck.IsChecked == true,
                _deviceSummary,
                _cts.Token);

            if (_isClosed) return;

            if (res.Success)
            {
                StatusLabel.Text = string.IsNullOrWhiteSpace(res.ReportId)
                    ? Loc.Get("L.BugReport.SiteSuccess")
                    : Loc.Format("L.BugReport.SiteSuccessWithId", res.ReportId);

                if (App.Services?.GetService(typeof(ViewModels.ShellViewModel)) is ViewModels.ShellViewModel shell)
                {
                    shell.ShowToast(Loc.Get("L.BugReport.MenuTitle"), StatusLabel.Text, "✓");
                }
            }
            else
            {
                StatusLabel.Text = Loc.Format("L.BugReport.SiteUnavailable", res.ErrorMessage ?? "HTTP error");
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!_isClosed) StatusLabel.Text = Loc.Format("L.BugReport.SiteUnavailable", ex.Message);
        }
        finally
        {
            _isSubmitting = false;
            if (!_isClosed)
            {
                SendSiteButton.IsEnabled = true;
                SendGitHubButton.IsEnabled = true;
            }
            if (_activeOps > 0) _activeOps--;
            if (_isClosed && _activeOps <= 0)
            {
                try { _cts.Dispose(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private void OnSendGitHubClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            try { Clipboard.SetText(BuildFullReport()); } catch { /* best effort */ }

            var issueUrl = _bugReportService.BuildGitHubIssueUrl(
                SubjectBox.Text,
                DescriptionBox.Text,
                IncludeLogsCheck.IsChecked == true,
                _deviceSummary);

            Process.Start(new ProcessStartInfo
            {
                FileName = issueUrl,
                UseShellExecute = true
            });

            StatusLabel.Text = Loc.Get("L.BugReport.OpenedBrowser");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            AppLog.Warn($"BugReport: failed to launch browser: {ex.Message}");
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (_activeOps <= 0)
        {
            try { _cts.Dispose(); } catch (ObjectDisposedException) { }
        }
        base.OnClosed(e);
    }
}
