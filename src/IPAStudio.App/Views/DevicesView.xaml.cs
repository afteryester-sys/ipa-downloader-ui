using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using IPAStudio.App.Services;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IPAStudio.App.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
        Loaded += (_, _) => StartPulseAnimation();
    }

    private void OnQuickTransferClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceViewModel device }) return;
        OpenQuickTransfer(device.Device, null);
    }

    public void OpenQuickTransfer(Device device, IEnumerable<string>? initialFiles = null)
    {
        var dialog = new QuickTransferDialog(
            device,
            App.Services.GetRequiredService<FileSharingService>(),
            App.Services.GetRequiredService<OperationService>(),
            App.Services.GetService<PhotoService>(),
            App.Services.GetService<QuickTransferService>(),
            initialFiles)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    private void OnDeviceCardDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDeviceCardDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (sender is FrameworkElement { Tag: DeviceViewModel dvm })
        {
            OpenQuickTransfer(dvm.Device, paths);
            e.Handled = true;
        }
    }

    private void OnViewDragOver(object sender, DragEventArgs e)
    {
        var vm = DataContext as DevicesViewModel;
        var hasDevices = vm != null && vm.Devices.Count > 0;
        e.Effects = (hasDevices && e.Data.GetDataPresent(DataFormats.FileDrop))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnViewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var vm = DataContext as DevicesViewModel;
        var targetDevice = vm?.Devices.FirstOrDefault()?.Device;
        if (targetDevice != null)
        {
            OpenQuickTransfer(targetDevice, paths);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Two expanding rings around the phone outline, offset by half a period,
    /// signalling "waiting for a device to connect".
    /// </summary>
    private void StartPulseAnimation()
    {
        AnimateRing(PulseRing1, TimeSpan.Zero);
        AnimateRing(PulseRing2, TimeSpan.FromSeconds(1.25));
    }

    private static void AnimateRing(Ellipse ring, TimeSpan delay)
    {
        var duration = TimeSpan.FromSeconds(2.5);

        var grow = new DoubleAnimation(110, 160, duration)
        {
            BeginTime = delay,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        var fade = new DoubleAnimation(0.7, 0, duration)
        {
            BeginTime = delay,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        ring.BeginAnimation(WidthProperty, grow);
        ring.BeginAnimation(HeightProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }
}
