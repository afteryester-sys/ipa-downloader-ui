using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IPAStudio.App.Services;
using IPAStudio.Core.Localization;

namespace IPAStudio.App.Views;

public partial class ModernDialogWindow : Window
{
    public ModernDialogResult DialogResultType { get; private set; } = ModernDialogResult.Cancel;

    private readonly ModernDialogButtons _buttons;

    public ModernDialogWindow(
        string title,
        string message,
        ModernDialogButtons buttons,
        ModernDialogType type,
        string? confirmText,
        string? cancelText,
        bool isDestructive = false)
    {
        InitializeComponent();
        _buttons = buttons;

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "IPA Studio" : title;
        MessageText.Text = message;

        ConfigureVisuals(type, isDestructive);
        ConfigureButtons(buttons, confirmText, cancelText, isDestructive);
    }

    private void ConfigureVisuals(ModernDialogType type, bool isDestructive)
    {
        switch (type)
        {
            case ModernDialogType.Danger:
            case ModernDialogType.Error:
                IconBadge.Background = TryFindBrush("Brush.DangerSoft", "#25FF453A");
                IconText.Foreground = TryFindBrush("Brush.Danger", "#FF453A");
                IconText.Text = "\uEA39"; // Error / Warning badge
                break;

            case ModernDialogType.Warning:
                IconBadge.Background = TryFindBrush("Brush.WarningSoft", "#25FFD60A");
                IconText.Foreground = TryFindBrush("Brush.Warning", "#FFD60A");
                IconText.Text = "\uE7BA"; // Warning triangle
                break;

            case ModernDialogType.Success:
                IconBadge.Background = TryFindBrush("Brush.SuccessSoft", "#2530D158");
                IconText.Foreground = TryFindBrush("Brush.Success", "#30D158");
                IconText.Text = "\uE73E"; // Checkmark
                break;

            case ModernDialogType.Question:
                if (isDestructive)
                {
                    IconBadge.Background = TryFindBrush("Brush.DangerSoft", "#25FF453A");
                    IconText.Foreground = TryFindBrush("Brush.Danger", "#FF453A");
                    IconText.Text = "\uE74D"; // Trash / Danger icon
                }
                else
                {
                    IconBadge.Background = TryFindBrush("Brush.AccentSoft", "#250A84FF");
                    IconText.Foreground = TryFindBrush("Brush.Accent", "#0A84FF");
                    IconText.Text = "\uE897"; // Question
                }
                break;

            case ModernDialogType.Information:
            default:
                IconBadge.Background = TryFindBrush("Brush.AccentSoft", "#250A84FF");
                IconText.Foreground = TryFindBrush("Brush.Accent", "#0A84FF");
                IconText.Text = "\uE946"; // Info
                break;
        }
    }

    private Brush TryFindBrush(string resourceKey, string hexFallback)
    {
        if (TryFindResource(resourceKey) is Brush brush) return brush;
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(hexFallback)!;
        }
        catch
        {
            return Brushes.DodgerBlue;
        }
    }

    private void ConfigureButtons(
        ModernDialogButtons buttons,
        string? confirmText,
        string? cancelText,
        bool isDestructive)
    {
        if (isDestructive && TryFindResource("Button.Danger") is Style dangerStyle)
        {
            ConfirmBtn.Style = dangerStyle;
        }

        switch (buttons)
        {
            case ModernDialogButtons.Ok:
                CancelBtn.Visibility = Visibility.Collapsed;
                ConfirmBtn.Content = !string.IsNullOrWhiteSpace(confirmText)
                    ? confirmText
                    : Loc.Get("L.Dialog.Ok");
                break;

            case ModernDialogButtons.YesNo:
                CancelBtn.Visibility = Visibility.Visible;
                CancelBtn.Content = !string.IsNullOrWhiteSpace(cancelText)
                    ? cancelText
                    : Loc.Get("L.Dialog.No");
                ConfirmBtn.Content = !string.IsNullOrWhiteSpace(confirmText)
                    ? confirmText
                    : Loc.Get("L.Dialog.Yes");
                break;

            case ModernDialogButtons.OkCancel:
            default:
                CancelBtn.Visibility = Visibility.Visible;
                CancelBtn.Content = !string.IsNullOrWhiteSpace(cancelText)
                    ? cancelText
                    : Loc.Get("L.Cancel");
                ConfirmBtn.Content = !string.IsNullOrWhiteSpace(confirmText)
                    ? confirmText
                    : Loc.Get("L.Dialog.Ok");
                break;
        }
    }

    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResultType = _buttons == ModernDialogButtons.YesNo
            ? ModernDialogResult.Yes
            : ModernDialogResult.Ok;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResultType = _buttons == ModernDialogButtons.YesNo
            ? ModernDialogResult.No
            : ModernDialogResult.Cancel;
        DialogResult = false;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResultType = _buttons == ModernDialogButtons.YesNo
            ? ModernDialogResult.No
            : ModernDialogResult.Cancel;
        DialogResult = false;
        Close();
    }
}
