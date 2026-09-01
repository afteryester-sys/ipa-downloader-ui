using System.Windows.Controls;
using System.Windows.Input;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    // PasswordBox does not support data binding for security reasons;
    // push the value into the viewmodel manually.
    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
            vm.Password = PasswordInput.Password;
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is LoginViewModel vm && vm.SignInCommand.CanExecute(null))
            vm.SignInCommand.Execute(null);
    }
}
