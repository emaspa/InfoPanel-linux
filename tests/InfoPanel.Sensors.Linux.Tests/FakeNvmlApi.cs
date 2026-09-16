using InfoPanel.Services;

namespace InfoPanel.Sensors.Linux.Tests;

internal class FakeNvmlApi : INvmlApi
{
    public virtual NvmlReturn nvmlInit_v2() { return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlShutdown() { return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount) { deviceCount = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device) { device = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetName(IntPtr device, out string name) { name = ""; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetUUID(IntPtr device, out string uuid) { uuid = ""; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, out uint temp) { temp = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint power) { power = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization) { utilization = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clock) { clock = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory) { memory = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetFanSpeed(IntPtr device, out uint speed) { speed = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetNumFans(IntPtr device, ref uint numFans) { return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanSpeed) { return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit) { limit = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetPerformanceState(IntPtr device, out uint pstate) { pstate = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons) { reasons = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo pci) { pci = default; return NvmlReturn.NotSupported; }
    public virtual NvmlReturn nvmlDeviceGetArchitecture(IntPtr device, out uint arch) { arch = default; return NvmlReturn.NotSupported; }
}
