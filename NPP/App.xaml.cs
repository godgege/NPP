using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Netch.App.Helpers;
using Netch.App.Services;
using Netch.App.ViewModels;
using Netch.App.Views;
using Netch.Controllers;
using Netch.Interfaces;
using Netch.Services;
using Netch.Utils;
using Serilog;

namespace Netch.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow MainWindow { get; private set; } = null!;

    public static UiLogSink? UiLogSink { get; private set; }

    private readonly NetchAppContext _appContext;
    private UiLogSink? _uiLogSink;
    private CancellationTokenSource? _activationListenerCts;

    public App()
    {
        InitializeComponent();

        _appContext = InitializeAppContext();
        NetchAppContext.Current = _appContext;

        Services = ConfigureServices(_appContext);

        InitializeLogging();
        UiLogSink = _uiLogSink;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await InitializeStartupStateAsync();
        }
        catch (Exception e)
        {
            Log.Fatal(e, "NPP startup initialization failed");
            Environment.Exit(-1);
            return;
        }

        MainWindow = new MainWindow();
        if (!Program.IsStartupLaunch)
            MainWindow.Activate();
        StartActivationListener();
        try
        {
            await MainWindow.RunStartupActionsAsync();
        }
        catch (Exception e)
        {
            Log.Error(e, "Startup actions failed");
            MainWindow.ShowNotification($"启动操作失败：{e.Message}", isInfo: false);
        }
    }

    private void StartActivationListener()
    {
        var cts = new CancellationTokenSource();
        _activationListenerCts = cts;
        MainWindow.Closed += (_, _) =>
        {
            cts.Cancel();
            _activationListenerCts = null;
        };

        _ = Task.Run(() =>
        {
            var handles = new[] { Program.ActivationEvent, cts.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
                MainWindow.DispatcherQueue.TryEnqueue(MainWindow.ActivateFromSecondaryInstance);
        });
    }

    private static NetchAppContext InitializeAppContext()
    {
        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        var dir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        var dataRoot = ResolveDataRoot(dir);

        Environment.CurrentDirectory = dir;
        Directory.CreateDirectory(dataRoot);
        MigrateWritableData(dir, dataRoot);

        return new NetchAppContext
        {
            NetchDir = dir,
            NetchExecutable = exePath,
            DataRoot = dataRoot
        };
    }

    private static string ResolveDataRoot(string applicationDirectory)
    {
        // Velopack portable builds run the real executable from <root>\current
        // and place a .portable marker in <root>. Keep portable settings beside
        // that stable root so an update can replace current without losing data.
        var parentDirectory = Directory.GetParent(applicationDirectory)?.FullName;
        if (parentDirectory is not null &&
            File.Exists(Path.Combine(parentDirectory, ".portable")))
        {
            return parentDirectory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetchX",
            "NPP");
    }

    private static void MigrateWritableData(string applicationDirectory, string dataRoot)
    {
        if (Path.GetFullPath(applicationDirectory)
            .Equals(Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var directoryName in new[] { "data", "logging", "mode" })
        {
            var source = Path.Combine(applicationDirectory, directoryName);
            var destination = Path.Combine(dataRoot, directoryName);
            if (!Directory.Exists(source))
                continue;

            try
            {
                foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(source, sourceFile);
                    var destinationFile = Path.Combine(destination, relativePath);
                    if (File.Exists(destinationFile))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                    File.Copy(sourceFile, destinationFile, overwrite: false);
                }
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"NPP data migration skipped for {directoryName}: {e.Message}");
            }
        }
    }

    private static IServiceProvider ConfigureServices(NetchAppContext appContext)
    {
        var services = new ServiceCollection();

        // Core context
        services.AddSingleton(appContext);

        // Core services
        services.AddSingleton<IStatusReporter, StatusReporterService>();
        services.AddSingleton<INotificationService, NotificationServiceImpl>();
        services.AddSingleton<IModeListManager, ModeListManagerService>();
        services.AddSingleton<IWindowActivator, WindowActivatorService>();
        services.AddSingleton<MainController>();
        services.AddSingleton<LiteModeManager>();
        services.AddSingleton<VelopackUpdateService>();
        services.AddSingleton<StartupRegistrationService>();
        services.AddSingleton<ModeService>();
        services.AddSingleton<Configuration>();
        services.AddSingleton<DelayTestHelper>();
        services.AddSingleton<Bandwidth>();

        // ViewModels
        // The main proxy configuration and command are window-scoped in practice.
        // Keep one instance alive so the global title-bar status/action continues
        // to work while the user views logs or settings.
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LogViewModel>();

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();

        return services.BuildServiceProvider();
    }

    private void InitializeLogging()
    {
        var logPath = Path.Combine(_appContext.DataRoot, Constants.LogFile);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var liteModeManager = Services.GetRequiredService<LiteModeManager>();
        var uiSink = new UiLogSink(
            DispatcherQueue.GetForCurrentThread(),
            () => liteModeManager.CurrentProcessNames);
        _uiLogSink = uiSink;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Async(a => a.File(logPath,
                outputTemplate: Constants.OutputTemplate,
                rollingInterval: RollingInterval.Day))
            .WriteTo.Sink(uiSink)
            .CreateLogger();

        StdoutRedirector.StartRedirecting();
    }

    private async Task InitializeStartupStateAsync()
    {
        var config = Services.GetRequiredService<Configuration>();
        await config.LoadAsync();

        try
        {
            Services.GetRequiredService<StartupRegistrationService>()
                .Reconcile(_appContext.Settings.RunAtStartup);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to reconcile the Windows startup task");
        }

        i18N.Load(_appContext.Settings.Language);
        ModeHelper.ModeBaseDirectory = Path.Combine(_appContext.DataRoot, "mode");
    }
}
