using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace InfoPanel.Platform.Linux
{
    /// <summary>
    /// Global hotkeys via X11 XGrabKey on the root window (the Linux equivalent of
    /// Win32 RegisterHotKey). Each combo is grabbed with all Caps/Num lock variants.
    ///
    /// Under a Wayland session this connects to XWayland, where root-window grabs
    /// only deliver while an X11 window has focus — reported via Limitation.
    /// A failed grab (combo taken by another client) is swallowed by a custom X
    /// error handler instead of killing the process.
    /// </summary>
    public sealed class X11GlobalHotkeyService : IGlobalHotkeyService
    {
        private static readonly ILogger Logger = Log.ForContext<X11GlobalHotkeyService>();

        private const int KeyPress = 2;
        private const int GrabModeAsync = 1;

        private const uint ShiftMask = 1 << 0;
        private const uint LockMask = 1 << 1;   // Caps Lock
        private const uint ControlMask = 1 << 2;
        private const uint Mod1Mask = 1 << 3;   // Alt
        private const uint Mod2Mask = 1 << 4;   // Num Lock
        private const uint Mod4Mask = 1 << 6;   // Super

        private const uint IgnoredLockBits = LockMask | Mod2Mask;

        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? display);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport("libX11.so.6")] private static extern ulong XStringToKeysym(string keysym);
        [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);
        [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
        [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
        [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
        [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, IntPtr eventBuffer);
        [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, int discard);
        [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);

        private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

        // Keep the delegate alive for the lifetime of the process
        private static readonly XErrorHandler IgnoreGrabErrors = (_, _) => 0;

        private readonly IntPtr _display;
        private readonly IntPtr _root;
        private readonly List<(byte Keycode, uint Modifiers, Action Callback)> _registrations = [];
        private readonly Lock _lock = new();
        private Thread? _eventThread;
        private volatile bool _running;

        public bool IsAvailable { get; }
        public string? Limitation { get; }

        public X11GlobalHotkeyService()
        {
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                Logger.Warning("X11GlobalHotkeyService: no X display available, hotkeys disabled");
                IsAvailable = false;
                return;
            }

            XSetErrorHandler(IgnoreGrabErrors);
            _root = XDefaultRootWindow(_display);
            IsAvailable = true;

            if (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
            {
                Limitation = "Wayland session: hotkeys are grabbed via XWayland and only fire while an X11 window has focus.";
                Logger.Information("X11GlobalHotkeyService: {Limitation}", Limitation);
            }
        }

        public bool Register(HotkeyModifierMask modifiers, string keyName, Action callback)
        {
            if (!IsAvailable) return false;

            var keysymName = KeyNameToKeysym(keyName);
            if (keysymName == null)
            {
                Logger.Warning("X11GlobalHotkeyService: unsupported key name '{Key}'", keyName);
                return false;
            }

            var keysym = XStringToKeysym(keysymName);
            if (keysym == 0)
            {
                Logger.Warning("X11GlobalHotkeyService: no keysym for '{Keysym}'", keysymName);
                return false;
            }

            var keycode = XKeysymToKeycode(_display, keysym);
            if (keycode == 0)
            {
                Logger.Warning("X11GlobalHotkeyService: no keycode for keysym '{Keysym}'", keysymName);
                return false;
            }

            uint mask = 0;
            if (modifiers.HasFlag(HotkeyModifierMask.Shift)) mask |= ShiftMask;
            if (modifiers.HasFlag(HotkeyModifierMask.Control)) mask |= ControlMask;
            if (modifiers.HasFlag(HotkeyModifierMask.Alt)) mask |= Mod1Mask;
            if (modifiers.HasFlag(HotkeyModifierMask.Super)) mask |= Mod4Mask;

            lock (_lock)
            {
                // Grab all Caps/Num lock combinations so the hotkey works regardless of lock state
                foreach (var lockBits in new uint[] { 0, LockMask, Mod2Mask, LockMask | Mod2Mask })
                {
                    XGrabKey(_display, keycode, mask | lockBits, _root, 0, GrabModeAsync, GrabModeAsync);
                }

                XSync(_display, 0);
                _registrations.Add((keycode, mask, callback));
                EnsureEventThread();
            }

            Logger.Debug("X11GlobalHotkeyService: grabbed keycode {Keycode} mask 0x{Mask:X}", keycode, mask);
            return true;
        }

        public void UnregisterAll()
        {
            if (!IsAvailable) return;

            lock (_lock)
            {
                foreach (var (keycode, mask, _) in _registrations)
                {
                    foreach (var lockBits in new uint[] { 0, LockMask, Mod2Mask, LockMask | Mod2Mask })
                    {
                        XUngrabKey(_display, keycode, mask | lockBits, _root);
                    }
                }

                XSync(_display, 0);
                _registrations.Clear();
            }
        }

        private void EnsureEventThread()
        {
            if (_eventThread != null) return;

            _running = true;
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "X11-Hotkeys" };
            _eventThread.Start();
        }

        private void EventLoop()
        {
            // XKeyEvent (x64): state at offset 80, keycode at offset 84
            var buffer = Marshal.AllocHGlobal(192);
            try
            {
                while (_running)
                {
                    while (_running && XPending(_display) > 0)
                    {
                        XNextEvent(_display, buffer);
                        if (Marshal.ReadInt32(buffer, 0) != KeyPress) continue;

                        var state = (uint)Marshal.ReadInt32(buffer, 80) & ~IgnoredLockBits;
                        var keycode = (byte)Marshal.ReadInt32(buffer, 84);

                        Action? callback = null;
                        lock (_lock)
                        {
                            foreach (var reg in _registrations)
                            {
                                if (reg.Keycode == keycode && reg.Modifiers == state)
                                {
                                    callback = reg.Callback;
                                    break;
                                }
                            }
                        }

                        try
                        {
                            callback?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "X11GlobalHotkeyService: hotkey callback failed");
                        }
                    }

                    Thread.Sleep(30);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Maps WPF Key enum names (the settings vocabulary) to X11 keysym names.</summary>
        private static string? KeyNameToKeysym(string keyName)
        {
            if (string.IsNullOrEmpty(keyName) || keyName == "None") return null;

            // Letters: A..Z -> a..z
            if (keyName.Length == 1 && keyName[0] is >= 'A' and <= 'Z')
                return char.ToLowerInvariant(keyName[0]).ToString();

            // Top-row digits: D0..D9 -> 0..9
            if (keyName.Length == 2 && keyName[0] == 'D' && char.IsDigit(keyName[1]))
                return keyName[1].ToString();

            // Function keys F1..F24 pass through
            if (keyName.Length is 2 or 3 && keyName[0] == 'F' && int.TryParse(keyName[1..], out var f) && f is >= 1 and <= 24)
                return keyName;

            // Numpad digits
            if (keyName.StartsWith("NumPad", StringComparison.Ordinal) && keyName.Length == 7 && char.IsDigit(keyName[6]))
                return $"KP_{keyName[6]}";

            return keyName switch
            {
                "Left" or "Right" or "Up" or "Down" or "Home" or "End"
                    or "Insert" or "Delete" or "Escape" or "Pause" or "Menu" or "Tab" => keyName,
                "PageUp" or "Prior" => "Prior",
                "PageDown" or "Next" => "Next",
                "Space" => "space",
                "Return" or "Enter" => "Return",
                "Back" => "BackSpace",
                "PrintScreen" or "Snapshot" => "Print",
                "Scroll" => "Scroll_Lock",
                "OemPlus" => "equal",
                "OemMinus" => "minus",
                "OemComma" => "comma",
                "OemPeriod" => "period",
                "OemQuestion" => "slash",
                "OemSemicolon" or "Oem1" => "semicolon",
                "OemQuotes" or "Oem7" => "apostrophe",
                "OemOpenBrackets" or "Oem4" => "bracketleft",
                "OemCloseBrackets" or "Oem6" => "bracketright",
                "OemPipe" or "Oem5" => "backslash",
                "OemTilde" or "Oem3" => "grave",
                "Add" => "KP_Add",
                "Subtract" => "KP_Subtract",
                "Multiply" => "KP_Multiply",
                "Divide" => "KP_Divide",
                "Decimal" => "KP_Decimal",
                _ => null,
            };
        }

        public void Dispose()
        {
            _running = false;
            _eventThread?.Join(500);
            _eventThread = null;

            if (IsAvailable)
            {
                UnregisterAll();
                XCloseDisplay(_display);
            }
        }
    }
}
