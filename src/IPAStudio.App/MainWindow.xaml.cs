using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Localization;

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
        shell.OperationMinimized += (_, _) => PlayMinimizeAnimation();

        // The password box can't bind to RollbackPasswordInput (WPF keeps Password
        // non-bindable on purpose), so once the view model clears it — on a successful
        // unlock or an explicit lock — the box itself needs to be wiped by hand.
        shell.Updater.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModels.UpdaterViewModel.RollbackPasswordInput)
                && shell.Updater.RollbackPasswordInput.Length == 0)
                RollbackPasswordBox.Password = "";
        };

        // Begin looking for updates on our own. Started from the window rather than the
        // view model's constructor so the timer belongs to the UI thread that will run its
        // ticks; the call is idempotent, which this hook needs since it can run again.
        shell.Updater.StartAutoCheck();
    }

    /// <summary>
    /// Bounces the corner circle when an operation drops into it.
    ///
    /// The circle is what the operation collapses into, so drawing attention there is what
    /// tells the user where the work went — otherwise a minimise looks like the work was
    /// simply cancelled.
    /// </summary>
    private void PlayMinimizeAnimation()
    {
        var pop = new DoubleAnimationUsingKeyFrames();
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));

        OpsCircleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
        OpsCircleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>
    /// Confirms closing while operations are still running.
    ///
    /// Worth interrupting for: a minimised operation is out of sight by design, and closing
    /// the window part-way through an install onto a device is the worst moment to cut the
    /// work off.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            var unfinished = shell.Operations.Unfinished;
            if (unfinished.Count > 0)
            {
                var list = string.Join("\n", unfinished.Select(o => $"  • {o.Title} — {o.Subtitle}"));
                var body = $"{Loc.Get("L.Ops.ExitBody")}\n\n{list}";

                var answer = MessageBox.Show(
                    this, body, Loc.Get("L.Ops.ExitTitle"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

                if (answer != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }

                // Confirmed: stop the work deliberately instead of letting process exit tear
                // it down mid-transfer.
                shell.Operations.CancelAll();
            }
        }

        base.OnClosing(e);
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

    private void OpenMediaExport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.Updater.IsOpen = false;
        shell.GoTo(Page.MediaExport);
    }

    /// <summary>
    /// Feeds the rollback password box into the Updater view model. PasswordBox.Password is
    /// deliberately not bindable in WPF (so it can't be dumped to a binding trace or a
    /// crash log), so this is the standard code-behind bridge instead of a Binding.
    /// </summary>
    private void RollbackPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (sender is System.Windows.Controls.PasswordBox box)
            shell.Updater.RollbackPasswordInput = box.Password;
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
