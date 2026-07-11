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
}

/// <summary>Simple page-based navigation used by all viewmodels.</summary>
public interface INavigator
{
    void GoTo(Page page);
    void GoToAppPicker(Device device);
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
            _ => CurrentViewModel,
        };

        if (CurrentViewModel is IPageAware aware)
            aware.OnNavigatedTo(this);
    }

    public void GoToAppPicker(Device device)
    {
        var picker = Resolve<AppPickerViewModel>();
        picker.TargetDevice = device;
        GoTo(Page.AppPicker);
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
