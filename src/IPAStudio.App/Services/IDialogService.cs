using System.Threading.Tasks;
using System.Windows;

namespace IPAStudio.App.Services;

public enum ModernDialogType
{
    Information,
    Question,
    Warning,
    Danger,
    Error,
    Success,
}

public enum ModernDialogButtons
{
    Ok,
    OkCancel,
    YesNo,
}

public enum ModernDialogResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
}

/// <summary>
/// Unified in-app modern dialog service replacing native Win32/Windows 98 message boxes.
/// </summary>
public interface IDialogService
{
    ModernDialogResult Show(
        string message,
        string title = "",
        ModernDialogButtons buttons = ModernDialogButtons.Ok,
        ModernDialogType type = ModernDialogType.Information,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false);

    bool Confirm(
        string message,
        string title = "",
        ModernDialogType type = ModernDialogType.Question,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false);

    Task<ModernDialogResult> ShowAsync(
        string message,
        string title = "",
        ModernDialogButtons buttons = ModernDialogButtons.Ok,
        ModernDialogType type = ModernDialogType.Information,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false);

    Task<bool> ConfirmAsync(
        string message,
        string title = "",
        ModernDialogType type = ModernDialogType.Question,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDestructive = false);
}
