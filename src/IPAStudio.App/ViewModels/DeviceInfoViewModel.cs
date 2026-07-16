using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

/// <summary>A single label/value row shown on the device information screen.</summary>
public sealed class DeviceInfoRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// Read-only detail screen for a connected device. Shows the associated Apple ID
/// (best effort) plus hardware and storage details read via ideviceinfo. Extra
/// details (disk usage, Apple ID) are fetched lazily when the page opens.
/// </summary>
public sealed partial class DeviceInfoViewModel : ObservableObject, IPageAware
{
    private readonly DeviceService _devices;
    private readonly AuthService _auth;
    private INavigator? _navigator;

    private Device? _device;

    [ObservableProperty]
    private string _deviceName = "";

    [ObservableProperty]
    private string _modelLine = "";

    [ObservableProperty]
    private string _appleId = "";

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Storage usage as a 0-100 percentage, or -1 when unknown.</summary>
    [ObservableProperty]
    private double _storageUsedPercent = -1;

    [ObservableProperty]
    private string _storageSummary = "";

    /// <summary>Battery charge level 0-100; -1 = unknown.</summary>
    [ObservableProperty]
    private int _batteryLevel = -1;

    /// <summary>True when battery level is known (>= 0), so the card is shown.</summary>
    public bool BatteryVisible => BatteryLevel >= 0;

    partial void OnBatteryLevelChanged(int value) => OnPropertyChanged(nameof(BatteryVisible));

    /// <summary>Placeholder for future charging state; false until ideviceinfo reports it.</summary>
    [ObservableProperty]
    private bool _batteryCharging;

    public ObservableCollection<DeviceInfoRow> Rows { get; } = new();

    public DeviceInfoViewModel(DeviceService devices, AuthService auth)
    {
        _devices = devices;
        _auth = auth;
    }

    public void SetDevice(Device device) => _device = device;

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        if (_device is null) return;

        Render();
        _ = LoadExtraAsync();
    }

    private async Task LoadExtraAsync()
    {
        if (_device is null) return;
        IsLoading = true;
        try
        {
            await _devices.EnrichInfoAsync(_device);
        }
        catch { /* show whatever we already have */ }
        finally
        {
            IsLoading = false;
            Render();
        }
    }

    private void Render()
    {
        if (_device is null) return;

        DeviceName = _device.Name;
        ModelLine = string.IsNullOrEmpty(_device.Model)
            ? $"iOS {_device.OsVersion}"
            : $"{_device.Model} · iOS {_device.OsVersion}";

        // Apple ID: prefer what the device exposes, then the signed-in account.
        // Modern iOS (14+) hides the account Apple ID from lockdown queries for
        // privacy, so when neither source has a value we say so plainly instead of
        // leaving the field blank (which looked like a bug).
        AppleId = !string.IsNullOrWhiteSpace(_device.AppleId)
            ? _device.AppleId!
            : !string.IsNullOrWhiteSpace(_auth.CurrentAccount?.Email)
                ? _auth.CurrentAccount!.Email
                : IsLoading
                    ? "Определение…"
                    : "Недоступно (iOS скрывает Apple ID)";

        StorageSummary = FormatStorage(_device);
        StorageUsedPercent = _device is { TotalDiskCapacity: > 0, FreeDiskSpace: >= 0 }
            ? Math.Clamp(100.0 * (_device.TotalDiskCapacity - _device.FreeDiskSpace) / _device.TotalDiskCapacity, 0, 100)
            : -1;

        // Battery card
        BatteryLevel = _device.BatteryLevel;

        Rows.Clear();
        // Always show the battery-capacity row, even when the value can't be read,
        // so the item is present in the list (with an honest fallback string).
        Rows.Add(new DeviceInfoRow { Label = "Емкость аккумулятора", Value = FormatBatteryHealth(_device) });
        AddRow("Модель", _device.Model);
        AddRow("Идентификатор модели", _device.ProductType);
        AddRow("Версия iOS", _device.OsVersion);
        AddRow("Сборка", _device.BuildVersion);
        AddRow("Тип устройства", _device.DeviceClass);
        AddRow("Серийный номер", _device.SerialNumber);
        AddRow("UDID", _device.Udid);
        AddRow("Номер телефона", _device.PhoneNumber);
        AddRow("Регион", _device.RegionInfo);
        AddRow("Wi-Fi адрес", _device.WifiAddress);
        AddRow("Bluetooth адрес", _device.BluetoothAddress);
    }

    private void AddRow(string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Rows.Add(new DeviceInfoRow { Label = label, Value = value });
    }

    /// <summary>
    /// Formats the remaining battery capacity (iOS "Maximum Capacity") plus cycle
    /// count when available, e.g. "89% · 320 циклов". Empty when nothing was read.
    /// </summary>
    private string FormatBatteryHealth(Device device)
    {
        var parts = new List<string>(2);
        if (device.BatteryHealthPercent >= 0)
            parts.Add($"{device.BatteryHealthPercent}%");
        if (device.BatteryCycleCount >= 0)
            parts.Add($"{device.BatteryCycleCount} циклов");

        if (parts.Count > 0)
            return string.Join(" · ", parts);

        // No data yet: distinguish "still reading" from "device didn't report it".
        return IsLoading ? "Определение…" : "Недоступно";
    }

    private static string FormatStorage(Device device)
    {
        if (device.TotalDiskCapacity <= 0) return "";
        var total = FormatBytes(device.TotalDiskCapacity);
        if (device.FreeDiskSpace < 0) return total;

        var used = FormatBytes(device.TotalDiskCapacity - device.FreeDiskSpace);
        var free = FormatBytes(device.FreeDiskSpace);
        return $"Занято {used} из {total} · свободно {free}";
    }

    private static string FormatBytes(long bytes)
    {
        // Apple reports storage in DECIMAL units (1 GB = 1000 MB) everywhere the
        // user sees it — iOS Settings, Finder and iTunes. Using binary units
        // (÷1024) here made a 128 GB iPhone read as "119 GB", which looked wrong.
        // Divide by 1000 so our numbers match what the device itself shows.
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        // Whole numbers for GB/TB (matches iOS), one decimal for smaller units.
        return unit >= 3 ? $"{value:0.#} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    [RelayCommand]
    private void Back() => _navigator?.GoTo(Page.Devices);
}
