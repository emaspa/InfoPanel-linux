# Device-stack porting notes (vs InfoPanel for Windows fork)

The device code is kept line-for-line identical to
[emaspa/infopanel-1](https://github.com/emaspa/infopanel-1) `all-changes`
wherever possible, so fixes can be cherry-picked in both directions. A full
parity audit (2026-07-16) confirmed the protocol logic, timings, ACK
handling, init sequences and model databases are byte-identical apart from
the items below.

## Approved deviations

1. **TrofeoBulk JPEG encoding: SkiaSharp instead of GDI+.**
   System.Drawing does not exist on Linux. libjpeg-turbo uses 4:2:0 chroma
   subsampling at all qualities (the property TRCC compatibility needs);
   the 230 KB adaptive-quality cap is unchanged. Byte output differs from
   GDI+ at the same quality number — expected and harmless.

2. **USB device-id self-heal.** libusb ids (`usbdevBUS.DEV`) change on
   every replug, so a saved id never matches again. When the saved id is
   absent and exactly one device with the right VID/PID is present, the
   task rebinds and persists the new id. (The Turing task carries the v1
   port's first-match variant of the same idea.)

3. **(Retired)** ~~Model database beats device-reported resolution.~~ This
   was our workaround for 5408 units reporting 1920x599. The fork later
   solved it properly (June 2026): the reported height plus init-response
   byte[20] re-identify the exact variant — 9.16" v1 (reports 480),
   9.16" v2 (reports 599, renders 480 + opt-in flicker fix), 11.3"
   (byte[20]=0x05, renders 1920x400). We now carry that logic verbatim.

4. **BackgroundTask: per-instance start/stop lock, 5 s stop grace.**
   The fork used one process-wide static lock (a wedged device stalled
   every task) and waited on stop forever. A stop that exceeds the grace
   period abandons the task; the reset-on-reconnect path recovers the
   claimed interface.

## Linux-required adaptations

- `DeviceProperties["DeviceID"/"LocationInformation"]` → `TryGetValue`
  with `DevicePath` fallback (libusb exposes no Windows registry props).
- SCSI transport is Linux SG_IO (`IScsiTransport`); zero-length data
  phases (TEST UNIT READY) use `SG_DXFER_NONE`, which SPTI tolerated
  implicitly.
- Turing serial discovery uses sysfs; CT13/CT21INCH companion-port
  detection re-expressed on sysfs VID/PID.
- UI-thread marshaling in device model classes goes through the UiThread
  seam instead of the WPF dispatcher.

## Hotkeys (PR #99)

The Windows build registers global hotkeys with Win32 `RegisterHotKey`. Here
they are X11 `XGrabKey` grabs on the root window (all Caps/Num lock variants),
behind `IGlobalHotkeyService`. `HotkeyBinding.ModifierKeys`/`Key` are strings
carrying the exact WPF enum text ("Control, Alt" / "F5") so settings.xml
round-trips with Windows unchanged. Caveats: under a Wayland session the grab
lives in XWayland and only fires while an X11 window has focus (fully global
on X11 sessions), and combos owned by the compositor (e.g. Ctrl+Alt+F-keys)
fail to grab and are logged.
