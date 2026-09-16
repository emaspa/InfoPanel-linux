namespace InfoPanel.Services;

internal interface INvApi : IDisposable
{
    IntPtr FindGpuByBusId(uint busId);
    IntPtr SingleGpuHandle { get; }
    int CalculateThermalsMask(IntPtr handle);
    (int? Hotspot, int? Vram) ReadTemperatures(IntPtr handle, int mask, bool isBlackwell);
    int? ReadVoltageMv(IntPtr handle);
}

internal sealed class NvApiUnavailableException(int status) : Exception($"NvAPI session unavailable: {status}");
