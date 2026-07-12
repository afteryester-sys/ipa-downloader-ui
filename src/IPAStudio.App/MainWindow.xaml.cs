using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = BuildTitle();
        DataContextChanged += (_, _) => HookShell();
    }

    /// <summary>"IPA Studio 1.1.0" — app name plus the running assembly version.</summary>
    private static string BuildTitle()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        var version = v is null ? "" : $" {v.Major}.{v.Minor}.{v.Build}";
        return $"IPA Studio{version}";
    }

    private void HookShell()
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.PropertyChanged += OnShellPropertyChanged;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentViewModel)) return;

        // Fade + slide-up transition on every page change.
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        PageHost.BeginAnimation(OpacityProperty, fade);
        PageTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    // ---- Developer credit popup ----

    private void CreditButton_Click(object sender, RoutedEventArgs e)
        => ContactPopup.IsOpen = !ContactPopup.IsOpen;

    private void ContactEmail_Click(object sender, RoutedEventArgs e)
    {
        ContactPopup.IsOpen = false;
        OpenUrl("mailto:leq77751@gmail.com");
    }

    private void ContactTelegram_Click(object sender, RoutedEventArgs e)
    {
        ContactPopup.IsOpen = false;
        OpenUrl("https://t.me/alfredyester");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Silently ignore — can't open browser.
        }
    }
}
