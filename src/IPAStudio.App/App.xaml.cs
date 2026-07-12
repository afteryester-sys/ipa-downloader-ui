using System.Net.Http;
using System.Windows;
using IPAStudio.App.Services;
using IPAStudio.App.ViewModels;
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
