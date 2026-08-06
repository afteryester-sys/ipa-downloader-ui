using CommunityToolkit.Mvvm.ComponentModel;
using IPAStudio.App.Services;
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

    /// <summary>
    /// Direct download page: fetch an IPA into a folder of the user's choice.
    /// Needs an Apple ID but no connected device.
    /// </summary>
    DirectDownload,

    /// <summary>
    /// iCloud page: signs in to an Apple ID and browses contacts, photos and notes from
    /// iCloud itself. Needs no connected device.
    /// </summary>
    ICloud,

    /// <summary>
    /// "On the device" page: the apps actually installed on the connected device, with the
    /// option to save one owned by the signed-in Apple ID as an IPA.
    /// </summary>
    OnDevice,
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

    /// <summary>Opens the list of apps installed on a device.</summary>
    void GoToOnDevice(Device device);

    /// <summary>
    /// Opens the queue page for a specific operation. Needed because the queue page no
    /// longer has one queue of its own: it has to be told which operation to show.
    /// </summary>
    void GoToOperation(Operation operation);

    /// <summary>
    /// Returns to the previously shown page. Needed by pages reachable from anywhere
    /// (e.g. the direct download page via the corner menu), where a hardcoded back target would
    /// dump the user somewhere they never came from.
    /// </summary>
    void GoBack();
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

    /// <summary>
    /// Pages visited, most recent last, each with the device it was showing.
    ///
    /// Replaces a single "previous page" field, which could not survive its own use: going back
    /// ran through <see cref="GoTo"/>, and that first recorded the page being left as the new
    /// previous one. So after one Back the previous page was the page just abandoned, and the
    /// next Back returned to it — the two screens trading places for as long as the user kept
    /// pressing, which is exactly the loop reported.
    ///
    /// The device is stored alongside the page because the device-bound screens are handed
    /// their target on the way in. Recording the page alone sent the user back to an "Apps on
    /// the device" screen with no device attached to it.
    ///
    /// A list rather than a Stack so the oldest entry can be dropped once the history grows
    /// long; a Stack can only be trimmed from the end that matters.
    /// </summary>
    private readonly List<(Page Page, Device? Device)> _history = new();

    /// <summary>How many steps back are kept. Deep enough for any real path through the app.</summary>
    private const int MaxHistory = 32;

    /// <summary>Device passed to a page that needs a target (login/info/photos).</summary>
    private Device? _pendingDevice;

    /// <summary>
    /// The device the current page was opened with, so it can be restored when the user comes
    /// back to it. Distinct from <see cref="_pendingDevice"/>, which is cleared as soon as it
    /// has been handed over.
    /// </summary>
    private Device? _currentDevice;

    /// <summary>
    /// False until the first page has been shown, so the startup navigation does not record a
    /// step back to a page that was never displayed.
    /// </summary>
    private bool _hasNavigated;

    /// <summary>Whether there is anywhere to go back to.</summary>
    public bool CanGoBack => _history.Count > 0;

    /// <summary>Global updater backing the corner update flyout (available everywhere).</summary>
    public UpdaterViewModel Updater { get; }

    /// <summary>Backs the corner circle and the operations list, bound to from the window.</summary>
    public OperationService Operations { get; }

    /// <summary>
    /// Raised when an operation is minimised, for the window to animate the collapse into
    /// the corner circle. An event rather than a viewmodel flag because it is a one-shot
    /// visual cue with no state to hold.
    /// </summary>
    public event EventHandler? OperationMinimized;

    public ShellViewModel(UpdaterViewModel updater, OperationService operations)
    {
        Updater = updater;
        Operations = operations;

        Operations.ReturnRequested += (_, op) => GoToOperation(op);
        Operations.MinimizeRequested += (_, op) => MinimizeOperation(op);

        GoTo(Page.Setup);
    }

    /// <summary>
    /// Leaves an operation running and returns to the page it came from.
    ///
    /// The queue page is detached on the way out so a background operation's events stop
    /// arriving at a page now showing something else.
    /// </summary>
    private void MinimizeOperation(Operation operation)
    {
        Resolve<QueueViewModel>().Detach();

        // Back to where the operation was started from, which is where the user would go
        // next anyway — usually to start the second operation.
        if (operation.ReturnDevice is not null && operation.ReturnPage == Page.OnDevice)
            GoToOnDevice(operation.ReturnDevice);
        else if (operation.ReturnDevice is not null && operation.ReturnPage == Page.AppPicker)
            GoToAppPicker(operation.ReturnDevice);
        else
            GoTo(Page.Devices);

        OperationMinimized?.Invoke(this, EventArgs.Empty);
    }

    public void GoToOperation(Operation operation)
    {
        // Attach before navigating: OnNavigatedTo starts the run, and with no queue attached
        // it would open an empty page and never start the work.
        Resolve<QueueViewModel>().Attach(operation);
        GoTo(Page.Queue);
    }

    public void GoTo(Page page) => Navigate(page, recordHistory: true);

    /// <summary>
    /// Performs the page switch. <paramref name="recordHistory"/> is false only when going
    /// back: recording that step would put the page being left onto the history the user is
    /// currently walking out of, which is what made Back bounce between two screens.
    /// </summary>
    private void Navigate(Page page, bool recordHistory)
    {
        // A repeat of the current page is not a step; recording it would mean one Back press
        // that visibly does nothing. The very first navigation has no page to record either.
        if (recordHistory && _hasNavigated && page != CurrentPage)
        {
            _history.Add((CurrentPage, _currentDevice));

            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        // Captured before _pendingDevice is cleared below, so returning here later reopens the
        // page against the same device.
        _currentDevice = _pendingDevice ?? (page == CurrentPage ? _currentDevice : null);

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
            Page.DirectDownload => Resolve<DirectDownloadViewModel>(),
            Page.ICloud => Resolve<ICloudViewModel>(),
            Page.OnDevice => Resolve<OnDeviceViewModel>(),
            _ => CurrentViewModel,
        };

        // Hand the pending device to pages that need one, before OnNavigatedTo runs.
        switch (CurrentViewModel)
        {
            case LoginViewModel login: login.SetPendingDevice(_pendingDevice); break;
            case DeviceInfoViewModel info when _pendingDevice is not null: info.SetDevice(_pendingDevice); break;
            case PhotosViewModel photos when _pendingDevice is not null: photos.SetDevice(_pendingDevice); break;
            case OnDeviceViewModel onDevice when _pendingDevice is not null: onDevice.SetDevice(_pendingDevice); break;
        }

        if (CurrentViewModel is IPageAware aware)
            aware.OnNavigatedTo(this);

        _pendingDevice = null;
        _hasNavigated = true;

        OnPropertyChanged(nameof(CanGoBack));
    }

    public void GoToAppPicker(Device device)
    {
        var picker = Resolve<AppPickerViewModel>();
        picker.TargetDevice = device;

        // This page takes its device by assignment rather than through _pendingDevice, but the
        // history still needs to know which device it was showing.
        _pendingDevice = device;
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

    public void GoToOnDevice(Device device)
    {
        _pendingDevice = device;
        GoTo(Page.OnDevice);
    }

    public void GoBack()
    {
        // Nothing recorded yet — the device list is the app's root, and pressing Back on a page
        // reached some other way should land somewhere sensible rather than do nothing.
        if (_history.Count == 0)
        {
            Navigate(Page.Devices, recordHistory: false);
            return;
        }

        var (page, device) = _history[^1];
        _history.RemoveAt(_history.Count - 1);

        // Restored so a device-bound page reopens against the device it was showing.
        _pendingDevice = device;

        if (page == Page.AppPicker && device is not null)
            Resolve<AppPickerViewModel>().TargetDevice = device;

        Navigate(page, recordHistory: false);
    }

    private static T Resolve<T>() where T : ObservableObject
        => App.Services.GetRequiredService<T>();
}

/// <summary>Implemented by page viewmodels that need to react to navigation.</summary>
public interface IPageAware
{
    void OnNavigatedTo(INavigator navigator);
}
