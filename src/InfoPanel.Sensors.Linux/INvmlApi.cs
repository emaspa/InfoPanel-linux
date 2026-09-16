namespace InfoPanel.Services;

// Native boundary shared by discovery and polling; tests never load the driver.
internal interface INvmlApi
{
    NvmlReturn nvmlInit_v2();
    NvmlReturn nvmlShutdown();
    NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount);
    NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    NvmlReturn nvmlDeviceGetName(IntPtr device, out string name);
    NvmlReturn nvmlDeviceGetUUID(IntPtr device, out string uuid);
    NvmlReturn nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, out uint temp);
    NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint power);
    NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
    NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clock);
    NvmlReturn nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    NvmlReturn nvmlDeviceGetFanSpeed(IntPtr device, out uint speed);
    NvmlReturn nvmlDeviceGetNumFans(IntPtr device, ref uint numFans);
    NvmlReturn nvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanSpeed);
    NvmlReturn nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit);
    NvmlReturn nvmlDeviceGetPerformanceState(IntPtr device, out uint pstate);
    NvmlReturn nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
    NvmlReturn nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo pci);
    NvmlReturn nvmlDeviceGetArchitecture(IntPtr device, out uint arch);
}

internal sealed class NativeNvmlApi : INvmlApi
{
    public NvmlReturn nvmlInit_v2() => Nvml.nvmlInit_v2();
    public NvmlReturn nvmlShutdown() => Nvml.nvmlShutdown();
    public NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount) => Nvml.nvmlDeviceGetCount_v2(out deviceCount);
    public NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device) => Nvml.nvmlDeviceGetHandleByIndex_v2(index, out device);
    public NvmlReturn nvmlDeviceGetName(IntPtr device, out string name) => Nvml.nvmlDeviceGetName(device, out name);
    public NvmlReturn nvmlDeviceGetUUID(IntPtr device, out string uuid) => Nvml.nvmlDeviceGetUUID(device, out uuid);
    public NvmlReturn nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, out uint temp) => Nvml.nvmlDeviceGetTemperature(device, sensorType, out temp);
    public NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint power) => Nvml.nvmlDeviceGetPowerUsage(device, out power);
    public NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization) => Nvml.nvmlDeviceGetUtilizationRates(device, out utilization);
    public NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clock) => Nvml.nvmlDeviceGetClockInfo(device, type, out clock);
    public NvmlReturn nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory) => Nvml.nvmlDeviceGetMemoryInfo(device, out memory);
    public NvmlReturn nvmlDeviceGetFanSpeed(IntPtr device, out uint speed) => Nvml.nvmlDeviceGetFanSpeed(device, out speed);
    public NvmlReturn nvmlDeviceGetNumFans(IntPtr device, ref uint numFans) => Nvml.nvmlDeviceGetNumFans(device, ref numFans);
    public NvmlReturn nvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanSpeed) => Nvml.nvmlDeviceGetFanSpeedRPM(device, ref fanSpeed);
    public NvmlReturn nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit) => Nvml.nvmlDeviceGetPowerManagementLimit(device, out limit);
    public NvmlReturn nvmlDeviceGetPerformanceState(IntPtr device, out uint pstate) => Nvml.nvmlDeviceGetPerformanceState(device, out pstate);
    public NvmlReturn nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons) => Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(device, out reasons);
    public NvmlReturn nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo pci) => Nvml.nvmlDeviceGetPciInfo_v3(device, out pci);
    public NvmlReturn nvmlDeviceGetArchitecture(IntPtr device, out uint arch) => Nvml.nvmlDeviceGetArchitecture(device, out arch);
}
