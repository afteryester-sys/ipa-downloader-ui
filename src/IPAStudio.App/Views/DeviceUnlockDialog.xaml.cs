using System.Windows;
using System.Windows.Input;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Views;

/// <summary>
/// Asks for the password that unlocks a guarded device, once per action.
///
/// Code-behind for the same reason FileConflictDialog is: a modal prompt with one field and two
/// buttons, opened from wherever an action starts, with nothing worth binding.
///
/// A wrong answer keeps the dialog open and clears the field rather than closing with a failure,
/// because the common case is a typo and the alternative is starting the whole action again.
/// </summary>
public partial class DeviceUnlockDialog : Window
{
    private readonly DeviceGuardService _guard;

    /// <summary>
    /// True only when the correct password was entered and confirmed. Defaults to false, so
    /// closing the window by any other route — Escape, Alt+F4, the title bar — denies the action.
    /// </summary>
    public bool Unlocked { get; private set; }

    /// <param name="actionName">
    /// What is about to happen, named in the prompt. A password box on its own gives no way to
    /// tell an expected request from one triggered by a misclick.
    /// </param>
    public DeviceUnlockDialog(DeviceGuardService guard, Device device, string actionName)
    {
        _guard = guard;
        InitializeComponent();

        ActionLine.Text = Loc.Format("L.Guard.ActionLine", actionName);
        DeviceLine.Text = string.IsNullOrWhiteSpace(device.Name)
            ? Loc.Get("L.Guard.UnknownDevice")
            : device.Name;
        SerialLine.Text = Loc.Format(
            "L.Guard.Serial",
            string.IsNullOrWhiteSpace(device.SerialNumber) ? "—" : device.SerialNumber);

        // Focused on open so the password can be typed straight away: this dialog appears on
        // every single action against the device, and a click into the field each time would
        // make routine work tiring.
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Attempt();

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        // Enter is handled by IsDefault on the confirm button; this catches the numeric-pad
        // Enter, which does not always reach it.
        if (e.Key == Key.Enter)
        {
            Attempt();
            e.Handled = true;
        }
    }

    private void Attempt()
    {
        if (_guard.Verify(PasswordInput.Password))
        {
            Unlocked = true;
            DialogResult = true;
            return;
        }

        ErrorLine.Visibility = Visibility.Visible;
        PasswordInput.Clear();
        PasswordInput.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Unlocked = false;
        DialogResult = false;
    }
}
