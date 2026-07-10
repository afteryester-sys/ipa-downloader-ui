using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Services;
using IPAStudio.Core.Tools;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Settings: UI language, ipatool version, apps folder, parallel downloads and sign out.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IPageAware
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly QueueService _queue;
    private readonly ToolLocator _tools;
    private readonly LocalizationManager _localization;
    private INavigator? _navigator;

    [ObservableProperty]
    private string _language = "ru";

    [ObservableProperty]
    private int _ipatoolVersion = 2;

    [ObservableProperty]
    private string _appsFolder = "";

    [ObservableProperty]
    private int _maxParallelDownloads = 3;

    [ObservableProperty]
    private string _accountEmail = "";

    [ObservableProperty]
    private string _toolsFolder = "";

    public SettingsViewModel(
        SettingsService settings, AuthService auth, QueueService queue,
        ToolLocator tools, LocalizationManager localization)
    {
        _settings = settings;
        _auth = auth;
        _queue = queue;
        _tools = tools;
        _localization = localization;
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        Language = _settings.Current.Language;
        IpatoolVersion = _settings.Current.IpatoolVersion;
        AppsFolder = _settings.Current.AppsFolder ?? _tools.AppsFolder;
        MaxParallelDownloads = _settings.Current.MaxParallelDownloads;
        AccountEmail = _auth.CurrentAccount?.Email ?? "";
        ToolsFolder = _tools.ToolsRoot;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.Current.Language = Language;
        _settings.Current.IpatoolVersion = IpatoolVersion;
        _settings.Current.AppsFolder = string.IsNullOrWhiteSpace(AppsFolder) ? null : AppsFolder;
        _settings.Current.MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 5);
        _settings.Save();

        _queue.MaxParallelDownloads = _settings.Current.MaxParallelDownloads;
        _localization.Apply(Language);

        _navigator?.GoTo(Page.Devices);
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _auth.LogoutAsync();
        _navigator?.GoTo(Page.Login);
    }

    [RelayCommand]
    private void GoBack() => _navigator?.GoTo(Page.Devices);
}
