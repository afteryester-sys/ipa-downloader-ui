using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

    /// <summary>
    /// Opens the direct download page from the corner flyout.
    ///
    /// This is a Click handler rather than a Command binding because the flyout's
    /// DataContext is the Updater viewmodel, so a binding would look for the command
    /// on the wrong object and silently do nothing. The navigator lives on the shell.
    /// </summary>
    private void OpenDirectDownload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.Updater.IsOpen = false;
        shell.GoTo(Page.DirectDownload);
    }

    private void OpenICloud_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.Updater.IsOpen = false;
        shell.GoTo(Page.ICloud);
    }

    /// <summary>
    /// Opens the settings page. A Click handler for the same reason as the two above: the
    /// flyout's DataContext is the Updater, not the shell that owns the navigator.
    ///
    /// DevicesViewModel.OpenSettings could already do this, but nothing was ever bound to
    /// it, so Page.Settings had no caller anywhere in the app and the page could not be
    /// opened. Routing it from the flyout instead of a per-page button keeps it reachable
    /// from every page, including the ones a user lands on before any device is connected.
    /// </summary>
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.Updater.IsOpen = false;
        shell.GoTo(Page.Settings);
    }

    // ---- Developer credit popup ----

    private void CreditButton_Click(object sender, RoutedEventArgs e)
        => ContactPopup.IsOpen = !ContactPopup.IsOpen;

    private const string DeveloperEmail = "leq77751@gmail.com";

    /// <summary>
    /// Copies the address instead of launching it.
    ///
    /// This used to open "mailto:", which hands the address to whatever Windows has
    /// registered for the scheme. On a machine with no desktop mail client that is the
    /// browser, so clicking "write an email" appeared to do nothing but open a web page —
    /// the address itself never made it anywhere useful. Copying is the part the user
    /// actually wanted and behaves the same on every machine.
    ///
    /// The popup deliberately stays open: it is where the confirmation is shown, and
    /// closing it would hide the only evidence that the click did anything.
    /// </summary>
    private void ContactEmail_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DeveloperEmail);
        }
        catch
        {
            // Another process can hold the clipboard open; the address is on screen to be
            // copied by hand, so there is nothing worth interrupting the user over.
            return;
        }

        ShowEmailCopied();
    }

    /// <summary>Swaps the caption to a confirmation, then puts the original back.</summary>
    private void ShowEmailCopied()
    {
        EmailCaption.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "L.Contact.Copied");

        // A single timer instance is reused so that repeated clicks cannot stack several
        // pending restores, where the last one to fire could revert a newer confirmation.
        _emailCopiedReset ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _emailCopiedReset.Stop();
        _emailCopiedReset.Tick -= RestoreEmailCaption;
        _emailCopiedReset.Tick += RestoreEmailCaption;
        _emailCopiedReset.Start();
    }

    private DispatcherTimer? _emailCopiedReset;

    private void RestoreEmailCaption(object? sender, EventArgs e)
    {
        _emailCopiedReset?.Stop();
        EmailCaption.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "L.Contact.Email");
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
