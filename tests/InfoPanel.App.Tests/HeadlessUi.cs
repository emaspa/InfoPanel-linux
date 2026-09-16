using Avalonia;
using Avalonia.Controls.Embedding;
using Avalonia.Controls.Embedding.Offscreen;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering;
using Avalonia.Threading;
using FluentAvalonia.Styling;

namespace InfoPanel.App.Tests;

// Runs real layout, font measurement and the application's theme without X11,
// an AppHost, or hardware polling. Keep all Avalonia objects on one UI thread.
public sealed class HeadlessUi : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public HeadlessUi()
    {
        _thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<Application>()
                    .UseStandardRuntimePlatformSubsystem().UseSkia().UseHarfBuzz()
                    .UseWindowingSubsystem(InitializePlatform, "Offscreen tests")
                    .SetupWithoutStarting();
                Application.Current!.Styles.Add(new FluentAvaloniaTheme());
                _ready.SetResult(Dispatcher.UIThread);
                Dispatcher.UIThread.MainLoop(_stop.Token);
            }
            catch (Exception error)
            {
                _ready.TrySetException(error);
            }
        }) { IsBackground = true, Name = "Avalonia layout tests" };
        _thread.Start();
    }

    public async Task Run(Action test)
    {
        var dispatcher = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await dispatcher.InvokeAsync(test);
    }

    public static EmbeddableControlRoot CreateRoot() => new(new OffscreenTopLevel());

    private static void InitializePlatform()
    {
        AvaloniaLocator.CurrentMutable
            .Bind<ICursorFactory>().ToConstant(new Cursors())
            .Bind<IPlatformSettings>().ToConstant(new DefaultPlatformSettings())
            .Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(new UiThreadRenderTimer(60)));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(10));
        _stop.Dispose();
    }

    private sealed class OffscreenTopLevel : OffscreenTopLevelImplBase
    {
        public override IPlatformRenderSurface[] Surfaces => [];
        public override IMouseDevice MouseDevice => null!;
    }

    private sealed class Cursors : ICursorFactory
    {
        public ICursorImpl GetCursor(StandardCursorType type) => new Cursor();
        public ICursorImpl CreateCursor(Bitmap bitmap, PixelPoint hotSpot) => new Cursor();
    }

    private sealed class Cursor : ICursorImpl
    {
        public void Dispose() { }
    }
}
