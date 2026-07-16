using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using IPAStudio.App.Services;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Services;
using IPAStudio.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IPAStudio.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global safety net — installed FIRST so nothing can silently kill the
        // app. Any unhandled exception is logged with a full stack trace and
        // surfaced in a dialog instead of terminating the process.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var services = new ServiceCollection();

        // Core
        services.AddSingleton<ToolLocator>();
        // Job object first: every spawned tool is bound to it and killed on exit.
        services.AddSingleton<ProcessJobObject>();
        services.AddSingleton<ProcessRunner>(sp => new ProcessRunner(sp.GetRequiredService<ProcessJobObject>()));
        services.AddSingleton<HttpClient>(_ =>
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IPAStudio/1.0");
            return client;
        });
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<DeviceService>();
        services.AddSingleton<PhotoService>();
        services.AddSingleton<DownloadService>();
        services.AddSingleton<InstallService>();
        services.AddSingleton<QueueService>();
        services.AddSingleton<DependencyService>();
        services.AddSingleton<UpdateService>();

        // App
        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<UpdaterViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<AppPickerViewModel>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DeviceInfoViewModel>();
        services.AddSingleton<PhotosViewModel>();

        Services = services.BuildServiceProvider();

        // Load settings and apply language before showing the window.
        var settings = Services.GetRequiredService<SettingsService>();
        settings.Load();

        // Apply the saved color theme BEFORE any window is created. The palette
        // (Palette.Dark/Light) must be merged ahead of the styles dictionary so the
        // StaticResource brush references inside Theme.xaml resolve against it.
        ApplyTheme(settings.Current.Theme);

        Services.GetRequiredService<LocalizationManager>().Apply(settings.Current.Language);

        // Closing the main window (the X button) shuts the whole app down.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Also clean up if Windows is logging off / shutting down.
        SessionEnding += (_, _) => Cleanup();

        var window = new MainWindow
        {
            DataContext = Services.GetRequiredService<ShellViewModel>(),
        };
        window.Show();
    }

    /// <summary>
    /// Inserts the palette for the requested theme ("dark" or "light") followed by
    /// the shared styles dictionary at the front of the application resources, so
    /// every StaticResource brush reference resolves against the chosen palette.
    /// Called once at startup; changing the theme later requires an app restart.
    /// </summary>
    private void ApplyTheme(string? theme)
    {
        var isLight = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        var paletteName = isLight ? "Palette.Light.xaml" : "Palette.Dark.xaml";

        var palette = new ResourceDictionary
        {
            Source = new Uri($"Resources/{paletteName}", UriKind.Relative),
        };
        var styles = new ResourceDictionary
        {
            Source = new Uri("Resources/Theme.xaml", UriKind.Relative),
        };

        // Palette first (index 0), styles second (index 1) — order matters for
        // StaticResource resolution. Strings dictionary stays after them.
        Resources.MergedDictionaries.Insert(0, palette);
        Resources.MergedDictionaries.Insert(1, styles);
    }

    // ---------------------------------------------------- crash safety net --

    /// <summary>
    /// Handles exceptions raised on the UI thread. Marks them handled so the
    /// app keeps running, logs the full stack and tells the user where to find
    /// the details.
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI-thread exception (recovered).", e.Exception);
        e.Handled = true;
        ShowError(e.Exception);
    }

    /// <summary>Logs fatal exceptions from non-UI threads (cannot be recovered).</summary>
    private static void OnDomainUnhandledException(
        object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLog.Error("Unhandled background exception.", ex);
        else
            AppLog.Error($"Unhandled background error: {e.ExceptionObject}");
    }

    /// <summary>Observes faulted background tasks so they don't escalate.</summary>
    private void OnUnobservedTaskException(
        object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception (recovered).", e.Exception);
        e.SetObserved();
    }

    private static void ShowError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "Произошла ошибка, но приложение продолжит работу.\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                "Подробности сохранены в журнале (кнопка меню в правом верхнем углу → Журнал).",
                "IPA Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch { /* never let the error handler itself crash */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private bool _cleanedUp;

    /// <summary>
    /// Stops all background work and terminates every spawned tool process so nothing
    /// keeps the (portable) application folder locked after the window is closed.
    /// </summary>
    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        try
        {
            // Stop device polling (bounded so a stuck poll can't block shutdown).
            if (Services.GetService<DeviceService>() is IAsyncDisposable disposable)
                disposable.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* best effort */ }

        try
        {
            // Kill every tracked child process and close the job object
            // (KILL_ON_JOB_CLOSE finishes off anything still running).
            Services.GetService<ProcessJobObject>()?.Dispose();
        }
        catch { /* best effort */ }
    }
}
