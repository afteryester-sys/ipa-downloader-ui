using CommunityToolkit.Mvvm.ComponentModel;
using IPAStudio.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IPAStudio.App.ViewModels;

public enum Page
{
    Setup,
    Login,
    Devices,
    AppPicker,
    Queue,
    Settings,
    DeviceInfo,
    Photos,
}

/// <summary>Simple page-based navigation used by all viewmodels.</summary>
public interface INavigator
{
    void GoTo(Page page);
    void GoToAppPicker(Device device);

    /// <summary>Opens the login screen for a device chosen before signing in.</summary>
    void GoToLoginForDevice(Device device);

    /// <summary>Opens the detailed information screen for a device.</summary>
    void GoToDeviceInfo(Device device);

    /// <summary>Opens the photo transfer screen for a device.</summary>
    void GoToPhotos(Device device);
}

/// <summary>
/// Root viewmodel: owns the current page and wires child viewmodels together.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, INavigator
{
    [ObservableProperty]
    private ObservableObject _currentViewModel = null!;

    [ObservableProperty]
    private Page _currentPage = Page.Setup;

    private Page _previousPage = Page.Setup;

    /// <summary>Device passed to a page that needs a target (login/info/photos).</summary>
    private Device? _pendingDevice;

    /// <summary>Global updater backing the corner update flyout (available everywhere).</summary>
    public UpdaterViewModel Updater { get; }

    public ShellViewModel(UpdaterViewModel updater)
    {
        Updater = updater;
        GoTo(Page.Setup);
    }

    public void GoTo(Page page)
    {
        _previousPage = CurrentPage;
        CurrentPage = page;
        CurrentViewModel = page switch
        {
            Page.Setup => Resolve<SetupViewModel>(),
            Page.Login => Resolve<LoginViewModel>(),
            Page.Devices => Resolve<DevicesViewModel>(),
            Page.AppPicker => Resolve<AppPickerViewModel>(),
            Page.Queue => Resolve<QueueViewModel>(),
            Page.Settings => Resolve<SettingsViewModel>(),
            Page.DeviceInfo => Resolve<DeviceInfoViewModel>(),
            Page.Photos => Resolve<PhotosViewModel>(),
            _ => CurrentViewModel,
        };

        // Hand the pending device to pages that need one, before OnNavigatedTo runs.
        switch (CurrentViewModel)
        {
            case LoginViewModel login: login.SetPendingDevice(_pendingDevice); break;
            case DeviceInfoViewModel info when _pendingDevice is not null: info.SetDevice(_pendingDevice); break;
            case PhotosViewModel photos when _pendingDevice is not null: photos.SetDevice(_pendingDevice); break;
        }

        if (CurrentViewModel is IPageAware aware)
            aware.OnNavigatedTo(this);

        _pendingDevice = null;
    }

    public void GoToAppPicker(Device device)
    {
        var picker = Resolve<AppPickerViewModel>();
        picker.TargetDevice = device;
        GoTo(Page.AppPicker);
    }

    public void GoToLoginForDevice(Device device)
    {
        _pendingDevice = device;
        GoTo(Page.Login);
    }

    public void GoToDeviceInfo(Device device)
    {
        _pendingDevice = device;
        GoTo(Page.DeviceInfo);
    }

    public void GoToPhotos(Device device)
    {
        _pendingDevice = device;
        GoTo(Page.Photos);
    }

    public void GoBack() => GoTo(_previousPage);

    private static T Resolve<T>() where T : ObservableObject
        => App.Services.GetRequiredService<T>();
}

/// <summary>Implemented by page viewmodels that need to react to navigation.</summary>
public interface IPageAware
{
    void OnNavigatedTo(INavigator navigator);
}
