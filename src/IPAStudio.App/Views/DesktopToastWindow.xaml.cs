using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IPAStudio.App.Views;

public partial class DesktopToastWindow : Window
{
    private static DesktopToastWindow? _activeToast;
    private readonly Action? _clickAction;
    private DispatcherTimer? _dismissTimer;
    private bool _isDismissing;

    public DesktopToastWindow(string title, string message, string icon = "✓", Action? clickAction = null)
    {
        InitializeComponent();
        _clickAction = clickAction;

        TitleBlock.Text = title;
        MessageBlock.Text = message;
        ApplyIconStyle(icon);

        // Copy merged dictionaries to ensure custom brush keys resolve
        if (Application.Current?.Resources.MergedDictionaries != null)
        {
            foreach (var dict in Application.Current.Resources.MergedDictionaries)
            {
                Resources.MergedDictionaries.Add(dict);
            }
        }

        Loaded += OnLoaded;
    }

    private void ApplyIconStyle(string icon)
    {
        string glyph;
        string hexColor;

        switch (icon?.ToLowerInvariant())
        {
            case "🔄":
            case "update":
                glyph = "\uE895";
                hexColor = "#3B82F6"; // Accent blue
                break;
            case "📱":
            case "device":
                glyph = "\uE8EA";
                hexColor = "#8B5CF6"; // Purple
                break;
            case "📥":
            case "download":
                glyph = "\uE896";
                hexColor = "#06B6D4"; // Cyan
                break;
            case "⚠":
            case "warning":
                glyph = "\uE7BA";
                hexColor = "#F59E0B"; // Amber
                break;
            case "✕":
            case "error":
                glyph = "\uEA39";
                hexColor = "#EF4444"; // Red
                break;
            case "📷":
            case "photos":
                glyph = "\uEB9F";
                hexColor = "#0EA5E9"; // Sky blue
                break;
            case "⚙":
            case "firmware":
                glyph = "\uE713";
                hexColor = "#6366F1"; // Indigo
                break;
            default:
                glyph = "\uE73E";
                hexColor = "#10B981"; // Emerald
                break;
        }

        IconGlyph.Text = glyph;
        if (ColorConverter.ConvertFromString(hexColor) is Color color)
        {
            IconGlyph.Foreground = new SolidColorBrush(color);
            var bg = Color.FromArgb(0x28, color.R, color.G, color.B);
            IconCircle.Background = new SolidColorBrush(bg);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionInWorkArea();

        // Slide up and fade in
        var fadeIn = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var slideUp = new DoubleAnimation
        {
            From = 35.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new BackEase { Amplitude = 0.15, EasingMode = EasingMode.EaseOut }
        };

        ToastBorder.BeginAnimation(OpacityProperty, fadeIn);
        ToastTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        StartDismissTimer(TimeSpan.FromSeconds(5.0));
    }

    private void PositionInWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 20;
        Top = workArea.Bottom - ActualHeight - 20;
    }

    private void StartDismissTimer(TimeSpan interval)
    {
        _dismissTimer?.Stop();
        _dismissTimer = new DispatcherTimer { Interval = interval };
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            Dismiss();
        };
        _dismissTimer.Start();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _dismissTimer?.Stop();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDismissing)
        {
            StartDismissTimer(TimeSpan.FromSeconds(2.5));
        }
    }

    private void OnToastClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        _clickAction?.Invoke();
        Dismiss();
    }

    private void OnDismissClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    public void Dismiss()
    {
        if (_isDismissing) return;
        _isDismissing = true;
        _dismissTimer?.Stop();

        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var slideDown = new DoubleAnimation
        {
            To = 30.0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            if (_activeToast == this) _activeToast = null;
            Close();
        };

        ToastBorder.BeginAnimation(OpacityProperty, fadeOut);
        ToastTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
    }

    /// <summary>
    /// Displays a floating desktop toast in the bottom-right corner of the primary screen.
    /// Safely updates or closes any previous toast.
    /// </summary>
    public static void ShowToast(string title, string message, string icon = "✓", Action? onClick = null)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_activeToast != null)
                {
                    _activeToast.Dismiss();
                    _activeToast = null;
                }

                var toast = new DesktopToastWindow(title, message, icon, onClick);
                _activeToast = toast;
                toast.Show();
            }
            catch (Exception ex)
            {
                IPAStudio.Core.Diagnostics.AppLog.Warn($"Desktop toast failed to display: {ex.Message}");
            }
        });
    }
}
