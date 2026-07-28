using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services.ICloud;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IPAStudio.App.ViewModels;

/// <summary>Selectable wrapper around an iCloud photo.</summary>
public sealed partial class ICloudAssetViewModel : ObservableObject
{
    public ICloudAssetViewModel(ICloudAsset asset) => Item = asset;

    public ICloudAsset Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// The grid preview. Fetched through the signed-in http client rather than bound
    /// straight to the CloudKit url: that url is only served to a caller carrying the
    /// session cookies, so letting the image control fetch it yields a 401 and an empty
    /// tile.
    /// </summary>
    [ObservableProperty]
    private BitmapImage? _preview;

    /// <summary>
    /// Set once a preview has been requested, successfully or not, so a photo iCloud will
    /// not serve a preview for is not retried on every pass.
    /// </summary>
    public bool PreviewAttempted { get; set; }
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

    /// <summary>
    /// Where the code went, in words. Accounts without a second Apple device get an SMS or
    /// a call, and telling someone to "check your other device" then sends them looking for
    /// a code that is already sitting in their messages.
    /// </summary>
    [ObservableProperty]
    private string _twoFactorDeliveryText = "";

    /// <summary>
    /// True when the code went to the account's other Apple devices and a trusted number is
    /// on file, so switching to a text message is worth offering. Hidden once the code has
    /// already been texted - there is nothing left to switch to.
    /// </summary>
    [ObservableProperty]
    private bool _canSendSms;

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

    /// <summary>Albums in the iCloud library, with "all photos" first.</summary>
    public ObservableCollection<ICloudAlbum> Albums { get; } = new();

    /// <summary>
    /// Which album the grid is showing. Assigning it reloads the grid, so it is also what
    /// the album list binds its selection to.
    /// </summary>
    [ObservableProperty]
    private ICloudAlbum? _selectedAlbum;

    /// <summary>The note being read, or null while the list is showing.</summary>
    [ObservableProperty]
    private ICloudNote? _openNote;

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
                TwoFactorDeliveryText = DescribeCodeDelivery();
                CanSendSms = _icloud.CanSendTwoFactorSms && _icloud.TwoFactorDelivery == "device";
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

    private string DescribeCodeDelivery() => _icloud.TwoFactorDelivery switch
    {
        "sms" when _icloud.TwoFactorPhoneNumber is { Length: > 0 } number
            => Loc.Format("L.ICloud.CodeSentSms", number),
        "voice" when _icloud.TwoFactorPhoneNumber is { Length: > 0 } number
            => Loc.Format("L.ICloud.CodeSentCall", number),
        "sms" or "voice" => Loc.Get("L.ICloud.CodeSentPhone"),
        _ => Loc.Get("L.ICloud.CodeSentDevice"),
    };

    /// <summary>
    /// Asks Apple to send the code again. Separate from starting over, which throws away
    /// the password too — the usual reason to be here is a push that never arrived.
    /// </summary>
    [RelayCommand]
    private Task ResendCode() => RequestCodeAsync(preferSms: false);

    /// <summary>
    /// Asks Apple to text the code instead. The point of a separate command is that the
    /// other Apple device is not always to hand, and the push route gives the user no way
    /// out on its own.
    /// </summary>
    [RelayCommand]
    private Task SendCodeBySms() => RequestCodeAsync(preferSms: true);

