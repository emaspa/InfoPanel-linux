using Avalonia;
using Serilog;
using Serilog.Events;

namespace InfoPanel
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var verbose = args.Contains("--verbose");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console()
                .WriteTo.Sink(Utils.UiLogSink.Instance)
                .WriteTo.File(
                    Path.Combine(Persistence.ConfigPersistence.BaseFolder, "logs", "infopanel-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .CreateLogger();

            try
            {
                if (args.Contains("--headless") || args.Contains("--render-once") || args.Contains("--dump-sensors"))
                {
                    return HeadlessRunner.RunAsync(args).GetAwaiter().GetResult();
                }

                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error");
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions
                {
                    // WM_CLASS must match infopanel.desktop for correct dock/taskbar icon
                    WmClass = "infopanel"
                })
                .WithInterFont()
                .LogToTrace();
    }
}
