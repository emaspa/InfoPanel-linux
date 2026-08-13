using Serilog;
using System;
using System.Runtime.InteropServices;

namespace InfoPanel.Services;

/// <summary>
/// Reads GPU hotspot temperature, VRAM temperature and core voltage through the
/// undocumented NvAPI surface of the proprietary NVIDIA driver. Since R525 the
/// driver ships libnvidia-api.so.1 on Linux, exposing the same query-interface
/// mechanism as nvapi.dll on Windows. None of these values are available via NVML.
/// Query ids, struct layouts and register offsets follow LACT's implementation
/// (github.com/ilya-zlobintsev/LACT, lact-daemon nvidia/nvapi.rs, GPL-3.0 like us).
/// </summary>
internal sealed class NvApi : IDisposable
{
    private const string LibraryName = "libnvidia-api.so.1";

    private const uint QUERY_INITIALIZE = 0x0150e828;
    private const uint QUERY_UNLOAD = 0xd22bdd7e;
    private const uint QUERY_ENUM_PHYSICAL_GPUS = 0xe5ac921f;
    private const uint QUERY_GET_BUS_ID = 0x1be0b8e5;
    private const uint QUERY_THERMALS = 0x65fe3aad;
    private const uint QUERY_VOLTAGE = 0x465f9bcf;
    private const uint QUERY_GPU_REGISTER_OP = 0x2eb3c140;

    private const int MAX_PHYSICAL_GPUS = 64;

    // Hotspot lives in thermal sensor slot 9 up to Ada/Hopper; Blackwell dropped it
    // from the thermals query and only reports it through this GPU register.
    private const int THERMALS_SLOT_HOTSPOT = 9;
    private const int THERMALS_SLOT_VRAM = 15;
    private const int THERMALS_SLOT_VRAM_GDDR7 = 10;
    private const uint REG_OFFSET_BLACKWELL_HOTSPOT = 0xad0aa0;

