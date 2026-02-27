using Avalonia;
using Avalonia.X11;
using System;

namespace InfoPanel;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                WmClass = "infopanel"
            })
            .WithInterFont()
            .LogToTrace();
}
