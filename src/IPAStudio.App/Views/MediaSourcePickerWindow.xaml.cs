using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Win32;

namespace IPAStudio.App.Views;

/// <summary>
/// Source device and folder picker for Media Export.
/// Supports MTP Android phones, USB flash drives, external hard drives, local disks,
/// and custom folder browsing.
/// </summary>
public partial class MediaSourcePickerWindow : Window
{
    private readonly string _currentSource;

    public MediaExportSourceDevice? SelectedSource { get; private set; }

    public MediaSourcePickerWindow(string? currentSource = null)
    {
        InitializeComponent();
        _currentSource = currentSource ?? "";

        Loaded += async (_, _) => await LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            PhonesPanel.Children.Clear();
            DrivesPanel.Children.Clear();

            var sources = await Task.Run(() => MediaExportService.GetAvailableSources(Loc.Get));

            var phones = sources.Where(s => s.IsPhone).ToList();
            var drives = sources.Where(s => !s.IsPhone).ToList();

            // Populate phones
            if (phones.Count > 0)
            {
                NoPhonesCard.Visibility = Visibility.Collapsed;
                foreach (var phone in phones)
                {
                    PhonesPanel.Children.Add(CreateDeviceButton(phone, isPhone: true));
                }
            }
            else
            {
                NoPhonesCard.Visibility = Visibility.Visible;
            }

            // Populate drives
            foreach (var drive in drives)
            {
                DrivesPanel.Children.Add(CreateDeviceButton(drive, isPhone: false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media source picker: failed to load devices: {ex.Message}");
        }
    }

    private Button CreateDeviceButton(MediaExportSourceDevice device, bool isPhone)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("DeviceRowButton"),
            Tag = device
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon badge
        var badge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = isPhone
                ? (Brush)FindResource("Brush.AccentSoft")
                : (Brush)FindResource("Brush.SurfaceHover"),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconText = new TextBlock
        {
            Text = device.IconGlyph ?? (isPhone ? "\uE8EA" : "\uEDA2"),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = isPhone
                ? (Brush)FindResource("Brush.Accent")
                : (Brush)FindResource("Brush.Text"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = iconText;
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        // Text StackPanel
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock
        {
            Text = device.Name,
            Style = (Style)FindResource("Text.Body"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5
        };
        textStack.Children.Add(nameText);

        if (!string.IsNullOrWhiteSpace(device.Subtitle) || !string.IsNullOrWhiteSpace(device.Details))
        {
            var subtitleText = new TextBlock
            {
                Text = !string.IsNullOrWhiteSpace(device.Details)
                    ? $"{device.Subtitle} • {device.Details}"
                    : device.Subtitle,
                Style = (Style)FindResource("Text.Caption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            textStack.Children.Add(subtitleText);
        }

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        // Action indicator / chevron
        var actionBtn = new Border
        {
            Background = isPhone ? (Brush)FindResource("Brush.Accent") : (Brush)FindResource("Brush.SurfaceHover"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var actionLabel = new TextBlock
        {
            Text = Loc.Get("L.MediaExport.Select"),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = isPhone ? Brushes.White : (Brush)FindResource("Brush.Text")
        };
        actionBtn.Child = actionLabel;
        Grid.SetColumn(actionBtn, 2);
        grid.Children.Add(actionBtn);

        btn.Content = grid;
        btn.Click += (_, _) =>
        {
            SelectedSource = device;
            DialogResult = true;
            Close();
        };

        return btn;
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("L.MediaExport.PickSourceTitle"),
            InitialDirectory = Directory.Exists(_currentSource) ? _currentSource : ""
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            SelectedSource = new MediaExportSourceDevice(
                Name: Path.GetFileName(dialog.FolderName) is { Length: > 0 } n ? n : dialog.FolderName,
                Path: dialog.FolderName,
                SourceType: MediaExportSourceType.CustomFolder,
                Subtitle: Loc.Get("L.MediaExport.Device.CustomFolder"),
                Details: dialog.FolderName,
                IconGlyph: "\uED25",
                IsPhone: false);

            DialogResult = true;
            Close();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await LoadDevicesAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
