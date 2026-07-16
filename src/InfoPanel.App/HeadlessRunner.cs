using Serilog;

namespace InfoPanel
{
    /// <summary>
    /// CLI mode: runs sensors, panel streaming and diagnostics without the UI.
    /// </summary>
    public static class HeadlessRunner
    {
        public static async Task<int> RunAsync(string[] args)
        {
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

                foreach (var (id, reading) in Monitors.PluginMonitor.SENSORHASH.OrderBy(kv => kv.Key))
                {
                    var value = reading.Data switch
                    {
                        Plugins.IPluginSensor sensor => $"{sensor.Value:0.###} {sensor.Unit}",
                        Plugins.IPluginText text => text.Value,
                        _ => ""
                    };
                    Console.WriteLine($"{id,-60} {value}");
                }

                Log.Information("{Count} hwmon sensors, {PluginCount} plugin sensors",
                    Services.HwmonMonitor.SENSORHASH.Count, Monitors.PluginMonitor.SENSORHASH.Count);
                Log.CloseAndFlush();
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
                    // warmup: image/video sources load asynchronously on first draw
                    PanelRenderer.RenderSK(profile).Dispose();
                    await Task.Delay(3000);

                    using var bitmap = PanelRenderer.RenderSK(profile);
                    using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    var file = Path.Combine(outDir, $"{profile.Name.Replace(' ', '_')}-{profile.Guid}.png");
                    await File.WriteAllBytesAsync(file, data.ToArray());
                    Log.Information("Rendered {Profile} ({W}x{H}) -> {File}", profile.Name, profile.Width, profile.Height, file);
                }

                Log.CloseAndFlush();
                Environment.Exit(0);
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
    }
}
