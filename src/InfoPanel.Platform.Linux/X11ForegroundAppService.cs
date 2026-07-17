using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace InfoPanel.Platform.Linux
{
    /// <summary>
    /// Foreground application detection via the EWMH _NET_ACTIVE_WINDOW root property
    /// and the window's _NET_WM_PID, resolved to a process name through /proc.
    ///
    /// Under a Wayland session this connects to XWayland, which only tracks X11
    /// windows: games and most apps run through XWayland and are detected, but a
    /// focused native Wayland window leaves the last X11 active window in place.
    /// Proton/Wine games surface their Windows executable name (e.g.
    /// "Cyberpunk2077.exe"), matching what Windows users configure.
    /// </summary>
    public sealed class X11ForegroundAppService : IForegroundAppService
    {
        private static readonly ILogger Logger = Log.ForContext<X11ForegroundAppService>();

        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? display);
        [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string atomName, int onlyIfExists);
        [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(
            IntPtr display, IntPtr window, IntPtr property, IntPtr longOffset, IntPtr longLength,
            int delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
            out IntPtr nItems, out IntPtr bytesAfter, out IntPtr prop);
        [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
        [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);

        // XRes extension: authoritative client pid for windows whose toolkit does
        // not set _NET_WM_PID (Athena and other legacy clients).
        [StructLayout(LayoutKind.Sequential)]
        private struct XResClientIdSpec
        {
            public IntPtr Client;
            public uint Mask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XResClientIdValue
        {
            public XResClientIdSpec Spec;
            public IntPtr Length;
            public IntPtr Value;
        }

        private const uint XRES_CLIENT_ID_PID_MASK = 1 << 1;

        [DllImport("libXRes.so.1")] private static extern int XResQueryClientIds(
            IntPtr display, IntPtr numSpecs, ref XResClientIdSpec clientSpecs,
            out IntPtr numIds, out IntPtr clientIds);
        [DllImport("libXRes.so.1")] private static extern int XResGetClientPid(ref XResClientIdValue value);
        [DllImport("libXRes.so.1")] private static extern void XResClientIdsDestroy(IntPtr numIds, IntPtr clientIds);

        private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

        // The active window can vanish between the root read and the pid read; the
        // default Xlib handler would kill the process on the resulting BadWindow.
        private static readonly XErrorHandler IgnoreErrors = (_, _) => 0;

        private const int Success = 0;
        private static readonly IntPtr AnyPropertyType = IntPtr.Zero;

        private readonly IntPtr _display;
        private readonly IntPtr _root;
        private readonly IntPtr _atomActiveWindow;
        private readonly IntPtr _atomWmPid;
        private readonly object _lock = new();

        public bool IsAvailable { get; }
        public string? Limitation { get; }

        public X11ForegroundAppService()
        {
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                Logger.Information("No X display available; program-specific profiles disabled");
                IsAvailable = false;
                return;
            }

            XSetErrorHandler(IgnoreErrors);

            _root = XDefaultRootWindow(_display);
            _atomActiveWindow = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 1);
            _atomWmPid = XInternAtom(_display, "_NET_WM_PID", 1);

            if (_atomActiveWindow == IntPtr.Zero)
            {
                Logger.Information("Window manager does not expose _NET_ACTIVE_WINDOW; program-specific profiles disabled");
                IsAvailable = false;
                return;
            }

            IsAvailable = true;

            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null)
            {
                Limitation = "Wayland session: only X11/XWayland windows (games, most apps) are detected as foreground.";
            }
        }

        public string? GetForegroundProcessName()
        {
            if (!IsAvailable)
            {
                return null;
            }

            lock (_lock)
            {
                var window = GetActiveWindow();
                if (window == IntPtr.Zero)
                {
                    return null;
                }

                var pid = GetWindowPid(window);
                if (pid <= 0)
                {
                    pid = GetWindowPidViaXRes(window);
                }

                if (pid <= 0)
                {
                    return null;
                }

                return GetProcessName(pid);
            }
        }

        private IntPtr GetActiveWindow()
        {
            if (XGetWindowProperty(_display, _root, _atomActiveWindow, IntPtr.Zero, (IntPtr)1, 0, AnyPropertyType,
                    out _, out var format, out var nItems, out _, out var prop) != Success || prop == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (format != 32 || nItems == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                // 32-bit format properties are stored as native longs
                return Marshal.ReadIntPtr(prop);
            }
            finally
            {
                XFree(prop);
            }
        }

        private int GetWindowPid(IntPtr window)
        {
            if (_atomWmPid == IntPtr.Zero)
            {
                return 0;
            }

            if (XGetWindowProperty(_display, window, _atomWmPid, IntPtr.Zero, (IntPtr)1, 0, AnyPropertyType,
                    out _, out var format, out var nItems, out _, out var prop) != Success || prop == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                if (format != 32 || nItems == IntPtr.Zero)
                {
                    return 0;
                }

                return (int)Marshal.ReadIntPtr(prop);
            }
            finally
            {
                XFree(prop);
            }
        }

        private int GetWindowPidViaXRes(IntPtr window)
        {
            try
            {
                var spec = new XResClientIdSpec { Client = window, Mask = XRES_CLIENT_ID_PID_MASK };
                if (XResQueryClientIds(_display, (IntPtr)1, ref spec, out var numIds, out var clientIds) != Success
                    || clientIds == IntPtr.Zero)
                {
                    return 0;
                }

                try
                {
                    var count = (long)numIds;
                    var size = Marshal.SizeOf<XResClientIdValue>();
                    for (long i = 0; i < count; i++)
                    {
                        var value = Marshal.PtrToStructure<XResClientIdValue>(clientIds + (nint)(i * size));
                        var pid = XResGetClientPid(ref value);
                        if (pid > 0)
                        {
                            return pid;
                        }
                    }

                    return 0;
                }
                finally
                {
                    XResClientIdsDestroy(numIds, clientIds);
                }
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
            catch (EntryPointNotFoundException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Resolves a pid to a process name. /proc/{pid}/comm truncates at 15 chars,
        /// so prefer the basename of cmdline's argv[0], which keeps full Windows
        /// executable names for Proton/Wine games.
        /// </summary>
        private static string? GetProcessName(int pid)
        {
            try
            {
                var cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
                var argv0 = cmdline.Split('\0', 2)[0];
                if (!string.IsNullOrWhiteSpace(argv0))
                {
                    var lastSlash = argv0.LastIndexOfAny(['/', '\\']);
                    var name = lastSlash >= 0 ? argv0[(lastSlash + 1)..] : argv0;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }
            catch
            {
                // process may have exited; fall through to comm
            }

            try
            {
                var comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
                return string.IsNullOrWhiteSpace(comm) ? null : comm;
            }
            catch
            {
                return null;
            }
        }
    }
}
