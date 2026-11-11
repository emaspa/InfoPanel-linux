using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InfoPanel.Gpu
{
    /// <summary>A GPU that panel frames can be rendered on.</summary>
    /// <param name="Id">Stable identifier saved in settings: "pci:" plus the PCI address.</param>
    /// <param name="Name">Renderer name for the UI.</param>
    /// <param name="Driver">Kernel driver (nvidia, amdgpu, i915, xe, ...).</param>
    /// <param name="PciAddress">e.g. 0000:7d:00.0.</param>
    /// <param name="Handle">EGLDeviceEXT for the process lifetime.</param>
    public sealed record GpuDeviceInfo(string Id, string Name, string Driver, string PciAddress, IntPtr Handle)
    {
        public string DisplayName => $"{Name} ({Driver}, {PciAddress})";
    }

    /// <summary>
    /// Lists the GPUs EGL can render on headlessly, one entry per physical
    /// device. With glvnd every vendor library reports its own EGL devices, so
    /// a card can show up twice (e.g. the NVIDIA card through both the NVIDIA
    /// and the Mesa vendor); only the entry from the vendor matching the
    /// kernel driver is kept. Mesa's software device is skipped: the CPU
    /// option already covers that, faster.
    /// </summary>
    public static class GpuDeviceCatalog
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(GpuDeviceCatalog));
        private static readonly Lazy<IReadOnlyList<GpuDeviceInfo>> _devices = new(Enumerate);

        public const string IdPrefix = "pci:";

        public static IReadOnlyList<GpuDeviceInfo> Devices => _devices.Value;

        public static GpuDeviceInfo? Find(string id) =>
            Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

        private static IReadOnlyList<GpuDeviceInfo> Enumerate()
        {
            try
            {
                var clientExt = Egl.String(Egl.eglQueryString(IntPtr.Zero, Egl.EGL_EXTENSIONS)) ?? "";
                if (!clientExt.Contains("EGL_EXT_device_enumeration") && !clientExt.Contains("EGL_EXT_device_base"))
                {
                    Logger.Information("GPU rendering unavailable: EGL has no device enumeration");
                    return [];
                }
                if (!clientExt.Contains("EGL_EXT_platform_device"))
                {
                    Logger.Information("GPU rendering unavailable: EGL has no EGL_EXT_platform_device");
                    return [];
                }

                var query = Egl.Proc<Egl.QueryDevicesExt>("eglQueryDevicesEXT");
                var queryString = Egl.Proc<Egl.QueryDeviceStringExt>("eglQueryDeviceStringEXT");
                if (query == null || queryString == null)
                {
                    return [];
                }

                if (!query(0, null, out var count) || count <= 0)
                {
                    return [];
                }
                var handles = new IntPtr[count];
                if (!query(count, handles, out count))
                {
                    return [];
                }

                var result = new List<GpuDeviceInfo>();
                foreach (var handle in handles.Take(count))
                {
                    var ext = Egl.String(queryString(handle, Egl.EGL_EXTENSIONS)) ?? "";
                    if (ext.Contains("EGL_MESA_device_software"))
                    {
                        continue;
                    }

                    var node = ext.Contains("EGL_EXT_device_drm_render_node")
                        ? Egl.String(queryString(handle, Egl.EGL_DRM_RENDER_NODE_FILE_EXT))
                        : null;
                    if (string.IsNullOrEmpty(node) && ext.Contains("EGL_EXT_device_drm"))
                    {
                        node = Egl.String(queryString(handle, Egl.EGL_DRM_DEVICE_FILE_EXT));
                    }
                    if (string.IsNullOrEmpty(node))
                    {
                        continue;
                    }

                    var sysDevice = Path.Combine("/sys/class/drm", Path.GetFileName(node), "device");
                    var pci = ResolveName(sysDevice);
                    var driver = ResolveName(Path.Combine(sysDevice, "driver"));
                    if (pci == null || driver == null)
                    {
                        Logger.Debug("EGL device {Node} skipped: no PCI device in sysfs", node);
                        continue;
                    }

                    // Keep the vendor library that actually drives the card.
                    var isNvidiaVendor = ext.Contains("EGL_NV_device_cuda");
                    if ((driver == "nvidia") != isNvidiaVendor)
                    {
                        continue;
                    }

                    var renderer = ext.Contains("EGL_EXT_device_query_name")
                        ? Egl.String(queryString(handle, Egl.EGL_RENDERER_EXT))
                        : null;
                    var info = new GpuDeviceInfo(IdPrefix + pci, CleanName(renderer, driver), driver, pci, handle);
                    if (result.All(d => d.Id != info.Id))
                    {
                        result.Add(info);
                    }
                }

                foreach (var d in result)
                {
                    Logger.Information("GPU available for panel rendering: {Name} [{Id}]", d.DisplayName, d.Id);
                }
                return result;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Logger.Information("GPU rendering unavailable: {Message}", ex.Message);
                return [];
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "GPU enumeration failed");
                return [];
            }
        }

        // sysfs links are relative and chained (/sys/class/drm/X is itself a
        // link), so resolve with realpath(3) rather than textual link reading.
        private static string? ResolveName(string link)
        {
            var p = realpath(link, IntPtr.Zero);
            if (p == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var full = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p);
                return string.IsNullOrEmpty(full) ? null : Path.GetFileName(full);
            }
            finally
            {
                free(p);
            }
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern IntPtr realpath(string path, IntPtr resolved);

        [System.Runtime.InteropServices.DllImport("libc")]
        private static extern void free(IntPtr p);

        // "NVIDIA GeForce RTX 5090/PCIe/SSE2" -> "NVIDIA GeForce RTX 5090";
        // Mesa: "AMD Ryzen 9 9950X3D 16-Core Processor (radeonsi, raphael_mendocino, ...)"
        // names the APU, so keep the chip codename to make clear it is the iGPU.
        internal static string CleanName(string? renderer, string driver)
        {
            if (string.IsNullOrWhiteSpace(renderer))
            {
                return driver switch
                {
                    "nvidia" => "NVIDIA GPU",
                    "amdgpu" => "AMD GPU",
                    "i915" or "xe" => "Intel GPU",
                    _ => "GPU",
                };
            }

            var name = renderer.Trim();
            var slash = name.IndexOf('/');
            if (slash > 0)
            {
                name = name[..slash];
            }

            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0)
            {
                var details = name[(paren + 2)..].TrimEnd(')').Split(',', StringSplitOptions.TrimEntries);
                // AMD: "(radeonsi, raphael_mendocino, ACO, ...)" driver then chip;
                // Intel: "(ADL GT2)" chip only.
                var chip = details.Length > 1 ? details[1] : details.FirstOrDefault();
                name = name[..paren] + (string.IsNullOrEmpty(chip) ? "" : $" [{chip}]");
            }
            return name;
        }
    }
}
