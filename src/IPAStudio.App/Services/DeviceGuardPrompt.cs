using System.Windows;
using IPAStudio.App.Views;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Services;

/// <summary>
/// The one place an action asks whether it may touch a device.
///
/// Every guarded action calls <see cref="Allow"/> and stops when it returns false. Keeping the
/// check in a single helper is what makes "asks every time" true by construction: there is no
/// second copy of the logic that could quietly remember a previous answer, and adding a new
/// action means adding one call rather than reimplementing a gate.
///
/// Static, matching how the other view models open their dialogs directly, so an action deep
/// inside a command does not need a new constructor parameter threaded down to it.
/// </summary>
internal static class DeviceGuardPrompt
{
    /// <summary>
    /// Returns true when the action may proceed: either the device is not guarded, or the correct
    /// password was just entered.
    ///
    /// Unguarded devices return immediately without showing anything — the guard is about one
    /// known phone, and every other device must behave exactly as it did before.
    /// </summary>
    /// <param name="actionKey">
    /// Localization key naming the action ("L.Guard.Action.Download" and friends), shown in the
    /// prompt so an unexpected request is recognizable as one.
    /// </param>
    public static bool Allow(DeviceGuardService guard, Device? device, string actionKey)
    {
        if (device is null || !guard.IsGuarded(device)) return true;

        // Owned by the active window so the prompt cannot appear behind it — this dialog blocks
        // the action, and a hidden blocker reads as a freeze.
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

        var dialog = new DeviceUnlockDialog(guard, device, Loc.Get(actionKey)) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Unlocked;
    }
}
