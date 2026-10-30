using System.Net;
using System.Runtime.InteropServices;
using DroidDeck.Adb;
using DroidDeck.Adb.Commands;
using DroidDeck.Adb.Devices;
using DroidDeck.Adb.Emulators;
using DroidDeck.Adb.Logcat;
using DroidDeck.Adb.Protocol;
using DroidDeck.Adb.Toolbox;
using DroidDeck.Adb.Tracking;
using DroidDeck.Adb.Wireless;
using DroidDeck.App.Services;
using DroidDeck.App.Views;
using DroidDeck.Core;
using DroidDeck.Core.Commands;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Emulators;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Localization;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Notifications;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Resources;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Screen;
using DroidDeck.Core.Security;
using DroidDeck.Core.Sessions;
using DroidDeck.Core.Toolbox;
using DroidDeck.Core.Testing;
using DroidDeck.Core.Toolchain;
using DroidDeck.Core.Updates;
using DroidDeck.Core.Wireless;
using DroidDeck.Plugins;
using DroidDeck.Plugins.BuiltIns;
using DroidDeck.Plugins.Catalog;
using DroidDeck.Plugin.UI.Rendering;
using DroidDeck.Projects;
using DroidDeck.Projects.Artifacts;
using DroidDeck.Projects.Detection;
using DroidDeck.Projects.Storage;
using DroidDeck.Screen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using IPluginRegistry = DroidDeck.Plugin.Abstractions.IPluginRegistry;

namespace DroidDeck.App;

/// <summary>
/// The application object and composition root. Services are resolved before
/// the main window is created, and background work starts after the shell is
/// interactive so startup stays inside its budget.
/// </summary>
public partial class App : Application, IDisposable
{
    private readonly AppOptions _options;
    private readonly AppPaths _paths;
    private readonly AppInstanceCoordinator _instance;
    private readonly BoundedLogBuffer _logBuffer = new();
    private readonly SessionContext _session = new();
    private ILoggerFactory? _loggerFactory;
    private ConfigurationService? _configuration;
    private ServiceProvider? _services;
    private PluginHost? _pluginHost;
    private Localizer? _localizer;
    private MainWindow? _window;
    private AdbDeviceTracker? _adbTracker;
    private AdbDeviceMetadataSource? _adbMetadata;
    private bool _shutdownStarted;
    private bool _disposed;

