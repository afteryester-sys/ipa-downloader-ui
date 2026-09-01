using System.Windows;
using System.Windows.Controls;
using IPAStudio.Core.Models;

namespace IPAStudio.App.Views;

public partial class FirmwareDevicePickerWindow : Window
{
    private readonly IReadOnlyList<FirmwareDevice> _devices;
    public IReadOnlyList<FirmwareDevice> SelectedDevices { get; private set; } = Array.Empty<FirmwareDevice>();

    public FirmwareDevicePickerWindow(IEnumerable<FirmwareDevice> devices, IEnumerable<string> excludedIdentifiers)
    {
        InitializeComponent();
        var excluded = excludedIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _devices = devices.Where(d => !excluded.Contains(d.Identifier)).OrderBy(d => d.Name).ThenBy(d => d.Identifier).ToList();
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (DevicesList is null) return;
        var query = SearchBox?.Text.Trim() ?? "";
        DevicesList.ItemsSource = _devices.Where(d => string.IsNullOrEmpty(query) ||
            d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            d.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedDevices = DevicesList.SelectedItems.Cast<FirmwareDevice>().ToList();
        if (SelectedDevices.Count == 0) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
