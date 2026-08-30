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
        if (string.IsNullOrWhiteSpace(BootstrapSecret.Password))
        {
            StatusText.Text = "Введите bootstrap-секрет.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить зашифрованный ключ администратора",
            Filter = "Зашифрованный ключ IPA Studio (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = "ipa-studio-admin-key.txt",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        IsEnabled = false;
        StatusText.Text = "Создание ключа…";
        try
        {
            await _support.CreateAdministratorKeyAsync(dialog.FileName, BootstrapSecret.Password, Password.Password);
            await _support.ImportAdministratorKeyAsync(dialog.FileName);
            MessageBox.Show(this,
                "Ключ создан и безопасно импортирован на этот ПК. Сохраните TXT-файл как резервную копию отдельно от пароля.",
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
