using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services.ICloud;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>Selectable wrapper around an iCloud photo.</summary>
public sealed partial class ICloudAssetViewModel : ObservableObject
{
    public ICloudAssetViewModel(ICloudAsset asset) => Item = asset;

    public ICloudAsset Item { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// iCloud screen: signs in to the account and browses contacts, photos and notes
/// straight from iCloud, independent of whether a device is plugged in.
///
/// Sign-in is a two-step flow because Apple requires two-factor auth on essentially all
/// accounts: password first, then the six-digit code. After that Apple issues a trust
/// token, so later launches resume silently.
/// </summary>
public sealed partial class ICloudViewModel : ObservableObject, IPageAware
{
    private readonly ICloudService _icloud;
    private INavigator? _navigator;
    private CancellationTokenSource? _cts;

    public ICloudViewModel(ICloudService icloud) => _icloud = icloud;

    // ── sign-in state ──

    [ObservableProperty]
    private string _appleId = "";

    /// <summary>
    /// Bound from the PasswordBox in code-behind (WPF won't bind PasswordBox directly).
    /// Cleared the moment sign-in finishes so it does not linger in memory.
    /// </summary>
    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _twoFactorCode = "";

    /// <summary>True while Apple is waiting for the six-digit code.</summary>
    [ObservableProperty]
    private bool _needsTwoFactorCode;

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private string _accountName = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>Set when something went wrong, so the view can style it as an error.</summary>
    [ObservableProperty]
    private bool _hasError;

    // ── data ──

    public ObservableCollection<ICloudContact> Contacts { get; } = new();
    public ObservableCollection<ICloudAssetViewModel> Photos { get; } = new();
    public ObservableCollection<ICloudNote> Notes { get; } = new();

    /// <summary>0 = contacts, 1 = photos, 2 = notes. Drives which list is loaded.</summary>
    [ObservableProperty]
    private int _selectedTab;

    public int SelectedPhotoCount => Photos.Count(p => p.IsSelected);

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        _ = TryResumeAsync();
    }

    [RelayCommand]
    private void Back() => _navigator?.GoBack();

    /// <summary>
    /// Attempts to pick up a stored session so a returning user goes straight to their
    /// data without retyping anything.
    /// </summary>
    private async Task TryResumeAsync()
    {
        if (IsSignedIn || IsBusy) return;
        if (!_icloud.HasSavedSession) return;

        IsBusy = true;
        StatusText = Loc.Get("L.ICloud.Resuming");
        HasError = false;
        try
        {
            if (await _icloud.TryRestoreSessionAsync().ConfigureAwait(true))
            {
                await AfterSignInAsync().ConfigureAwait(true);
                return;
            }
            StatusText = Loc.Get("L.ICloud.SessionExpired");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud resume failed: {ex.Message}");
            StatusText = Loc.Get("L.ICloud.SessionExpired");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSignIn() => !IsBusy && !string.IsNullOrWhiteSpace(AppleId) && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignIn()
    {
        IsBusy = true;
        HasError = false;
        StatusText = Loc.Get("L.ICloud.SigningIn");

        try
        {
            var result = await _icloud.SignInAsync(AppleId.Trim(), Password).ConfigureAwait(true);
            await HandleSignInResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud sign-in failed: {ex.Message}");
            Fail(Loc.Get("L.ICloud.SignInFailed"));
        }
        finally
        {
            // The password has served its purpose (one SRP proof); don't keep it around.
            Password = "";
            IsBusy = false;
        }
    }

    private bool CanSubmitCode() => !IsBusy && TwoFactorCode.Trim().Length >= 6;

    [RelayCommand(CanExecute = nameof(CanSubmitCode))]
    private async Task SubmitCode()
    {
        IsBusy = true;
        HasError = false;
        StatusText = Loc.Get("L.ICloud.VerifyingCode");

        try
        {
            var result = await _icloud.SubmitTwoFactorCodeAsync(TwoFactorCode).ConfigureAwait(true);
            if (result == ICloudSignInResult.InvalidCredentials)
            {
                Fail(Loc.Get("L.ICloud.BadCode"));
                TwoFactorCode = "";
                return;
            }
            await HandleSignInResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud 2FA failed: {ex.Message}");
            Fail(Loc.Get("L.ICloud.SignInFailed"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task HandleSignInResultAsync(ICloudSignInResult result)
    {
        switch (result)
        {
            case ICloudSignInResult.Success:
                NeedsTwoFactorCode = false;
                TwoFactorCode = "";
                await AfterSignInAsync().ConfigureAwait(true);
                break;

            case ICloudSignInResult.NeedsTwoFactorCode:
                NeedsTwoFactorCode = true;
                HasError = false;
                StatusText = Loc.Get("L.ICloud.EnterCode");
                break;

            case ICloudSignInResult.InvalidCredentials:
                Fail(Loc.Get("L.ICloud.BadCredentials"));
                break;

            default:
                Fail(Loc.Get("L.ICloud.SignInFailed"));
                break;
        }
    }

    private async Task AfterSignInAsync()
    {
        IsSignedIn = true;
        NeedsTwoFactorCode = false;
        HasError = false;
        AccountName = _icloud.AccountName ?? AppleId;
        StatusText = "";
        await LoadCurrentTabAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SignOut()
    {
        _cts?.Cancel();
        _icloud.SignOut();

        IsSignedIn = false;
        NeedsTwoFactorCode = false;
        AccountName = "";
        Password = "";
        TwoFactorCode = "";
        StatusText = "";
        HasError = false;
        Contacts.Clear();
        Photos.Clear();
        Notes.Clear();
    }

    partial void OnSelectedTabChanged(int value) => _ = LoadCurrentTabAsync();

    [RelayCommand]
    private async Task Refresh() => await LoadCurrentTabAsync();

    /// <summary>Loads whichever list the user is looking at, once.</summary>
    private async Task LoadCurrentTabAsync()
    {
        if (!IsSignedIn || IsBusy) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        HasError = false;
        StatusText = Loc.Get("L.ICloud.Loading");

        try
        {
            switch (SelectedTab)
            {
                case 0:
                    var contacts = await _icloud.GetContactsAsync(ct).ConfigureAwait(true);
                    Contacts.Clear();
                    foreach (var c in contacts) Contacts.Add(c);
                    StatusText = contacts.Count == 0
                        ? Loc.Get("L.ICloud.NoContacts")
                        : Loc.Format("L.ICloud.ContactCount", contacts.Count);
                    break;

                case 1:
                    var photos = await _icloud.GetPhotosAsync(ct: ct).ConfigureAwait(true);
                    Photos.Clear();
                    foreach (var p in photos) Photos.Add(new ICloudAssetViewModel(p));
                    OnPropertyChanged(nameof(SelectedPhotoCount));
                    StatusText = photos.Count == 0
                        ? Loc.Get("L.ICloud.NoPhotos")
                        : Loc.Format("L.ICloud.PhotoCount", photos.Count);
                    break;

                default:
                    var notes = await _icloud.GetNotesAsync(ct).ConfigureAwait(true);
                    Notes.Clear();
                    foreach (var n in notes) Notes.Add(n);
                    StatusText = notes.Count == 0
                        ? Loc.Get("L.ICloud.NoNotes")
                        : Loc.Format("L.ICloud.NoteCount", notes.Count);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Switching tabs quickly: the newer load owns the status text.
        }
        catch (ICloudRequestException ex)
        {
            // A rejected request is not an empty account: say so, and name the code so the
            // difference between "nothing there" and "Apple said no" is visible.
            AppLog.Warn($"iCloud load failed: {ex.Message}");
            Fail(ex.StatusCode is 401 or 421
                ? Loc.Get("L.ICloud.SessionExpired")
                : Loc.Format("L.ICloud.LoadFailedCode", ex.StatusCode));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud load failed: {ex.Message}");
            Fail(Loc.Get("L.ICloud.LoadFailed"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExportContacts() => Contacts.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExportContacts))]
    private async Task ExportContacts()
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("L.ICloud.ExportContacts"),
            FileName = "icloud-contacts.vcf",
            Filter = Loc.Get("L.ICloud.VCardFilter"),
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await ICloudService.ExportContactsVCardAsync(Contacts, dialog.FileName).ConfigureAwait(true);
            HasError = false;
            StatusText = Loc.Format("L.ICloud.ContactsExported", Contacts.Count);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud contact export failed: {ex.Message}");
            Fail(Loc.Get("L.ICloud.ExportFailed"));
        }
    }

    [RelayCommand]
    private void ToggleSelectAllPhotos()
    {
        var selectAll = SelectedPhotoCount < Photos.Count;
        foreach (var p in Photos) p.IsSelected = selectAll;
        OnPropertyChanged(nameof(SelectedPhotoCount));
        DownloadPhotosCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called by the view when a checkbox changes, to refresh the counter.</summary>
    public void OnPhotoSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedPhotoCount));
        DownloadPhotosCommand.NotifyCanExecuteChanged();
    }

    private bool CanDownloadPhotos() => SelectedPhotoCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDownloadPhotos))]
    private async Task DownloadPhotos()
    {
        var dialog = new OpenFolderDialog { Title = Loc.Get("L.ICloud.PickDownloadFolder") };
        if (dialog.ShowDialog() != true) return;

        var selected = Photos.Where(p => p.IsSelected).Select(p => p.Item).ToList();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        HasError = false;
        var done = 0;
        var failed = 0;

        try
        {
            foreach (var asset in selected)
            {
                ct.ThrowIfCancellationRequested();
                StatusText = Loc.Format("L.ICloud.Downloading", done + 1, selected.Count);

                var path = await _icloud.DownloadAssetAsync(asset, dialog.FolderName, ct).ConfigureAwait(true);
                if (path is null) failed++;
                else done++;
            }

            StatusText = failed == 0
                ? Loc.Format("L.ICloud.Downloaded", done)
                : Loc.Format("L.ICloud.DownloadedWithErrors", done, failed);
            HasError = failed > 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Format("L.ICloud.Downloaded", done);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud download failed: {ex.Message}");
            Fail(Loc.Get("L.ICloud.DownloadFailed"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string message)
    {
        HasError = true;
        StatusText = message;
    }

    partial void OnAppleIdChanged(string value) => SignInCommand.NotifyCanExecuteChanged();
    partial void OnPasswordChanged(string value) => SignInCommand.NotifyCanExecuteChanged();
    partial void OnTwoFactorCodeChanged(string value) => SubmitCodeCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        SignInCommand.NotifyCanExecuteChanged();
        SubmitCodeCommand.NotifyCanExecuteChanged();
        ExportContactsCommand.NotifyCanExecuteChanged();
        DownloadPhotosCommand.NotifyCanExecuteChanged();
    }
}
