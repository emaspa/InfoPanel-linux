using System;
using System.Runtime.InteropServices;

namespace InfoPanel.Gpu
{
    /// <summary>
    /// Minimal EGL bindings for headless rendering on a chosen GPU through
    /// EGL_EXT_platform_device. Works with any vendor libEGL behind glvnd
    /// (NVIDIA proprietary, Mesa radeonsi/iris/...), needs no X or Wayland
    /// display, and lets us pick the GPU explicitly instead of whatever the
    /// desktop session runs on.
    /// </summary>
    internal static class Egl
    {
        private const string Lib = "libEGL.so.1";

        public const int EGL_NONE = 0x3038;
        public const int EGL_EXTENSIONS = 0x3055;
        public const int EGL_VENDOR = 0x3053;
        public const int EGL_DRM_DEVICE_FILE_EXT = 0x3233;
        public const int EGL_DRM_RENDER_NODE_FILE_EXT = 0x3377;
        public const int EGL_RENDERER_EXT = 0x335F;
        public const int EGL_PLATFORM_DEVICE_EXT = 0x313F;
        public const int EGL_OPENGL_API = 0x30A2;
        public const int EGL_SURFACE_TYPE = 0x3033;
        public const int EGL_PBUFFER_BIT = 0x0001;
        public const int EGL_RENDERABLE_TYPE = 0x3040;
        public const int EGL_OPENGL_BIT = 0x0008;
        public const int EGL_RED_SIZE = 0x3024;
        public const int EGL_GREEN_SIZE = 0x3023;
        public const int EGL_BLUE_SIZE = 0x3022;
        public const int EGL_ALPHA_SIZE = 0x3021;
        public const int EGL_WIDTH = 0x3057;
        public const int EGL_HEIGHT = 0x3056;
        public const int GL_RENDERER = 0x1F01;

        [DllImport(Lib)]
        public static extern IntPtr eglGetProcAddress(string name);

        [DllImport(Lib)]
        public static extern bool eglInitialize(IntPtr dpy, out int major, out int minor);

        [DllImport(Lib)]
        public static extern bool eglTerminate(IntPtr dpy);

        [DllImport(Lib)]
        public static extern IntPtr eglQueryString(IntPtr dpy, int name);

        [DllImport(Lib)]
        public static extern bool eglBindAPI(int api);

        [DllImport(Lib)]
        public static extern bool eglChooseConfig(IntPtr dpy, int[] attribs, IntPtr[] configs, int size, out int num);

        [DllImport(Lib)]
        public static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int[] attribs);

        [DllImport(Lib)]
        public static extern bool eglDestroyContext(IntPtr dpy, IntPtr ctx);

        [DllImport(Lib)]
        public static extern IntPtr eglCreatePbufferSurface(IntPtr dpy, IntPtr config, int[] attribs);

        [DllImport(Lib)]
        public static extern bool eglDestroySurface(IntPtr dpy, IntPtr surface);

        [DllImport(Lib)]
        public static extern bool eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);

        [DllImport(Lib)]
        public static extern bool eglReleaseThread();

        [DllImport(Lib)]
        public static extern int eglGetError();

        // Extension entry points are only reachable through eglGetProcAddress.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public delegate bool QueryDevicesExt(int maxDevices, [Out] IntPtr[]? devices, out int numDevices);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr QueryDeviceStringExt(IntPtr device, int name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GetPlatformDisplayExt(int platform, IntPtr nativeDisplay, int[]? attribs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GlGetString(int name);

        public static T? Proc<T>(string name) where T : Delegate
        {
            var p = eglGetProcAddress(name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        public static string? String(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        public static string ErrorText() => "0x" + eglGetError().ToString("X4");
    }
}
