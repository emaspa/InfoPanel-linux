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
        {
            var x11 = new X11PlatformOptions
            {
                // WM_CLASS must match infopanel.desktop for correct dock/taskbar icon
                WmClass = "infopanel"
            };

            // Rendering backend override for diagnosing presentation jank
            // (GLX vsync under XWayland on some drivers). Values: software, egl, glx.
            switch (Environment.GetEnvironmentVariable("INFOPANEL_RENDER_MODE"))
            {
                case "software":
                    x11.RenderingMode = [X11RenderingMode.Software];
                    break;
                case "egl":
                    x11.RenderingMode = [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software];
                    break;
                case "glx":
                    x11.RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Software];
                    break;
            }

            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(x11)
                .WithInterFont()
                .LogToTrace();
        }
    }
}
