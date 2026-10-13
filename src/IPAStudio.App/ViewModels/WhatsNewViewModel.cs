using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Controls the "What's New" modal dialog shown on first launch after each application update,
/// and available manually from Settings and Menu.
/// </summary>
public sealed partial class WhatsNewViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ReleaseNotesService _releaseNotes;
    private readonly LocalizationManager _localization;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _runningVersion = "1.8.48";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private ReleaseNote? _currentNote;

    [ObservableProperty]
    private bool _isHistoryOpen;

    public ObservableCollection<ReleaseNote> HistoryNotes { get; } = new();

    public WhatsNewViewModel(SettingsService settings, ReleaseNotesService releaseNotes, LocalizationManager localization)
    {
        _settings = settings;
        _releaseNotes = releaseNotes;
        _localization = localization;

        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        _runningVersion = v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.8.48";

        RefreshContent();
        _localization.LanguageChanged += _ => RefreshContent();
    }

    /// <summary>
    /// Refreshes the localized release notes content according to current UI language.
    /// </summary>
    public void RefreshContent()
    {
        var lang = _localization.CurrentLanguage ?? _settings.Current.Language ?? "ru";
        CurrentNote = _releaseNotes.GetReleaseNote(RunningVersion, lang);

        HistoryNotes.Clear();
        foreach (var note in _releaseNotes.GetAllReleaseNotes(lang))
        {
            HistoryNotes.Add(note);
        }
    }

    /// <summary>
    /// Checks on startup whether this version has been seen yet. If not, opens the modal once.
    /// </summary>
    public void CheckOnStartup()
    {
        var lastSeen = _settings.Current.LastSeenReleaseNotesVersion?.Trim();
        if (string.IsNullOrEmpty(lastSeen) || !string.Equals(lastSeen, RunningVersion, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info($"First launch for version {RunningVersion} (last seen: '{lastSeen}'). Opening What's New modal.");
            Open();
        }
    }

    [RelayCommand]
    public void Open()
    {
        RefreshContent();
        IsHistoryOpen = false;
        IsOpen = true;
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        IsHistoryOpen = false;

        // Remember that the user has acknowledged this version
        if (!string.Equals(_settings.Current.LastSeenReleaseNotesVersion, RunningVersion, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Current.LastSeenReleaseNotesVersion = RunningVersion;
            try
            {
                _settings.Save();
                AppLog.Info($"Saved LastSeenReleaseNotesVersion = {RunningVersion}");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save LastSeenReleaseNotesVersion: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    public void ToggleHistory()
    {
        IsHistoryOpen = !IsHistoryOpen;
    }

    [RelayCommand]
    public void OpenReleasesUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/afteryester-sys/ipa-downloader-ui/releases")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not open releases URL: {ex.Message}");
        }
    }
}
