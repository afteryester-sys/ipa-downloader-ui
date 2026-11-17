using System;
using System.Threading.Tasks;
using System.Windows;
using IPAStudio.App.Views;

namespace IPAStudio.App.Services;

public class ModernDialogService : IDialogService
{
    public ModernDialogResult Show(
        string message,
        string title = "",
        ModernDialogButtons buttons = ModernDialogButtons.Ok,
        ModernDialogType type = ModernDialogType.Information,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() => Show(message, title, buttons, type, owner, confirmText, cancelText, isDestructive));
        }

        owner ??= GetActiveWindow();

        MainWindow? mw = owner as MainWindow ?? app?.MainWindow as MainWindow;
        bool dimmerShown = false;
        if (mw != null && mw.IsVisible)
        {
            dimmerShown = true;
            mw.ShowDimmer();
        }

        try
        {
            var dialog = new ModernDialogWindow(title, message, buttons, type, confirmText, cancelText, isDestructive)
            {
                Owner = owner
            };
            dialog.ShowDialog();
            return dialog.DialogResultType;
        }
        finally
        {
            if (dimmerShown && mw != null)
            {
                mw.HideDimmer();
            }
        }
    }

    public bool Confirm(
        string message,
        string title = "",
        ModernDialogType type = ModernDialogType.Question,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        var result = Show(message, title, ModernDialogButtons.YesNo, type, owner, confirmText, cancelText, isDestructive);
        return result is ModernDialogResult.Yes or ModernDialogResult.Ok;
    }

    public Task<ModernDialogResult> ShowAsync(
        string message,
        string title = "",
        ModernDialogButtons buttons = ModernDialogButtons.Ok,
        ModernDialogType type = ModernDialogType.Information,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        var app = Application.Current;
        if (app == null)
        {
            return Task.FromResult(Show(message, title, buttons, type, owner, confirmText, cancelText, isDestructive));
        }

        return app.Dispatcher.InvokeAsync(() => Show(message, title, buttons, type, owner, confirmText, cancelText, isDestructive)).Task;
    }

    public Task<bool> ConfirmAsync(
        string message,
        string title = "",
        ModernDialogType type = ModernDialogType.Question,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        var app = Application.Current;
        if (app == null)
        {
            return Task.FromResult(Confirm(message, title, type, owner, confirmText, cancelText, isDestructive));
        }

        return app.Dispatcher.InvokeAsync(() => Confirm(message, title, type, owner, confirmText, cancelText, isDestructive)).Task;
    }

    private static Window? GetActiveWindow()
    {
        var app = Application.Current;
        if (app == null) return null;

        foreach (Window window in app.Windows)
        {
            if (window.IsActive) return window;
        }

        return app.MainWindow?.IsVisible == true ? app.MainWindow : null;
    }
}

/// <summary>
/// Static convenience facade for showing modern in-app dialogs.
/// </summary>
public static class ModernDialog
{
    public static IDialogService Instance { get; set; } = new ModernDialogService();

    public static ModernDialogResult Show(
        string message,
        string title = "",
        ModernDialogButtons buttons = ModernDialogButtons.Ok,
        ModernDialogType type = ModernDialogType.Information,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        return Instance.Show(message, title, buttons, type, owner, confirmText, cancelText, isDestructive);
    }

    public static bool Confirm(
        string message,
        string title = "",
        ModernDialogType type = ModernDialogType.Question,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        return Instance.Confirm(message, title, type, owner, confirmText, cancelText, isDestructive);
    }

    public static Task<ModernDialogResult> ShowAsync(
        string message,
        string title = "",
        ModernDialogButtons buttons = ModernDialogButtons.Ok,
        ModernDialogType type = ModernDialogType.Information,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        return Instance.ShowAsync(message, title, buttons, type, owner, confirmText, cancelText, isDestructive);
    }

    public static Task<bool> ConfirmAsync(
        string message,
        string title = "",
        ModernDialogType type = ModernDialogType.Question,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false)
    {
        return Instance.ConfirmAsync(message, title, type, owner, confirmText, cancelText, isDestructive);
    }
}