    private const ushort REG_OP_READ_32BIT_GLOBAL = 1 | 4 | 16;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceDelegate(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NoArgsDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([In, Out] IntPtr[] handles, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusIdDelegate(IntPtr handle, ref uint busId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ThermalsDelegate(IntPtr handle, ref NvApiThermals thermals);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VoltageDelegate(IntPtr handle, ref NvApiVoltage voltage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RegisterOpDelegate(IntPtr handle, ref NvGpuRegisterOpData data);

    private IntPtr _lib;
    private QueryInterfaceDelegate _queryInterface = null!;
    private ThermalsDelegate? _thermals;
    private VoltageDelegate? _voltage;
    private RegisterOpDelegate? _registerOp;
    private IntPtr[] _gpuHandles = [];

    private NvApi() { }

    /// <summary>
    /// Loads and initializes NvAPI. Returns null when the library is absent
    /// (open/nouveau driver, or proprietary driver older than R525) or refuses
    /// to initialize; callers simply skip the extra sensors in that case.
    /// </summary>
    public static NvApi? TryCreate()
    {
        var api = new NvApi();
        try
        {
            if (!NativeLibrary.TryLoad(LibraryName, out api._lib))
            {
                Log.Debug("NvApi: {Library} not found", LibraryName);
                return null;
            }

            var qi = NativeLibrary.GetExport(api._lib, "nvapi_QueryInterface");
            api._queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(qi);

            var init = api.GetDelegate<NoArgsDelegate>(QUERY_INITIALIZE);
            if (init == null || init() != 0)
            {
                Log.Debug("NvApi: initialize failed");
                api.Dispose();
                return null;
            }

            var enumGpus = api.GetDelegate<EnumPhysicalGpusDelegate>(QUERY_ENUM_PHYSICAL_GPUS);
            if (enumGpus == null)
            {
                api.Dispose();
                return null;
            }

            uint count = 0;
            var handles = new IntPtr[MAX_PHYSICAL_GPUS];
            if (enumGpus(handles, ref count) != 0 || count == 0)
            {
                Log.Debug("NvApi: no physical GPUs enumerated");
                api.Dispose();
                return null;
            }

            api._gpuHandles = handles[..(int)count];
            api._thermals = api.GetDelegate<ThermalsDelegate>(QUERY_THERMALS);
            api._voltage = api.GetDelegate<VoltageDelegate>(QUERY_VOLTAGE);
            api._registerOp = api.GetDelegate<RegisterOpDelegate>(QUERY_GPU_REGISTER_OP);

            Log.Information("NvApi: initialized, {Count} GPU(s)", count);
            return api;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "NvApi: initialization error");
            api.Dispose();
            return null;
        }
    }

    private T? GetDelegate<T>(uint queryId) where T : class
    {
        var ptr = _queryInterface(queryId);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>Finds the NvAPI handle for the GPU on the given PCI bus number.</summary>
    public IntPtr FindGpuByBusId(uint busId)
    {
        var getBusId = GetDelegate<GetBusIdDelegate>(QUERY_GET_BUS_ID);
        if (getBusId == null) return IntPtr.Zero;

        foreach (var handle in _gpuHandles)
        {
            uint id = 0;
            if (getBusId(handle, ref id) == 0 && id == busId)
                return handle;
        }

        return IntPtr.Zero;
    }

    /// <summary>Single NvAPI GPU handle, usable directly on single-GPU systems.</summary>
    public IntPtr SingleGpuHandle => _gpuHandles.Length == 1 ? _gpuHandles[0] : IntPtr.Zero;

    /// <summary>
    /// Determines which thermal sensor slots this GPU exposes by widening the query
    /// mask until the driver rejects it. Result is cached by the caller per device.
    /// </summary>
    public int CalculateThermalsMask(IntPtr handle)
    {
        if (_thermals == null) return 0;

        var thermals = NvApiThermals.Create(1);
        if (_thermals(handle, ref thermals) != 0)
            return 0;

        for (int bit = 0; bit < 32; bit++)
        {
            thermals = NvApiThermals.Create(1 << bit);
            if (_thermals(handle, ref thermals) != 0)
                return (1 << bit) - 1;
        }

        return 0;
    }

    /// <summary>
    /// Reads hotspot and VRAM temperature (°C). Values in the thermals response
    /// are fixed-point /256; slots outside 1..254 after scaling are not present.
    /// isBlackwell selects the register-based hotspot and the GDDR7 VRAM slot.
    /// </summary>
    public (int? Hotspot, int? Vram) ReadTemperatures(IntPtr handle, int mask, bool isBlackwell)
    {
        int? hotspot = null;
        int? vram = null;

        if (_thermals != null && mask != 0)
        {
            var thermals = NvApiThermals.Create(mask);
            if (_thermals(handle, ref thermals) == 0)
            {
                if (!isBlackwell)
                    hotspot = thermals.GetValue(THERMALS_SLOT_HOTSPOT);
                vram = thermals.GetValue(isBlackwell ? THERMALS_SLOT_VRAM_GDDR7 : THERMALS_SLOT_VRAM);
            }
        }

        if (isBlackwell)
        {
            var raw = ReadRegister(handle, REG_OFFSET_BLACKWELL_HOTSPOT);
            if (raw.HasValue)
            {
                var value = (int)((raw.Value & 0xFFFF) / 256);
                if (value > 0 && value < 255)
                    hotspot = value;
            }
        }

        return (hotspot, vram);
    }

    /// <summary>Reads GPU core voltage in millivolts.</summary>
    public int? ReadVoltageMv(IntPtr handle)
    {
        if (_voltage == null) return null;

        var data = NvApiVoltage.Create();
        if (_voltage(handle, ref data) != 0)
            return null;

        var mv = (int)(data.currentVoltageUv / 1000);
        return mv > 0 ? mv : null;
    }

    private ulong? ReadRegister(IntPtr handle, uint offset)
    {
        if (_registerOp == null) return null;

        var data = NvGpuRegisterOpData.Create();
        data.ops[0].flags = REG_OP_READ_32BIT_GLOBAL;
        data.ops[0].offset = offset;
        data.opCount = 1;

        if (_registerOp(handle, ref data) != 0)
            return null;

        return data.ops[0].value;
    }

    public void Dispose()
    {
        if (_lib != IntPtr.Zero)
        {
            try
            {
                var unload = GetDelegate<NoArgsDelegate>(QUERY_UNLOAD);
                unload?.Invoke();
            }
            catch { }
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
        }
    }

    // NvAPI versioned-struct convention: version = sizeof(struct) | (version << 16)

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvApiThermals
    {
        public uint version;
        public int mask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
        public int[] values;

        public static NvApiThermals Create(int mask) => new()
        {
            version = (uint)(Marshal.SizeOf<NvApiThermals>() | (2 << 16)),
            mask = mask,
            values = new int[40],
        };

        public readonly int? GetValue(int index)
        {
            if (values == null || index >= values.Length) return null;
            var value = values[index] / 256;
            return value > 0 && value < 255 ? value : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvApiVoltage
    {
        public uint version;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rsvd;
        // Single-entry rail array, flattened
        public uint railId;
        public uint currentVoltageUv;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] railRsvd;

        public static NvApiVoltage Create() => new()
        {
            version = (uint)(Marshal.SizeOf<NvApiVoltage>() | (1 << 16)),
            rsvd = new byte[32],
            railRsvd = new byte[32],
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvGpuRegisterOp
    {
        public ushort flags;
        public ushort status;
        public uint offset;
        public ulong writeMask;
        public ulong value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvGpuRegisterOpData
    {
        public uint version;
        public uint opCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public NvGpuRegisterOp[] ops;

        public static NvGpuRegisterOpData Create() => new()
        {
            version = (uint)(Marshal.SizeOf<NvGpuRegisterOpData>() | (1 << 16)),
            ops = new NvGpuRegisterOp[256],
        };
    }
}
