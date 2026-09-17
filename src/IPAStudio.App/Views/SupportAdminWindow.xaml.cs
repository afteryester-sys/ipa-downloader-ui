using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Views;

public partial class SupportAdminWindow : Window
{
    private readonly RemoteSupportService _support;
    private readonly ObservableCollection<SupportComputer> _computers = new();
    private string? _token;

    public SupportAdminWindow(RemoteSupportService support)
    {
        InitializeComponent();
        _support = support;
        DevicesList.ItemsSource = _computers;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (AdminPassword.Password.Length == 0) return;
        SetBusy(true, "Проверка ключа и пароля…");
        try
        {
            _token = await _support.AuthenticateAdministratorAsync(AdminPassword.Password);
            AdminPassword.Clear();
            LoginPanel.Visibility = Visibility.Collapsed;
            DevicesList.Visibility = Visibility.Visible;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async Task RefreshAsync()
    {
        if (_token is null) return;
        var items = await _support.GetComputersAsync(_token);
        _computers.Clear();
        foreach (var item in items) _computers.Add(item);
        StatusText.Text = items.Count == 0 ? "Разрешившие доступ компьютеры пока не найдены." : $"Компьютеров: {items.Count}";
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SupportComputer computer) return;
        try { _support.Connect(computer); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SupportComputer computer || string.IsNullOrWhiteSpace(computer.EncryptedSessionSecret))
        {
            StatusText.Text = "Пароль RustDesk пока не получен.";
            return;
        }
        Clipboard.SetText(computer.EncryptedSessionSecret);
        StatusText.Text = "Пароль RustDesk скопирован. Буфер обмена очистится автоматически через 60 секунд.";
        _ = ClearClipboardAsync(computer.EncryptedSessionSecret);
    }

    private static async Task ClearClipboardAsync(string value)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        try { if (Clipboard.ContainsText() && Clipboard.GetText() == value) Clipboard.Clear(); }
        catch { }
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (_token is null || (sender as Button)?.Tag is not SupportComputer computer) return;
        if (MessageBox.Show(this, $"Отозвать доступ для «{computer.DisplayName}»?", "Отзыв доступа",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true, "Отзыв доступа…");
        try { await _support.RevokeAsync(_token, computer.Id); await RefreshAsync(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        IsEnabled = !busy;
        if (message is not null) StatusText.Text = message;
    }
}
