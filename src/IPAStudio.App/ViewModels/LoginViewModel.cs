using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// Apple ID sign-in flow: email + password, then an animated 2FA step when
/// the account requires it. Attempts silent session restore on first show.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject, IPageAware
{
    private readonly AuthService _auth;
    private INavigator? _navigator;
    private bool _sessionChecked;

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
        ErrorMessage = null;
        try
        {
            var result = await _auth.LoginAsync(Email.Trim(), Password);
            if (result.Success)
            {
                _navigator?.GoTo(Page.Devices);
            }
            else if (result.RequiresTwoFactor)
            {
                IsTwoFactorStep = true;
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConfirmTwoFactor() =>
        !IsBusy && TwoFactorCode.Trim().Length >= 6;

    [RelayCommand(CanExecute = nameof(CanConfirmTwoFactor))]
    private async Task ConfirmTwoFactorAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _auth.LoginAsync(Email.Trim(), Password, TwoFactorCode);
            if (result.Success)
            {
                IsTwoFactorStep = false;
                TwoFactorCode = "";
                _navigator?.GoTo(Page.Devices);
            }
            else
            {
                ErrorMessage = result.Error ?? "Invalid code";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BackToCredentials()
    {
        IsTwoFactorStep = false;
        TwoFactorCode = "";
        ErrorMessage = null;
    }
}
