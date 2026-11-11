using Serilog;
using System.Runtime.InteropServices;

namespace InfoPanel.Video
{
    internal enum HardwareDecodeApi
    {
        /// <summary>ffmpeg's -hwaccel auto: no usable render node found.</summary>
        Auto,
        Vaapi,
        Cuda,
    }

    /// <summary>How ffmpeg decodes on the GPU: the API and, for VA-API, the render node.</summary>
    internal sealed record HardwareDecoder(HardwareDecodeApi Api, string? Device = null, string? Description = null);

    /// <summary>A DRM render node as sysfs describes it.</summary>
    /// <param name="Path">e.g. /dev/dri/renderD129.</param>
    /// <param name="PciAddress">e.g. 0000:7d:00.0.</param>
    /// <param name="Driver">Kernel driver: amdgpu, i915, xe, nvidia, ...</param>
    /// <param name="VramBytes">amdgpu's dedicated VRAM, 0 when the driver doesn't report it.</param>
    internal sealed record RenderNode(string Path, string PciAddress, string Driver, long VramBytes);

    /// <summary>
    /// Picks the GPU for hardware video decoding. ffmpeg's -hwaccel auto tries CUDA
    /// first, so on a machine with an NVIDIA card and an integrated GPU it decoded
    /// on the discrete card: +190 MB in ffmpeg for the CUDA context, and the card
    /// kept awake. Decoding and scaling on an AMD iGPU through VA-API instead took
    /// 0.083 cores and 225 MB for a 2400x1080 file at 1600x720, against 0.094 cores
    /// and 426 MB through CUDA and 0.197 cores and 235 MB in software.
    /// </summary>
    internal static class VideoHardwareDecoder
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(VideoHardwareDecoder));

        private static readonly Lazy<IReadOnlyList<RenderNode>> _nodes = new(Enumerate);

        /// <summary>
        /// The decoder for this session. <paramref name="preferredPci"/> is the GPU
        /// the panels render on ("pci:..." or "cpu"), which wins when it has a render
        /// node.
        /// </summary>
        public static HardwareDecoder Get(string? preferredPci)
        {
            var decoder = Choose(_nodes.Value, preferredPci);
            if (!Equals(Interlocked.Exchange(ref _lastLogged, decoder), decoder))
            {
                Logger.Information("Hardware video decoding uses {Decoder}", decoder.Description);
            }
            return decoder;
        }

        private static HardwareDecoder? _lastLogged;

        /// <summary>
        /// The GPU the panels render on if set; otherwise anything but NVIDIA, the
        /// one with the least VRAM first (an integrated GPU's carve-out is smaller
        /// than a discrete card's memory, and Intel reports none); NVIDIA through
        /// CUDA last, since NVIDIA's VA-API driver is a separate, optional package.
        /// </summary>
        internal static HardwareDecoder Choose(IReadOnlyList<RenderNode> nodes, string? preferredPci)
        {
            const string prefix = "pci:";
            RenderNode? node = null;
            if (preferredPci != null && preferredPci.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var pci = preferredPci[prefix.Length..];
                node = nodes.FirstOrDefault(n => string.Equals(n.PciAddress, pci, StringComparison.OrdinalIgnoreCase));
            }
            node ??= nodes.Where(n => n.Driver != "nvidia").OrderBy(n => n.VramBytes).ThenBy(n => n.Path, StringComparer.Ordinal).FirstOrDefault();
            node ??= nodes.FirstOrDefault(n => n.Driver == "nvidia");

            return node switch
            {
                null => new HardwareDecoder(HardwareDecodeApi.Auto, Description: "ffmpeg's choice"),
                { Driver: "nvidia" } => new HardwareDecoder(HardwareDecodeApi.Cuda, Description: $"CUDA on {node.PciAddress}"),
                _ => new HardwareDecoder(HardwareDecodeApi.Vaapi, node.Path, $"VA-API on {node.PciAddress} ({node.Driver})"),
            };
        }

        private static IReadOnlyList<RenderNode> Enumerate()
        {
            var result = new List<RenderNode>();
            try
            {
                foreach (var dir in System.IO.Directory.EnumerateDirectories("/sys/class/drm", "renderD*").Order(StringComparer.Ordinal))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    var device = System.IO.Path.Combine(dir, "device");
                    var pci = ResolveName(device);
                    var driver = ResolveName(System.IO.Path.Combine(device, "driver"));
                    if (pci == null || driver == null || !File.Exists("/dev/dri/" + name))
                    {
                        continue;
                    }
                    long vram = 0;
                    try { _ = long.TryParse(File.ReadAllText(System.IO.Path.Combine(device, "mem_info_vram_total")).Trim(), out vram); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    result.Add(new RenderNode("/dev/dri/" + name, pci, driver, vram));
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not list render nodes");
            }
            return result;
        }

        // sysfs links are relative and chained, so resolve with realpath(3).
        private static string? ResolveName(string link)
        {
            var p = realpath(link, IntPtr.Zero);
            if (p == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var full = Marshal.PtrToStringUTF8(p);
                return string.IsNullOrEmpty(full) ? null : System.IO.Path.GetFileName(full);
            }
            finally
            {
                free(p);
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr realpath(string path, IntPtr resolved);

        [DllImport("libc")]
        private static extern void free(IntPtr p);
    }
}
