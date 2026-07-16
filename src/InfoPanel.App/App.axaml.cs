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
        private AppHost? _host;
        private CancellationTokenSource? _cts;

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
                    Logger.Error("Another InfoPanel instance is already running");
                    Environment.Exit(1);
                    return;
                }

                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += OnShutdownRequested;

                // Marshal Core model notifications through the Avalonia dispatcher
                UiThread.Configure(action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

                _host = new AppHost();
                _host.Initialize();
                _cts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    await _host.StartSensorsAsync();
                    await _host.StartDevicesAsync(_cts.Token);
                });

                desktop.MainWindow = new MainWindow();
                desktop.MainWindow.Show();

                // Show overlay windows for active profiles
                foreach (var profile in _host.Profiles.Where(p => p.Active))
                {
                    DisplayWindowManager.Instance.ShowDisplayWindow(profile);
                }
            }

            base.OnFrameworkInitializationCompleted();
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
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                desktop.MainWindow.Show();
                desktop.MainWindow.WindowState = WindowState.Normal;
                desktop.MainWindow.Activate();
            }
        }

        private void TrayMenu_Open(object? sender, EventArgs e) => ShowMainWindow();

        private void TrayMenu_Exit(object? sender, EventArgs e) => Shutdown();

        public void Shutdown()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
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
