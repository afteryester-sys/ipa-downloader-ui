using System.Windows;
using System.Windows.Controls;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App.Views;

public partial class FirmwareView : UserControl
{
    public FirmwareView() => InitializeComponent();

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