    /// <summary>Creates the application.</summary>
    public App(AppOptions options, AppPaths paths, AppInstanceCoordinator instance)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        _instance.ActivationRequested += OnActivationRequested;
    }

    /// <summary>Gets the running application instance.</summary>
    public static App Instance => (App)Current;

    /// <summary>Gets or sets what startup session retention removed, for display on the history page.</summary>
    public RetentionResult? LastStartupRetention { get; set; }

    /// <summary>Gets the resolved data paths.</summary>
    public AppPaths Paths => _paths;

    /// <summary>Gets the launch options.</summary>
    public AppOptions Options => _options;

    /// <summary>Gets the current launch's identifier.</summary>
    public SessionContext Session => _session;

    /// <summary>Gets the service provider once composition completes.</summary>
    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("The application services are not ready yet.");

    /// <summary>Gets the main window once it has been created.</summary>
    internal MainWindow? Window => _window;

    /// <summary>Navigates the main window to a page key.</summary>
    public void Navigate(string key) => _window?.Navigate(key);

    /// <summary>Applies a theme to the main window.</summary>
    public void ApplyTheme(AppTheme theme) => _window?.ApplyTheme(theme);

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args) => _ = LaunchAsync();

    /// <summary>Disposes services and closes the window, then exits the process.</summary>
    public async Task RequestShutdownAsync(MainWindow window)
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        try
        {
            await window.SavePlacementAsync();
        }
        catch (Exception exception)
        {
            _loggerFactory?.CreateLogger<App>().LogWarning(exception, "Window placement could not be saved.");
        }

        try
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            logger?.LogWarning(exception, "Service shutdown failed.");
        }

        Dispose();

        if (_options.UsesTemporaryDataDirectory)
        {
            TryDeleteTemporaryData();
        }

        window.AllowClose();
        window.Close();
        Exit();
    }

    private async Task LaunchAsync()
    {
        try
        {
            InitializeLogging();
            var loggerFactory = _loggerFactory!;
            var logger = loggerFactory.CreateLogger<App>();
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "DroidDeck {Version} starting. Session {SessionId}, diagnostics {DiagnosticsId}, data {DataDirectory}.",
                    AppInfo.Version,
                    _session.SessionId,
                    _session.DiagnosticsId,
                    _paths.DataDirectory);
            }

            _configuration = new ConfigurationService(
                _paths,
                loggerFactory.CreateLogger<ConfigurationService>());
            await _configuration.InitializeAsync();
            if (_configuration.LastLoadNotice is { } notice)
            {
                logger.LogWarning("Settings notice: {Message}", notice.Message);
            }

            await ApplyLaunchOverridesAsync(_configuration, logger);

            _localizer = new Localizer(_configuration.GetString(SettingsPaths.Language));
            ApplyLocalizerResources();

            _services = BuildServices(loggerFactory, _configuration);
            _configuration.Changed += OnConfigurationChanged;
            _window = _services.GetRequiredService<MainWindow>();
            _window.Closed += OnWindowClosed;
            _window.Activate();

            foreach (var pending in _instance.TakePendingActivations())
            {
                HandleActivation(pending);
            }

            HandleActivation(_options.RawArguments);
            _ = InitializeBackgroundAsync();
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            logger?.LogCritical(exception, "DroidDeck failed to start.");
            ShowStartupFailure(exception);
        }
    }

    private async Task ApplyLaunchOverridesAsync(ConfigurationService configuration, ILogger logger)
    {
        var overrides = new List<KeyValuePair<string, string>>(_options.Overrides.Settings);
        var environment = new List<KeyValuePair<string, string?>>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment.Add(new(entry.Key?.ToString() ?? string.Empty, entry.Value?.ToString()));
        }

        overrides.AddRange(CliOverrides.ParseEnvironment(environment));
        if (overrides.Count == 0)
        {
            return;
        }

        try
        {
            await configuration.SetSessionOverridesAsync(overrides);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "A --set, --profile, or DROIDDECK__ override was rejected; it is ignored for this session.");
        }
    }

    private void InitializeLogging()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new InMemoryLoggerProvider(_logBuffer, _session.SessionId));
            builder.AddProvider(new RollingFileLoggerProvider(
                _paths.LogsDirectory,
                "app",
                retentionDays: 14,
                sessionId: _session.SessionId));
        });
    }

    private ServiceProvider BuildServices(ILoggerFactory loggerFactory, ConfigurationService configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_paths);
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IConfigurationService>(configuration);
        services.AddSingleton(_logBuffer);
        services.AddSingleton(_session);
        services.AddSingleton<ILocalizer>(_localizer
            ?? throw new InvalidOperationException("The localizer is not ready."));

        services.AddSingleton<IProcessRunner>(
            _ => new ProcessRunner(logger: loggerFactory.CreateLogger<ProcessRunner>()));
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<IResourceGuard>(_ => new ResourceGuard(new ResourceLimits
        {
            MaxBuilds = configuration.Current.Settings.Screen.MaxConcurrentBuilds,
            MaxMirrors = configuration.Current.Settings.Screen.MaxMirrors,
            MaxRecordings = configuration.Current.Settings.Screen.MaxRecordings,
            WarnAtPercent = configuration.Current.Settings.Screen.WarnAtPercent,
        }));
        services.AddSingleton<IDeviceOperationCoordinator>(_ => new DeviceOperationCoordinator(
            configuration.Current.Settings.Run.MaxConcurrentDevices,
            loggerFactory.CreateLogger<DeviceOperationCoordinator>()));
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<IDeviceRecordStore, JsonDeviceRecordStore>();
        services.AddSingleton<IAdbSocketClient>(_ => new AdbSocketClient());
        services.AddSingleton<IToolchainService, ToolchainDoctor>();
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<IPluginRegistry>(provider => provider.GetRequiredService<PluginRegistry>());
        services.AddSingleton<AppThemeService>();
        services.AddSingleton<IThemeService>(provider => provider.GetRequiredService<AppThemeService>());
        services.AddSingleton<IUiDispatcher>(new AppUiDispatcher());
        services.AddSingleton<RendererService>();
        services.AddSingleton<IRendererHost>(provider => provider.GetRequiredService<RendererService>());

        services.AddSingleton(provider =>
        {
            var registry = provider.GetRequiredService<PluginRegistry>();
            var loader = new PluginLoader(
                registry,
                loggerFactory,
                secretStore: provider.GetRequiredService<ISecretStore>(),
                dialogs: provider.GetRequiredService<PluginDialogService>());
            return new PluginHost(
                registry,
                loader,
                _paths,
                loggerFactory.CreateLogger<PluginHost>(),
                loggerFactory,
                [new WaitForDeviceRunStep(
                    provider.GetRequiredService<IProcessRunner>(),
                    () => GetAdbPath(provider))]);
        });

        services.AddSingleton(provider => new PluginDialogService(
            () => provider.GetRequiredService<PluginHost>(),
            () => (provider.GetRequiredService<MainWindow>().Content as FrameworkElement)?.XamlRoot,
            DispatcherQueue.GetForCurrentThread()));

        services.AddSingleton(_ => new PluginUiOrderStore(
            System.IO.Path.Combine(_paths.DataDirectory, "ui-order.json")));

        services.AddSingleton(provider => new PluginUiFaultReporter(
            provider.GetRequiredService<PluginHost>().Health));

        services.AddSingleton(provider =>
        {
            var http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 3,
            })
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            var client = new PluginCatalogClient(
                http,
                _paths.CatalogDirectory,
                userAgent: $"DroidDeck/{AppInfo.Version}",
                logger: loggerFactory.CreateLogger<PluginCatalogClient>());
            return new PluginCatalogService(
                _paths,
                client,
                AppInfo.Version,
                DroidDeck.Plugin.Abstractions.PluginApi.Version,
                loggerFactory.CreateLogger<PluginCatalogService>());
        });

        services.AddSingleton(new PluginAuditLog(_paths.PluginAuditFile));
        services.AddSingleton(provider => new CatalogInstaller(
            new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                // The installer follows redirects by hand so every hop is
                // checked against the HTTPS requirement before it is sent.
                AllowAutoRedirect = false,
            })
            {
                // Packages are larger than catalogs, so the download gets a
                // longer budget than the 20 second catalog client.
                Timeout = TimeSpan.FromSeconds(120),
            },
            _paths,
            provider.GetRequiredService<PluginAuditLog>(),
            userAgent: $"DroidDeck/{AppInfo.Version}",
            logger: loggerFactory.CreateLogger<CatalogInstaller>()));

        services.AddSingleton<IDeviceService>(provider =>
        {
            var adbPath = configuration.Current.Settings.Tools.AdbPath;
            if (string.IsNullOrWhiteSpace(adbPath))
            {
                adbPath = "adb";
            }

            var runner = provider.GetRequiredService<IProcessRunner>();
            var socketClient = provider.GetRequiredService<IAdbSocketClient>();
            var tracker = new AdbDeviceTracker(
                runner,
                new AdbTrackerOptions
                {
                    Endpoint = AdbEndpoint.Resolve(),
                    AdbExecutable = adbPath,
                },
                socketClient,
                loggerFactory.CreateLogger<AdbDeviceTracker>());
            var metadata = new AdbDeviceMetadataSource(
                runner,
                adbPath,
                logger: loggerFactory.CreateLogger<AdbDeviceMetadataSource>());
            _adbTracker = tracker;
            _adbMetadata = metadata;
            return new DeviceService(
                tracker,
                provider.GetRequiredService<IDeviceRecordStore>(),
                metadata,
                logger: loggerFactory.CreateLogger<DeviceService>());
        });
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        services.AddSingleton(provider => new UpdateChecker(
            new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                // Redirects are followed by hand so every hop is checked for HTTPS.
                AllowAutoRedirect = false,
            })
            {
                Timeout = TimeSpan.FromSeconds(15),
            },
            AppInfo.Version));
        services.AddSingleton<ICommandRegistry, CommandRegistry>();
        services.AddSingleton(provider => new KeybindingMap(
            provider.GetRequiredService<ICommandRegistry>(),
            Path.Combine(_paths.DataDirectory, "keybindings.json")));
        services.AddSingleton<DroidDeck.Core.Profiles.ProfileService>();
        services.AddSingleton<DroidDeck.Core.Features.FeatureVisibilityService>();
        services.AddSingleton<DroidDeck.Core.Themes.ThemeManager>();

        services.AddSingleton<SessionWorkspace>();
        services.AddSingleton<IProjectStore>(provider => new JsonProjectStore(
            _paths,
            loggerFactory.CreateLogger<JsonProjectStore>()));
        services.AddSingleton<IProjectDetector, GradleProjectDetector>();
        services.AddSingleton<IProjectDetector, FlutterProjectDetector>();
        services.AddSingleton<IProjectDetector, ReactNativeProjectDetector>();
        services.AddSingleton<IProjectDetector, CapacitorProjectDetector>();
        services.AddSingleton<IProjectDetector, CordovaProjectDetector>();
        services.AddSingleton<IProjectDetector, UnityProjectDetector>();
        services.AddSingleton<IProjectDetector, MauiProjectDetector>();
        services.AddSingleton<IProjectDetector, ApkProjectDetector>();
        services.AddSingleton<IApkInspector>(provider => new Aapt2ApkInspector(
            provider.GetRequiredService<IProcessRunner>(),
            configuration,
            provider.GetRequiredService<IToolchainService>(),
            loggerFactory.CreateLogger<Aapt2ApkInspector>()));
        services.AddSingleton<IArtifactResolver, GlobArtifactResolver>();
        services.AddSingleton<IProjectService>(provider => new ProjectService(
            provider.GetRequiredService<IProjectStore>(),
            provider.GetServices<IProjectDetector>(),
            provider.GetRequiredService<IApkInspector>(),
            configuration,
            provider.GetRequiredService<IToolchainService>(),
            loggerFactory.CreateLogger<ProjectService>()));
        services.AddSingleton<IRunPlanBuilder>(provider => new RunPlanBuilder(
            configuration,
            provider.GetRequiredService<IToolchainService>()));
        services.AddSingleton<ITestPlanBuilder>(provider => new TestPlanBuilder(
            provider.GetRequiredService<IRunPlanBuilder>(),
            configuration,
            provider.GetRequiredService<IToolchainService>()));
        services.AddSingleton<ITestRunService>(provider => new TestRunService(
            provider.GetRequiredService<ITestPlanBuilder>(),
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IDeviceOperationCoordinator>(),
            provider.GetRequiredService<IPluginRegistry>()));

        string GetAdbPath(IServiceProvider provider) =>
            ToolPathLocator.ResolveAdb(configuration, provider.GetRequiredService<IToolchainService>());

        services.AddSingleton<IDeviceCommandService>(provider => new AdbDeviceCommandService(
            provider.GetRequiredService<IProcessRunner>(),
            () => GetAdbPath(provider),
            loggerFactory.CreateLogger<AdbDeviceCommandService>()));
        services.AddSingleton<ILogEnricher>(provider => new PluginLogEnricher(
            provider.GetRequiredService<IPluginRegistry>(),
            loggerFactory.CreateLogger<PluginLogEnricher>()));
        services.AddSingleton<ILogcatService>(provider => new AdbLogcatService(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IDeviceCommandService>(),
            () => GetAdbPath(provider),
            loggerFactory.CreateLogger<AdbLogcatService>(),
            provider.GetRequiredService<ILogEnricher>()));
        services.AddSingleton<IDeviceToolboxService>(provider => new AdbToolboxService(
            provider.GetRequiredService<IProcessRunner>(),
            () => GetAdbPath(provider),
            logger: loggerFactory.CreateLogger<AdbToolboxService>(),
            coordinator: provider.GetRequiredService<IDeviceOperationCoordinator>()));
        services.AddSingleton<IDeviceFileService>(provider => new AdbDeviceFileService(
            provider.GetRequiredService<IProcessRunner>(),
            () => GetAdbPath(provider),
            logger: loggerFactory.CreateLogger<AdbDeviceFileService>(),
            coordinator: provider.GetRequiredService<IDeviceOperationCoordinator>()));
        services.AddSingleton<IDeviceIntentService>(provider => new AdbIntentService(
            provider.GetRequiredService<IProcessRunner>(),
            () => GetAdbPath(provider),
            logger: loggerFactory.CreateLogger<AdbIntentService>(),
            coordinator: provider.GetRequiredService<IDeviceOperationCoordinator>()));
        services.AddSingleton<IWirelessDeviceStore>(provider => new JsonWirelessDeviceStore(
            _paths,
            loggerFactory.CreateLogger<JsonWirelessDeviceStore>()));
        services.AddSingleton<IWirelessAdbService>(provider => new WirelessAdbService(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IWirelessDeviceStore>(),
            () => GetAdbPath(provider),
            () =>
            {
                var wireless = configuration.Current.Settings.Wireless;
                return new WirelessReconnectOptions(
                    wireless.AutoReconnect,
                    TimeSpan.FromSeconds(Math.Max(1, wireless.ReconnectDelaySeconds)));
            },
            logger: loggerFactory.CreateLogger<WirelessAdbService>()));
        services.AddSingleton<IEmulatorService>(provider => new EmulatorService(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IProcessLauncher>(),
            () => configuration.Current.Settings.Tools.EmulatorPath,
            () => GetAdbPath(provider),
            () => Environment.GetEnvironmentVariable("ANDROID_AVD_HOME"),
            logger: loggerFactory.CreateLogger<EmulatorService>()));
        services.AddSingleton<IScreenWindowController, Win32ScreenWindowController>();
        services.AddSingleton<IScreenService>(provider => new ScrcpyScreenService(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IProcessLauncher>(),
            provider.GetRequiredService<IScreenWindowController>(),
            provider.GetRequiredService<IResourceGuard>(),
            _paths,
            () => configuration.Current.Settings.Tools.ScrcpyPath,
            () => GetAdbPath(provider),
            () => configuration.Current.Settings.Screen,
            logger: loggerFactory.CreateLogger<ScrcpyScreenService>()));
        services.AddSingleton<IThumbnailService>(provider => new AdbThumbnailService(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IScreenService>(),
            () => GetAdbPath(provider),
            Path.Combine(_paths.DataDirectory, "thumbnails"),
            logger: loggerFactory.CreateLogger<AdbThumbnailService>(),
            resourceGuard: provider.GetRequiredService<IResourceGuard>()));
        services.AddSingleton<ISessionStore, JsonSessionStore>();
        services.AddSingleton<ISavedFilterStore, JsonSavedFilterStore>();
        services.AddSingleton<INotificationService>(_ => new NotificationService
        {
            Preferences = configuration.Current.Settings.Notifications.ToPreferences(),
        });
        services.AddSingleton<IRunSessionService, RunSessionService>();
        services.AddSingleton<ApkInstallService>();
        services.AddSingleton<IMultiDeviceRunService>(provider => new MultiDeviceRunService(
            provider.GetRequiredService<IRunSessionService>(),
            provider.GetRequiredService<IDeviceOperationCoordinator>(),
            () => configuration.Current.Settings.Run.MaxConcurrentDevices,
            loggerFactory.CreateLogger<MultiDeviceRunService>()));
        services.AddSingleton<QuickRunMemory>();
        services.AddTransient<ViewModels.DevicesViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<HomePage>();
        services.AddTransient<OnboardingPage>();
        services.AddTransient<DevicesPage>();
        services.AddTransient<ActivityPage>();
        services.AddTransient<ProjectsPage>();
        services.AddTransient<SessionPage>();
        services.AddTransient<HistoryPage>();
        services.AddTransient<ExtensionsPage>();
        services.AddTransient<CatalogPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<DiagnosticsPage>();
        services.AddTransient<AboutPage>();
        services.AddTransient<DeviceToolsPage>();
        services.AddTransient<MirrorPage>();
        services.AddTransient<EmulatorsPage>();
        services.AddTransient<WirelessPage>();
        services.AddTransient<MultiDevicePage>();
        services.AddTransient<PerformancePage>();
        services.AddTransient<InspectorPage>();
        services.AddTransient<TestsPage>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async Task InitializeBackgroundAsync()
    {
        var logger = _loggerFactory!.CreateLogger<App>();
        try
        {
            var toolchain = _services!.GetRequiredService<IToolchainService>();
            await toolchain.LoadCachedAsync();
            _ = CheckToolchainAsync(toolchain);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The cached toolchain snapshot could not be loaded.");
        }

        var services = _services!;
        var configuration = _configuration!;
        try
        {
            await services.GetRequiredService<IProjectService>().InitializeAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The project list could not be loaded.");
        }

        try
        {
            var logging = configuration.Current.Settings.Logging;
            var retention = await services.GetRequiredService<ISessionStore>().ApplyRetentionAsync(
                logging.RetentionDays,
                logging.MaxSessionBytes);
            if (retention.RemovedSessions > 0)
            {
                LastStartupRetention = retention;
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Session retention removed {Count} session(s).",
                        retention.RemovedSessions);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Session retention could not be applied.");
        }

        _pluginHost = services.GetRequiredService<PluginHost>();
        var safeMode = _options.SafeMode || configuration.Current.Settings.Features.SafeMode;
        var pluginsDisabled = _options.NoPlugins || safeMode;
        var reason = _options.NoPlugins
            ? "--no-plugins"
            : safeMode
                ? "safe mode"
                : null;
        // Plugins must not delay first frame: their load starts on the UI
        // dispatcher after the shell is up and continues off the UI thread.
        _window?.DispatcherQueue.TryEnqueue(() =>
            _ = InitializePluginsAsync(_pluginHost, pluginsDisabled, reason, logger));

        try
        {
            await services.GetRequiredService<RendererService>().InitializeAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Renderer preferences could not be loaded.");
        }

        try
        {
            await services.GetRequiredService<IDeviceService>().StartAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            logger.LogError(exception, "Device tracking failed to start.");
        }

        try
        {
            await services.GetRequiredService<IWirelessAdbService>().StartAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Wireless adb reconnect could not start.");
        }
    }

    private static async Task InitializePluginsAsync(
        PluginHost host,
        bool disabled,
        string? reason,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            await host.InitializeAsync(disabled, reason);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Plugin initialization failed.");
        }
    }

    private async Task CheckToolchainAsync(IToolchainService toolchain)
    {
        try
        {
            await toolchain.CheckAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            logger?.LogWarning(exception, "Toolchain check failed.");
        }
    }

    private void OnActivationRequested(object? sender, ActivationRequestedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        _window.DispatcherQueue.TryEnqueue(() => HandleActivation(e.Arguments));
    }

    private void HandleActivation(IReadOnlyList<string> arguments)
    {
        var forwarded = AppOptions.Parse(arguments);
        if (forwarded.OpenDiagnostics)
        {
            _window?.Navigate("diagnostics");
        }

        _window?.BringToFront();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _instance.ActivationRequested -= OnActivationRequested;
    }

    private void OnConfigurationChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (string.Equals(e.Path, SettingsPaths.Language, StringComparison.OrdinalIgnoreCase))
        {
            if (_window is { } window)
            {
                window.DispatcherQueue.TryEnqueue(ApplyLocalizationChange);
            }
            else
            {
                ApplyLocalizationChange();
            }

            return;
        }

        if (e.Path.StartsWith("notifications.", StringComparison.OrdinalIgnoreCase) &&
            _services?.GetService<INotificationService>() is { } notifications &&
            _configuration is not null)
        {
            notifications.Preferences = _configuration.Current.Settings.Notifications.ToPreferences();
        }

        if (string.Equals(e.Path, SettingsPaths.AdbPath, StringComparison.OrdinalIgnoreCase))
        {
            var adbPath = _configuration?.Current.Settings.Tools.AdbPath;
            if (string.IsNullOrWhiteSpace(adbPath))
            {
                adbPath = "adb";
            }

            _adbTracker?.UpdateAdbExecutable(adbPath);
            _adbMetadata?.UpdateAdbExecutable(adbPath);
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger?.IsEnabled(LogLevel.Information) == true)
            {
                logger.LogInformation("The adb executable path changed and applies to new commands.");
            }
        }

        if (e.Path.StartsWith("screen.", StringComparison.OrdinalIgnoreCase) &&
            _services?.GetService<IResourceGuard>() is { } guard &&
            _configuration is not null)
        {
            var screen = _configuration.Current.Settings.Screen;
            guard.UpdateLimits(new ResourceLimits
            {
                MaxBuilds = screen.MaxConcurrentBuilds,
                MaxMirrors = screen.MaxMirrors,
                MaxRecordings = screen.MaxRecordings,
                WarnAtPercent = screen.WarnAtPercent,
            });
        }

        if (string.Equals(e.Path, SettingsPaths.RunMaxConcurrentDevices, StringComparison.OrdinalIgnoreCase) &&
            _services?.GetService<IDeviceOperationCoordinator>() is { } coordinator &&
            _configuration is not null)
        {
            coordinator.MaxConcurrency = Math.Max(1, _configuration.Current.Settings.Run.MaxConcurrentDevices);
        }
    }

    private void ApplyLocalizationChange()
    {
        if (_localizer is null || _configuration is null)
        {
            return;
        }

        _localizer.Update(_configuration.GetString(SettingsPaths.Language));
        ApplyLocalizerResources();
        _window?.ReloadLocalization();
    }

    private void ApplyLocalizerResources()
    {
        var resources = Application.Current.Resources;
        foreach (var (key, value) in _localizer!.Resources)
        {
            resources[key] = value;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var logger = _loggerFactory?.CreateLogger<App>();
        if (logger?.IsEnabled(LogLevel.Critical) == true)
        {
            logger.LogCritical(e.Exception, "Unhandled UI exception in session {SessionId}.", _session.SessionId);
        }
        try
        {
            _paths.EnsureCreated();
            var reportPath = Path.Combine(
                _paths.DiagnosticsDirectory,
                $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{_session.SessionId}.txt");
            File.WriteAllText(
                reportPath,
                $"Diagnostics ID: {_session.DiagnosticsId}{Environment.NewLine}" +
                $"Session ID: {_session.SessionId}{Environment.NewLine}{e.Exception}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Crash reporting must not hide the original failure.
        }

        e.Handled = true;
    }

    private void ShowStartupFailure(Exception exception)
    {
        var window = new Window { Title = "DroidDeck" };
        var panel = new StackPanel
        {
            Padding = new Thickness(28),
            Spacing = 12,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "DroidDeck could not start",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Diagnostics ID: {_session.DiagnosticsId}. Quote this identifier when requesting support.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = exception.Message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        var copyButton = new Button { Content = "Copy diagnostics" };
        copyButton.Click += (_, _) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(
                    $"DroidDeck startup failure{Environment.NewLine}" +
                    $"Diagnostics ID: {_session.DiagnosticsId}{Environment.NewLine}" +
                    $"Session ID: {_session.SessionId}{Environment.NewLine}" +
                    exception);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
            catch (COMException)
            {
            }
        };
        panel.Children.Add(copyButton);
        window.Content = new ScrollViewer { Content = panel };
        window.Activate();

        if (_options.UsesTemporaryDataDirectory)
        {
            TryDeleteTemporaryData();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_configuration is not null)
        {
            _configuration.Changed -= OnConfigurationChanged;
        }

        _configuration?.Dispose();
        _loggerFactory?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void TryDeleteTemporaryData()
    {
        try
        {
            if (Directory.Exists(_paths.DataDirectory))
            {
                Directory.Delete(_paths.DataDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The OS temp cleaner handles leftovers.
        }
    }
}
