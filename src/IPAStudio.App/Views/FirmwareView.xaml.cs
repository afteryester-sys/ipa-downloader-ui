using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;

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

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FirmwareDevice device) return;
        if (DataContext is not FirmwareViewModel vm) return;

        var row = FindVisualParent<Grid>(fe, "DeviceRowRoot");
        if (row is null)
        {
            vm.RemoveDevice(device);
            return;
        }

        var sb = new Storyboard();

        var fadeOut = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fadeOut, row);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath("Opacity"));
        sb.Children.Add(fadeOut);

        var slideOut = new DoubleAnimation(0.0, -26.0, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(slideOut, row);
        Storyboard.SetTargetProperty(slideOut, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)"));
        sb.Children.Add(slideOut);

        var collapse = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            BeginTime = TimeSpan.FromMilliseconds(40),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(collapse, row);
        Storyboard.SetTargetProperty(collapse, new PropertyPath("(UIElement.LayoutTransform).(ScaleTransform.ScaleY)"));
        sb.Children.Add(collapse);

        sb.Completed += (_, _) =>
        {
            vm.RemoveDevice(device);
        };
        sb.Begin();
    }

    private static T? FindVisualParent<T>(DependencyObject? child, string? name = null) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed && (name is null || (child is FrameworkElement fe && fe.Name == name)))
                return typed;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
