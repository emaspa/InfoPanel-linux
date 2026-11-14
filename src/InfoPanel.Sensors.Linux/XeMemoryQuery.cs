using Serilog;
using System;
using System.Runtime.InteropServices;

namespace InfoPanel.Services;

/// <summary>VRAM in bytes, summed over a card's VRAM regions. Used is null when the kernel does not report it.</summary>
internal readonly record struct XeVram(ulong Total, ulong? Used);

internal interface IXeMemoryQuery : IDisposable
{
    /// <summary>Current VRAM totals, or null when the query fails or the device has no VRAM (integrated GPUs).</summary>
    XeVram? QueryVram();
}

/// <summary>
/// DRM_IOCTL_XE_DEVICE_QUERY with DRM_XE_DEVICE_QUERY_MEM_REGIONS on the card's render node.
/// Render nodes are open to every user, the query needs no capability on current kernels, and
/// it reads the VRAM allocator without GT forcewake, so polling it does not keep the GPU out of RC6.
/// Layouts follow include/uapi/drm/xe_drm.h.
/// </summary>
internal sealed class XeMemoryQuery : IXeMemoryQuery
{
    // _IOWR('d', DRM_COMMAND_BASE + DRM_XE_DEVICE_QUERY, struct drm_xe_device_query), 40 bytes
    private const ulong IoctlDeviceQuery = 0xC0286440;
    private const int QuerySize = 40;           // struct drm_xe_device_query
    private const int QueryKindOffset = 8;      // __u32 query
    private const int QueryLengthOffset = 12;   // __u32 size
    private const int QueryDataOffset = 16;     // __u64 data
    private const uint MemRegions = 1;          // DRM_XE_DEVICE_QUERY_MEM_REGIONS
    private const int RegionsHeader = 8;        // num_mem_regions + pad
    private const int RegionSize = 88;          // struct drm_xe_mem_region
    private const ushort ClassVram = 1;         // DRM_XE_MEM_REGION_CLASS_VRAM
    private const int MaxReply = 64 * 1024;

    private int _fd;

    private XeMemoryQuery(int fd) => _fd = fd;

    public static XeMemoryQuery? Open(string renderNode)
    {
        var fd = open(renderNode, O_RDWR | O_CLOEXEC);
        if (fd < 0)
        {
            Log.Debug("Intel GPU: cannot open {Node} for the VRAM query (errno {Errno})", renderNode, Marshal.GetLastPInvokeError());
            return null;
        }
        return new XeMemoryQuery(fd);
    }

    public XeVram? QueryVram()
    {
        if (_fd < 0) return null;
        var query = Marshal.AllocHGlobal(QuerySize);
        var reply = IntPtr.Zero;
        try
        {
            for (var i = 0; i < QuerySize; i++) Marshal.WriteByte(query, i, 0);
            Marshal.WriteInt32(query, QueryKindOffset, (int)MemRegions);

            // The first call with size 0 asks the kernel how large the reply is.
            if (Ioctl(query) != 0) return null;
            var length = Marshal.ReadInt32(query, QueryLengthOffset);
            if (length < RegionsHeader || length > MaxReply) return null;

            reply = Marshal.AllocHGlobal(length);
            for (var i = 0; i < length; i++) Marshal.WriteByte(reply, i, 0);
            Marshal.WriteInt64(query, QueryDataOffset, reply.ToInt64());
            if (Ioctl(query) != 0) return null;

            return Parse(reply, length);
        }
        finally
        {
            if (reply != IntPtr.Zero) Marshal.FreeHGlobal(reply);
            Marshal.FreeHGlobal(query);
        }
    }

    internal static XeVram? Parse(IntPtr reply, int length)
    {
        var count = (uint)Marshal.ReadInt32(reply, 0);
        if (count > (length - RegionsHeader) / RegionSize) return null;
        ulong total = 0, used = 0;
        for (var i = 0; i < count; i++)
        {
            var region = RegionsHeader + i * RegionSize;
            if ((ushort)Marshal.ReadInt16(reply, region) != ClassVram) continue;
            total += (ulong)Marshal.ReadInt64(reply, region + 8);   // total_size
            used += (ulong)Marshal.ReadInt64(reply, region + 16);   // used
        }
        if (total == 0) return null;
        // Kernels that gated 'used' behind CAP_PERFMON leave it at 0 for normal users. A card with
        // VRAM always has some allocated (firmware, framebuffers), so 0 means "not reported".
        return new XeVram(total, used > 0 ? used : null);
    }

    private int Ioctl(IntPtr arg)
    {
        while (true)
        {
            if (ioctl(_fd, IoctlDeviceQuery, arg) == 0) return 0;
            var errno = Marshal.GetLastPInvokeError();
            if (errno is EINTR or EAGAIN) continue;
            Log.Debug("Intel GPU: xe memory region query failed (errno {Errno})", errno);
            return -1;
        }
    }

    public void Dispose()
    {
        if (_fd >= 0) { close(_fd); _fd = -1; }
    }

    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;
    private const int EINTR = 4;
    private const int EAGAIN = 11;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, IntPtr arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
