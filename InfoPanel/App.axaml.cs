using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;
using Serilog.Events;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace InfoPanel
{
    public partial class App : Application
    {
        private static readonly ILogger Logger = Log.ForContext<App>();
        private static FileStream? _lockFile;
        private Views.MainWindow? _mainWindow;

        private static readonly IHost _host = Host
            .CreateDefaultBuilder()
            .UseSerilog((context, services, configuration) => configuration
#if DEBUG
                .MinimumLevel.Debug()
#else
                .MinimumLevel.Information()
#endif
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.WithThreadId()
                .Enrich.WithThreadName()
                .Enrich.WithMachineName()
                .Enrich.FromLogContext()
                .WriteTo.Debug()
                .WriteTo.File(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "logs", "infopanel-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 104857600,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] [{ThreadName}] - [{SourceContext}] {Message:lj}{NewLine}{Exception}"
                ))
            .ConfigureServices((context, services) =>
            {
                // Views and ViewModels will be registered here as they are ported
            }).Build();

        public static T? GetService<T>() where T : class
        {
            return _host.Services.GetService(typeof(T)) as T;
        }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override async void OnFrameworkInitializationCompleted()
        {
            // Exception handlers
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Register native library resolver for libusb on Linux
            if (OperatingSystem.IsLinux())
            {
                NativeLibrary.SetDllImportResolver(typeof(MonoLibUsb.MonoUsbApi).Assembly, (libraryName, assembly, searchPath) =>
                {
                    if (libraryName == "libusb-1.0")
                    {
                        if (NativeLibrary.TryLoad("libusb-1.0.so.0", out var handle))
                            return handle;
                    }
                    return IntPtr.Zero;
                });
            }

            SentrySdk.Init(o =>
            {
                o.Dsn = "https://5ca30f9d2faba70d50918db10cee0d26@o4508414465146880.ingest.us.sentry.io/4508414467833856";
                o.Debug = true;
                o.AutoSessionTracking = true;
                o.SendDefaultPii = true;
                o.AttachStacktrace = true;
                o.Environment = "production";
                o.Release = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            });

            Logger.Information("InfoPanel starting up");

            // Single instance check via file lock
            var lockDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InfoPanel");
            Directory.CreateDirectory(lockDir);
            var lockPath = Path.Combine(lockDir, ".lock");

            try
            {
                _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Logger.Warning("Another instance of InfoPanel is already running (lock file). Exiting.");
                Environment.Exit(1);
                return;
            }

            // Set working directory
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            var cwd = exePath != null ? Path.GetDirectoryName(exePath) : null;
            if (cwd != null)
            {
                Environment.CurrentDirectory = cwd;
            }

            _host.Start();
            Logger.Debug("Application host started");

            // Create and show the main window FIRST so the UI is responsive
            // while background services start up
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _mainWindow = new Views.MainWindow();
                desktop.MainWindow = _mainWindow;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

                // Handle start-minimized
                if (ConfigModel.Instance.Settings.StartMinimized)
                {
                    _mainWindow.WindowState = Avalonia.Controls.WindowState.Minimized;
                    if (ConfigModel.Instance.Settings.MinimizeToTray)
                    {
                        _mainWindow.Hide();
                    }
                }
            }

            base.OnFrameworkInitializationCompleted();

            ConfigModel.Instance.Initialize();
            Logger.Debug("Configuration initialized");

            if (ConfigModel.Instance.Profiles.Count == 0)
            {
                var profile = new Profile()
                {
                    Name = "Profile 1",
                    Active = false,
                };

                ConfigModel.Instance.AddProfile(profile);
                SharedModel.Instance.SelectedProfile = profile;
                ConfigModel.Instance.SaveProfiles();

                var textDisplayItem = new TextDisplayItem("Go to Design tab to start your journey.", profile);
                textDisplayItem.X = 50;
                textDisplayItem.Y = 100;
                textDisplayItem.Font = "Arial";
                textDisplayItem.Italic = true;

                SharedModel.Instance.AddDisplayItem(textDisplayItem);

                textDisplayItem = new TextDisplayItem("Drag this panel to reposition.", profile);
                textDisplayItem.X = 50;
                textDisplayItem.Y = 150;
                textDisplayItem.Font = "Arial";
                textDisplayItem.Italic = true;
                SharedModel.Instance.AddDisplayItem(textDisplayItem);
                SharedModel.Instance.SaveDisplayItems();
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    HWHash.SetDelay(300);
                    HWHash.Launch();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "HWHash initialization failed");
                }
            }
            else
            {
                Logger.Information("Skipping HWiNFO shared memory monitor (Windows-only)");
            }

            if (ConfigModel.Instance.Settings.LibreHardwareMonitor)
            {
                try
                {
                    await LibreMonitor.Instance.StartAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "LibreHardwareMonitor initialization failed - library may not be available on this platform");
                }
            }

            try
            {
                await PluginMonitor.Instance.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Plugin monitor initialization failed");
            }

            // Start Linux hwmon sensor monitor
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    HwmonMonitor.Instance.Start(ConfigModel.Instance.Settings.TargetGraphUpdateRate);
                    Logger.Information("HwmonMonitor started");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "HwmonMonitor initialization failed");
                }
            }

            try
            {
                await StartPanels();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Panel startup failed");
            }
        }

        private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception ?? new Exception($"Non-exception thrown: {e.ExceptionObject}");
            SentrySdk.CaptureException(exception);
            Logger.Fatal(exception, "CurrentDomain_UnhandledException occurred. IsTerminating: {IsTerminating}", e.IsTerminating);
            SentrySdk.FlushAsync(TimeSpan.FromSeconds(5)).Wait();
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception is AggregateException agg)
            {
                foreach (var innerEx in agg.InnerExceptions)
                {
                    SentrySdk.CaptureException(innerEx);
                    Logger.Error(innerEx, "Unobserved task exception in aggregate");
                }
            }
            else
            {
                SentrySdk.CaptureException(e.Exception);
                Logger.Error(e.Exception, "Unobserved task exception");
            }
            e.SetObserved();
        }

        private static async Task StartPanels()
        {
            if (ConfigModel.Instance.Settings.BeadaPanelMultiDeviceMode)
            {
                await BeadaPanelTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.TuringPanelMultiDeviceMode)
            {
                await TuringPanelTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.ThermalrightPanelMultiDeviceMode)
            {
                await ThermalrightPanelTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.WebServer)
            {
                await WebServerTask.Instance.StartAsync();
            }
        }

        private static async Task StopPanels()
        {
            await BeadaPanelTask.Instance.StopAsync();
            await TuringPanelTask.Instance.StopAsync();
            await ThermalrightPanelTask.Instance.StopAsync();
        }

        public static async Task CleanShutDown()
        {
            DisplayWindowManager.Instance.CloseAll();
            await StopPanels();

            try { await LibreMonitor.Instance.StopAsync(); } catch { }
            try { await PluginMonitor.Instance.StopAsync(); } catch { }
            try { HwmonMonitor.Instance.Stop(); } catch { }

            _lockFile?.Dispose();
            _lockFile = null;

            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    desktop.Shutdown();
                });
            }
        }

        public void ShowDisplayWindow(Profile profile)
        {
            DisplayWindowManager.Instance.ShowDisplayWindow(profile);
        }

        public void CloseDisplayWindow(Profile profile)
        {
            DisplayWindowManager.Instance.CloseDisplayWindow(profile.Guid);
        }

        // Tray icon event handlers
        private void TrayIcon_Clicked(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
        }

        private void TrayMenu_Open(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
        }

        private void TrayMenu_Profiles(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.Profiles);
        }

        private void TrayMenu_Design(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.Design);
        }

        private void TrayMenu_Updates(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.Updates);
        }

        private void TrayMenu_Plugins(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.Plugins);
        }

        private void TrayMenu_UsbPanels(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.UsbPanels);
        }

        private void TrayMenu_Settings(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.Settings);
        }

        private void TrayMenu_Logs(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.Logs);
        }

        private void TrayMenu_About(object? sender, EventArgs e)
        {
            _mainWindow?.RestoreWindow();
            _mainWindow?.NavigateToPage(ViewModels.NavigationPage.About);
        }

        private void TrayMenu_Exit(object? sender, EventArgs e)
        {
            _ = CleanShutDown();
        }
    }
}