    private async Task RequestCodeAsync(bool preferSms)
    {
        if (IsBusy) return;

        IsBusy = true;
        HasError = false;
        StatusText = Loc.Get(preferSms ? "L.ICloud.SendingSms" : "L.ICloud.SendingCode");
        try
        {
            var sent = await _icloud.ResendTwoFactorCodeAsync(preferSms).ConfigureAwait(true);
            TwoFactorCode = "";
            TwoFactorDeliveryText = DescribeCodeDelivery();
            CanSendSms = _icloud.CanSendTwoFactorSms && _icloud.TwoFactorDelivery == "device";

            if (sent) StatusText = Loc.Get("L.ICloud.CodeResent");
            else Fail(Loc.Get("L.ICloud.CodeResendFailed"));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud 2FA resend failed: {ex.Message}");
            Fail(Loc.Get("L.ICloud.CodeResendFailed"));
        }
        finally
        {
            IsBusy = false;
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
        TwoFactorDeliveryText = "";
        CanSendSms = false;
        AccountName = "";
        Password = "";
        TwoFactorCode = "";
        StatusText = "";
        HasError = false;
        Contacts.Clear();
        Photos.Clear();
        Notes.Clear();
        Albums.Clear();
        _reloadOnAlbumChange = false;
        SelectedAlbum = null;
        _reloadOnAlbumChange = true;
        OpenNote = null;
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
                    if (Albums.Count == 0)
                    {
                        var albums = await _icloud.GetAlbumsAsync(ct).ConfigureAwait(true);
                        foreach (var a in albums) Albums.Add(a);
                    }

                    // Seeding the selection would reload through OnSelectedAlbumChanged and
                    // re-enter this method, so the change handler sits out this one assignment.
                    if (SelectedAlbum is null)
                    {
                        _reloadOnAlbumChange = false;
                        SelectedAlbum = Albums.FirstOrDefault();
                        _reloadOnAlbumChange = true;
                    }

                    await LoadPhotosAsync(SelectedAlbum, ct).ConfigureAwait(true);
                    break;

                default:
                    var notes = await _icloud.GetNotesAsync(ct).ConfigureAwait(true);
                    OpenNote = null;
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

    /// <summary>
    /// False only while the album selection is being seeded, so choosing an album still
    /// reloads the grid but the initial load does not reload itself.
    /// </summary>
    private bool _reloadOnAlbumChange = true;

    partial void OnSelectedAlbumChanged(ICloudAlbum? value)
    {
        if (_reloadOnAlbumChange) _ = LoadCurrentTabAsync();
    }

    /// <summary>
    /// Fills the grid from one album, or the whole library, and then fetches the previews.
    /// </summary>
    private async Task LoadPhotosAsync(ICloudAlbum? album, CancellationToken ct)
    {
        // One call for all three album kinds: a real album, a smart album and the whole
        // library need different CloudKit queries, and picking between them here meant the
        // smart albums silently fell through to "everything".
        var photos = album is null
            ? await _icloud.GetPhotosAsync(ct: ct).ConfigureAwait(true)
            : await _icloud.GetAlbumAssetsAsync(album, ct: ct).ConfigureAwait(true);

        Photos.Clear();
        foreach (var p in photos) Photos.Add(new ICloudAssetViewModel(p));
        OnPropertyChanged(nameof(SelectedPhotoCount));
        DownloadPhotosCommand.NotifyCanExecuteChanged();

        StatusText = photos.Count == 0
            ? Loc.Get("L.ICloud.NoPhotos")
            : Loc.Format("L.ICloud.PhotoCount", photos.Count);

        // Deliberately not awaited: the grid is usable while the tiles fill in, and a slow
        // preview must not hold up the count the user is waiting for.
        _ = LoadPreviewsAsync(Photos.ToList(), ct);
    }

    /// <summary>
    /// Fetches the grid previews a few at a time.
    ///
    /// The tiles cannot simply bind to the CloudKit url: it is signed for the logged-in
    /// session, and an unauthenticated fetch by the image control gets a 401 - which is why
    /// the grid used to show nothing but placeholders. Concurrency is capped because a large
    /// library would otherwise open a request per photo at once.
    /// </summary>
    private async Task LoadPreviewsAsync(List<ICloudAssetViewModel> rows, CancellationToken ct)
    {
        const int Parallelism = 6;
        using var gate = new SemaphoreSlim(Parallelism);
        var loaded = 0;

        try
        {
            var tasks = rows.Select(async row =>
            {
                // Either rendition will do; skipping on a missing thumbnail alone left the
                // assets that only publish a medium rendition permanently blank.
                if (row.PreviewAttempted) return;
                if (row.Item.ThumbnailUrl is null && row.Item.PreviewUrl is null) return;
                row.PreviewAttempted = true;

                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var bytes = await _icloud.GetThumbnailAsync(row.Item, ct).ConfigureAwait(false);
                    if (bytes is null || bytes.Length == 0) return;

                    var image = DecodePreview(bytes);
                    if (image is null) return;

                    // Freezing lets the bitmap cross to the UI thread; without it the
                    // assignment throws because it was created on a worker.
                    image.Freeze();
                    Interlocked.Increment(ref loaded);

                    await App.Current.Dispatcher.InvokeAsync(() => row.Preview = image);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(true);
            AppLog.Info($"icloud: {loaded} of {rows.Count} previews loaded");
        }
        catch (OperationCanceledException)
        {
            // Album switched or signed out mid-fetch; the newer load owns the grid.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"icloud: preview loading stopped ({ex.Message})");
        }
    }

    /// <summary>
    /// Decodes a preview at tile size. iCloud thumbnails are jpeg whatever the original
    /// was, so a HEIC or video original still previews here even when Windows has no codec
    /// for the file itself.
    /// </summary>
    private static BitmapImage? DecodePreview(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = 160; // tiles render at 132 wide
            image.StreamSource = stream;
            image.EndInit();
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"icloud: could not decode a preview ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Opens a note. The list query returns only a snippet, so the body is fetched here -
    /// which is why tapping a note used to do nothing.
    /// </summary>
    [RelayCommand]
    private async Task OpenNoteBody(ICloudNote? note)
    {
        if (note is null) return;

        OpenNote = note;
        if (note.Body is not null) return;

        try
        {
            var body = await _icloud.GetNoteBodyAsync(note, _cts?.Token ?? default)
                .ConfigureAwait(true);

            // Store the empty string rather than null for a genuinely empty note, so it is
            // not fetched again every time it is opened.
            note.Body = body ?? "";
            OnPropertyChanged(nameof(OpenNote));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn($"icloud: could not open the note ({ex.Message})");
            note.Body = Loc.Get("L.ICloud.NoteBodyFailed");
            OnPropertyChanged(nameof(OpenNote));
        }
    }

    [RelayCommand]
    private void CloseNote() => OpenNote = null;

    private bool CanExportContacts() => Contacts.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExportContacts))]
    private async Task ExportContacts()
    {
        // Both formats are offered because they cover both phones: a .vcf imports directly on
        // an iPhone and on Android, while .csv is what Google Contacts and Excel expect.
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("L.ICloud.ExportContacts"),
            FileName = "icloud-contacts",
            Filter = Loc.Get("L.ICloud.ContactsFilter"),
            DefaultExt = "vcf",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // The chosen filter decides the format, but a name typed with the other extension
            // wins: saving "contacts.csv" must not quietly write a vCard.
            var isCsv = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                        || (dialog.FilterIndex == 2 && !dialog.FileName.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase));

            if (isCsv)
                await ICloudService.ExportContactsCsvAsync(Contacts, dialog.FileName).ConfigureAwait(true);
            else
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
