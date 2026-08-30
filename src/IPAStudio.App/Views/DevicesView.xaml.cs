using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using IPAStudio.App.Services;
using IPAStudio.App.ViewModels;
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

        var dialog = new QuickTransferDialog(
            device.Device,
            App.Services.GetRequiredService<FileSharingService>(),
            App.Services.GetRequiredService<OperationService>())
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
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
