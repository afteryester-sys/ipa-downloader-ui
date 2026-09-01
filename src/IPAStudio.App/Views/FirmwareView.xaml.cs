using System.Windows;
using System.Windows.Controls;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Localization;

namespace IPAStudio.App.Views;

public partial class FirmwareView : UserControl
{
    public FirmwareView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Supplies the ViewModel with the confirmation dialog it needs when unfinished
    /// downloads are found at startup. The prompt lives here so the ViewModel stays
    /// free of WPF dialog calls and remains testable.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not FirmwareViewModel vm) return;

        vm.ConfirmResumePending = pending => MessageBox.Show(
            Window.GetWindow(this),
            Loc.Format("L.Firmware.ResumePrompt", pending.Count),
            Loc.Get("L.Firmware.ResumePromptTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void AddDevices_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FirmwareViewModel vm) return;
        var dialog = new FirmwareDevicePickerWindow(vm.AllDevices, vm.MyDevices.Select(d => d.Identifier))
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true) vm.AddDevices(dialog.SelectedDevices);
    }
}
