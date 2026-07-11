using System.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Apple ID sign-in flow: email + password, then an animated 2FA step when the
/// account requires it. The 2FA code is requested on demand while ipatool is
/// running and pushed back to it via stdin. Attempts silent session restore first.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject, IPageAware
{
    private readonly AuthService _auth;
    private INavigator? _navigator;
    private bool _sessionChecked;

    private TaskCompletionSource<string?>? _twoFactorTcs;
    private CancellationTokenSource? _loginCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _email = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTwoFactorCommand))]
    private string _twoFactorCode = "";

    [ObservableProperty]
    private bool _isTwoFactorStep;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRestoringSession;

    [ObservableProperty]
    private string? _errorMessage;

    public LoginViewModel(AuthService auth)
    {
        _auth = auth;
    }

    public async void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;

        if (_sessionChecked) return;
        _sessionChecked = true;

        IsRestoringSession = true;
        try
        {
            var account = await _auth.TryRestoreSessionAsync();
            if (account is not null)
                _navigator?.GoTo(Page.Devices);
        }
        finally
        {
            IsRestoringSession = false;
        }
    }

    private bool CanSignIn() =>
        !IsBusy && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrEmpty(Password);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        IsTwoFactorStep = false;
        ErrorMessage = null;
        _loginCts = new CancellationTokenSource();

        try
        {
            var result = await _auth.LoginAsync(
                Email.Trim(), Password, RequestTwoFactorCodeAsync, _loginCts.Token);

            if (result.Success)
            {
                _navigator?.GoTo(Page.Devices);
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled from the 2FA step; stay on the login screen.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsTwoFactorStep = false;
            _twoFactorTcs = null;
            _loginCts?.Dispose();
            _loginCts = null;
        }
    }

    /// <summary>
    /// Invoked by <see cref="AuthService"/> (on a background thread) when ipatool asks
    /// for a 2FA code. Switches the UI to the code entry step and awaits user input.
    /// </summary>
    private Task<string?> RequestTwoFactorCodeAsync(CancellationToken ct)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            IsBusy = false;
            IsTwoFactorStep = true;
            TwoFactorCode = "";
            ErrorMessage = null;

            _twoFactorTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => _twoFactorTcs?.TrySetResult(null));
            return _twoFactorTcs.Task;
        });
    }

    private bool CanConfirmTwoFactor() =>
        !IsBusy && TwoFactorCode.Trim().Length >= 6;

    [RelayCommand(CanExecute = nameof(CanConfirmTwoFactor))]
    private void ConfirmTwoFactor()
    {
        var tcs = _twoFactorTcs;
        if (tcs is null) return;

        // Hand the code back to ipatool and show progress while it finishes.
        IsTwoFactorStep = false;
        IsBusy = true;
        tcs.TrySetResult(TwoFactorCode.Trim());
    }

    [RelayCommand]
    private void BackToCredentials()
    {
        _loginCts?.Cancel();
        _twoFactorTcs?.TrySetResult(null);
        IsTwoFactorStep = false;
        IsBusy = false;
        TwoFactorCode = "";
        ErrorMessage = null;
    }
}
