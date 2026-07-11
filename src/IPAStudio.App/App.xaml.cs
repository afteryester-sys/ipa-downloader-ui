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
        services.AddSingleton<ProcessRunner>();
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
        services.AddSingleton<DownloadService>();
        services.AddSingleton<InstallService>();
        services.AddSingleton<QueueService>();
        services.AddSingleton<DependencyService>();
        services.AddSingleton<UpdateService>();

        // App
        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<AppPickerViewModel>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<SettingsViewModel>();

        Services = services.BuildServiceProvider();

        // Load settings and apply language before showing the window.
        var settings = Services.GetRequiredService<SettingsService>();
        settings.Load();
        Services.GetRequiredService<LocalizationManager>().Apply(settings.Current.Language);

        var window = new MainWindow
        {
            DataContext = Services.GetRequiredService<ShellViewModel>(),
        };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (Services.GetService<DeviceService>() as IAsyncDisposable)
            ?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
