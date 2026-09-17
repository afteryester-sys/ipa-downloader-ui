using System.Windows.Controls;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // The load reading samples the process list every second, so it is tied to this page
        // being on screen rather than to the view model, which is a singleton and outlives it.
        // Unloaded fires when the user navigates away, which is exactly when measuring should
        // stop — otherwise the diagnostic meant to reveal idle CPU use would be causing it.
        Loaded += (_, _) => (DataContext as SettingsViewModel)?.StartLoadMonitor();
        Unloaded += (_, _) => (DataContext as SettingsViewModel)?.StopLoadMonitor();
    }
}
