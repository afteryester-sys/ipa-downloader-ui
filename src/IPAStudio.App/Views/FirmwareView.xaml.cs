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

        vm.ConfirmResumePending = pending =>
        {
            var message = Loc.Format("L.Firmware.ResumePrompt", pending.Count);
            var title = Loc.Get("L.Firmware.ResumePromptTitle");
            var owner = Window.GetWindow(this);
            var result = owner is null
                ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        };

        Dispatcher.BeginInvoke(vm.OfferPendingResume);
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
