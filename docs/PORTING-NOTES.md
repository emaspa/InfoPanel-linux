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

3. **Model database beats device-reported resolution.** TrofeoBulk init
   responses have been seen reporting wrong sizes (599 rows on a 480-row
   panel); TRCC ignores the field entirely. Reported values are accepted
   only when they match the database.

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
