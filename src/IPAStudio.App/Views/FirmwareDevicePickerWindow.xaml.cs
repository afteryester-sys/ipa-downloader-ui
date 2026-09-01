using System.Windows;
using System.Windows.Controls;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;

namespace IPAStudio.App.Views;

public partial class FirmwareDevicePickerWindow : Window
{
    private readonly IReadOnlyList<FirmwareDevice> _devices;

    public IReadOnlyList<FirmwareDevice> SelectedDevices { get; private set; } = Array.Empty<FirmwareDevice>();

    public FirmwareDevicePickerWindow(IEnumerable<FirmwareDevice> devices, IEnumerable<string> excludedIdentifiers)
    {
        InitializeComponent();

        // Devices already in the personal list are filtered out rather than shown disabled:
        // this dialog exists to add, so an entry that cannot be added is only noise.
        var excluded = excludedIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _devices = devices
            .Where(d => !excluded.Contains(d.Identifier))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (DevicesList is null) return;

        var query = SearchBox?.Text?.Trim() ?? "";
        DevicesList.ItemsSource = _devices
            .Where(d => query.Length == 0 ||
                        d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        d.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(600)
            .ToList();

        UpdateCount();
    }

    private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCount();

    /// <summary>
    /// Shows how many rows are listed and how many are picked, and keeps Add disabled while
    /// nothing is selected so the button never looks clickable without an effect.
    /// </summary>
    private void UpdateCount()
    {
        if (CountLabel is null || DevicesList is null) return;

        var shown = DevicesList.Items.Count;
        var picked = DevicesList.SelectedItems.Count;
        CountLabel.Text = Loc.Format("L.Firmware.PickerCount", shown, picked);

        if (AddButton is not null)
            AddButton.IsEnabled = picked > 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedDevices = DevicesList.SelectedItems.Cast<FirmwareDevice>().ToList();
        if (SelectedDevices.Count == 0) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
