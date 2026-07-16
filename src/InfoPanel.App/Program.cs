using Serilog;
using Serilog.Events;

namespace InfoPanel
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var headless = args.Contains("--headless");
            var verbose = args.Contains("--verbose");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine(Persistence.ConfigPersistence.BaseFolder, "logs", "infopanel-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .CreateLogger();

            try
            {
                if (!headless)
                {
                    Log.Warning("UI not implemented yet in v2 — running headless. Pass --headless to silence this warning.");
                }

                var host = new AppHost();
                host.Initialize();

                // --dump-sensors: start monitors, poll briefly, print all readings and exit
                if (args.Contains("--dump-sensors"))
                {
                    await host.StartSensorsAsync();
                    await Task.Delay(2500);

                    foreach (var (id, reading) in Services.HwmonMonitor.SENSORHASH.OrderBy(kv => kv.Key))
                    {
                        Console.WriteLine($"{id,-40} {reading.ValueNow,12:0.###} {reading.Unit}");
                    }

                    Log.Information("{Count} hwmon sensors, {PluginCount} plugin sensors",
                        Services.HwmonMonitor.SENSORHASH.Count, Monitors.PluginMonitor.SENSORHASH.Count);
                    Environment.Exit(0);
                }

                // --render-once [dir]: render every profile to PNG and exit (pipeline verification)
                var renderOnceIndex = Array.IndexOf(args, "--render-once");
                if (renderOnceIndex >= 0)
                {
                    await host.StartSensorsAsync();
                    await Task.Delay(2500); // let monitors populate so sensor items render real values

                    var outDir = args.Length > renderOnceIndex + 1 && !args[renderOnceIndex + 1].StartsWith("--")
                        ? args[renderOnceIndex + 1]
                        : Directory.GetCurrentDirectory();
                    Directory.CreateDirectory(outDir);

                    foreach (var profile in host.Profiles)
                    {
                        using var bitmap = PanelRenderer.RenderSK(profile);
                        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        var file = Path.Combine(outDir, $"{profile.Name.Replace(' ', '_')}-{profile.Guid}.png");
                        await File.WriteAllBytesAsync(file, data.ToArray());
                        Log.Information("Rendered {Profile} ({W}x{H}) -> {File}", profile.Name, profile.Width, profile.Height, file);
                    }

                    return 0;
                }

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    Log.Information("Shutdown requested");
                    cts.Cancel();
                };

                await host.StartSensorsAsync();
                await host.StartDevicesAsync(cts.Token);
                Log.Information("InfoPanel running headless — Ctrl+C to exit");

                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }

                await host.StopDevicesAsync();
                await Log.CloseAndFlushAsync();

                // LibUsbDotNet leaves non-daemon event threads behind; release the usb
                // context and force-exit (v1 did the same in App shutdown).
                LibUsbDotNet.UsbDevice.Exit();
                Environment.Exit(0);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error");
                return 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }
}
