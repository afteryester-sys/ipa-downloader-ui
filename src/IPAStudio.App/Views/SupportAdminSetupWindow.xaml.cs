using System.Windows;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Views;

public partial class SupportAdminSetupWindow : Window
{
    private readonly RemoteSupportService _support;

    public SupportAdminSetupWindow(RemoteSupportService support)
    {
        InitializeComponent();
        _support = support;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Password.Password.Length < 12)
        {
            StatusText.Text = "Пароль должен содержать не менее 12 символов.";
            return;
        }
        if (!string.Equals(Password.Password, PasswordAgain.Password, StringComparison.Ordinal))
        {
            StatusText.Text = "Пароли не совпадают.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить ключ администратора",
            Filter = "IPA Studio admin key (*.json)|*.json",
            FileName = "ipa-studio-admin-key.json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        IsEnabled = false;
        StatusText.Text = "Создание ключа…";
        try
        {
            await _support.CreateAdministratorKeyAsync(dialog.FileName, BootstrapSecret.Password, Password.Password);
            MessageBox.Show(this, "Ключ создан. Храните файл отдельно от пароля; восстановить его невозможно.",
                "Ключ администратора готов", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; IsEnabled = true; }
        finally
        {
            BootstrapSecret.Clear();
            Password.Clear();
            PasswordAgain.Clear();
        }
    }
}
