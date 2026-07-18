using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using InfoPanel.Utils;
using InfoPanel.Views;
using Serilog;

namespace InfoPanel
{
    public partial class App : Application
    {
        private static readonly ILogger Logger = Log.ForContext<App>();

        private FileStream? _instanceLock;
        private MainWindow? _mainWindow;
        private AppHost? _host;
        private CancellationTokenSource? _cts;
        private readonly List<System.Runtime.InteropServices.PosixSignalRegistration> _signalRegistrations = [];

        public AppHost Host => _host ?? throw new InvalidOperationException("AppHost not initialized");

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (!AcquireSingleInstanceLock())
                {
                    // Launching again (desktop icon, terminal) surfaces the running
                    // instance instead of dying silently: touch a request file the
                    // first instance watches, then exit.
                    Logger.Information("Another InfoPanel instance is already running; asking it to show its window");
                    try
                    {
                        File.WriteAllText(ShowRequestPath, "");
                    }
                    catch
                    {
                    }
                    Environment.Exit(0);
                    return;
                }

                StartShowRequestWatcher();

                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += OnShutdownRequested;

                // Session managers end the session with SIGTERM; run the normal
                // fast shutdown so logout and restart never wait on us (issue #2).
                // A backstop hard-exits if that path ever stalls. SIGHUP is left
                // alone: registering a handler would override nohup's ignore and
                // kill the app when a launching terminal closes.
                foreach (var signal in new[] { System.Runtime.InteropServices.PosixSignal.SIGTERM })
                {
                    _signalRegistrations.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true;
                        Logger.Information("Received {Signal}, shutting down", context.Signal);

                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(12));
                            Environment.Exit(1);
                        });

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                Logger.Information("Invoking application shutdown");
                                // TryShutdown, not Shutdown: the force path skips the
                                // ShutdownRequested event that runs our cleanup and
                                // the hard exit, leaving LibUsb threads holding the
                                // process open.
                                desktop.TryShutdown();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex, "Shutdown failed, exiting hard");
                                Environment.Exit(1);
                            }
                        });
                    }));
                }

                // Marshal Core model notifications through the Avalonia dispatcher
                UiThread.Configure(action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
                UiLogSink.Instance.AttachDispatcher(action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

                _host = new AppHost();
                _host.Initialize();
                _cts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    await _host.StartSensorsAsync();
                    await _host.StartDevicesAsync(_cts.Token);
                });

                if (_host.Settings.UpdateCheckEnabled)
                {
                    _ = Task.Run(Services.UpdateChecker.RunStartupCheckAsync);
                }

                // The classic desktop lifetime auto-shows MainWindow after init,
                // so for Start minimized the window must not be assigned yet
                // (issue #3); ShowMainWindow assigns it on first show.
                _mainWindow = new MainWindow();
                if (!_host.Settings.StartMinimized)
                {
                    desktop.MainWindow = _mainWindow;
                    _mainWindow.Show();
                }

                // Show overlay windows for active profiles
                foreach (var profile in _host.Profiles.Where(p => p.Active))
                {
                    DisplayWindowManager.Instance.ShowDisplayWindow(profile);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static string ShowRequestPath =>
            Path.Combine(Persistence.ConfigPersistence.BaseFolder, ".show-request");

        private FileSystemWatcher? _showRequestWatcher;

        private void StartShowRequestWatcher()
        {
            try
            {
                File.Delete(ShowRequestPath);
            }
            catch
            {
            }

            try
            {
                _showRequestWatcher = new FileSystemWatcher(Persistence.ConfigPersistence.BaseFolder, ".show-request")
                {
                    EnableRaisingEvents = true,
                };

                void OnRequest(object? _, FileSystemEventArgs __)
                {
                    try
                    {
                        File.Delete(ShowRequestPath);
                    }
                    catch
                    {
                    }

                    Avalonia.Threading.Dispatcher.UIThread.Post(ShowMainWindow);
                }

                _showRequestWatcher.Created += OnRequest;
                _showRequestWatcher.Changed += OnRequest;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not watch for show requests from other instances");
            }
        }

        private bool AcquireSingleInstanceLock()
        {
            try
            {
                var lockPath = Path.Combine(Persistence.ConfigPersistence.BaseFolder, ".lock");
                Directory.CreateDirectory(Persistence.ConfigPersistence.BaseFolder);
                _instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public void ShowMainWindow()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _mainWindow is { } main)
            {
                Logger.Debug("ShowMainWindow: IsVisible={IsVisible} State={State}", main.IsVisible, main.WindowState);
                desktop.MainWindow ??= main;
                main.WindowState = WindowState.Normal;
                main.Show();
                main.Activate();
            }
        }

        private void TrayMenu_Open(object? sender, EventArgs e) => ShowMainWindow();

        // Fires on tray activation: single or double click depending on what the
        // desktop's tray host sends (KDE/XFCE activate on left click; some hosts
        // only on double click; GNOME's AppIndicator extension is menu-only).
        private void TrayIcon_Clicked(object? sender, EventArgs e) => ShowMainWindow();

        private void OpenAt(string tag)
        {
            ShowMainWindow();
            _mainWindow?.NavigateTo(tag);
        }

        private void TrayMenu_Dashboard(object? sender, EventArgs e) => OpenAt("dashboard");
        private void TrayMenu_Designer(object? sender, EventArgs e) => OpenAt("designer");
        private void TrayMenu_Devices(object? sender, EventArgs e) => OpenAt("devices");
        private void TrayMenu_Sensors(object? sender, EventArgs e) => OpenAt("sensors");
        private void TrayMenu_Settings(object? sender, EventArgs e) => OpenAt("settings");

        private void TrayMenu_Exit(object? sender, EventArgs e) => Shutdown();

        public void Shutdown()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // TryShutdown so ShutdownRequested fires (see signal handler note)
                desktop.TryShutdown();
            }
        }

        private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
        {
            Logger.Information("Shutdown requested");
            _cts?.Cancel();
            DisplayWindowManager.Instance.CloseAll();

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_host != null)
                    {
                        await _host.StopDevicesAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Error stopping services during shutdown");
                }

                await Log.CloseAndFlushAsync();
                _instanceLock?.Dispose();

                // LibUsbDotNet leaves non-daemon threads behind; force exit like v1 did
                LibUsbDotNet.UsbDevice.Exit();
                Environment.Exit(0);
            });
        }
    }
}
