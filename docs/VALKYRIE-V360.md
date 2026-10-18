# Valkyrie V360 LCD (320×240)

The V360 screen sits behind a MacroSilicon MS9132 bridge (`345f:9132`), the same
chip the VMAX 4.6" and the Jonsbo DS339 use. It has its own Valkyrie section on
the Devices page with a separate streaming switch, and it reuses the VMAX UYVY
renderer and device supervisor.

## Identification

The USB ID alone can't tell these panels apart, so the scan reads the first
detailed timing descriptor of the panel EDID. 320×240 is a V360 and 376×960 is a
DS339. Any other readable geometry stays on the VMAX path, as it did before the
V360 existed, because no VMAX EDID has been captured yet. A panel whose EDID
can't be read is skipped, and the scan status asks you to check USB/HID
permissions.

The V360 driver pairs the HID control interface with the video interface by USB
serial, and rechecks the geometry before it initializes the panel. The VMAX
driver skips any HID interface whose EDID says V360 or DS339, so a VMAX entry
never initializes the wrong screen.

Earlier releases added a V360 as a VMAX 4.6", or as a DS339 when the EDID was
unreadable. A scan that confirms the V360 replaces that entry and keeps its
profile, enabled state, brightness, rotation and hotkeys.

The `345f:9132` udev rules cover both the USB and hidraw nodes. The deb, RPM and
AUR packages re-trigger udev for already-connected V360 and MC360 screens, so
they don't need a replug after install.

## Transport

HID feature reports go through `LinuxHidRawFeature`, which keeps the kernel HID
driver bound. Each ioctl buffer holds report ID 0 plus eight payload bytes.

After a V360-specific sequence of 77 commands, the driver sends complete UYVY
frames to interface 3, bulk OUT endpoint `0x04`. A frame is 153616 bytes:
`FF 00 00 00 00 14 00 F0`, then 153600 bytes of UYVY pixels, then
`FF C0 00 00 00 00 00 00`.

Scan-out starts after the first complete frame, with `B5 F5 07` (read),
`B6 F5 07 00`, `A6 05 01 00` and `B5 C5 55` (read). After that the driver polls
`B5 00 32` every 30 frames. Static profiles are resent, and a brightness,
rotation or profile change invalidates the cached conversion.

Brightness is software dimming. JPEG quality doesn't apply to raw UYVY, so the
panel options hide it. The driver sends no pump, fan or RGB commands.

## Testing

[EsiksonX](https://github.com/EsiksonX) tested this on a V360 under Fedora 44
with Linux 7.2. Discovery picked the V360 and left the Jonsbo path alone. A
profile with text, a live clock and CPU temperature streamed at about 9 FPS
against a 10 FPS target, with correct colors and orientation. Brightness and
180° rotation changed live while streaming. Stopping and restarting the device
task reacquired both interfaces, and a clean shutdown released them. Suspend and
resume have not been tested on the V360.

Tests pin the 77-command sequence with a digest of every padded payload and
read flag, compared byte for byte with LCD Fusion. They also cover the EDID
routing, the post-frame command order, settings round-trips, the separate family
switches and the migration of stale entries. No other Valkyrie model on this USB
ID has been verified. The [MC360](VALKYRIE-MC360.md) uses a different bulk
transport.

## Attribution

The initialization sequence is adapted from
[LCD Fusion](https://github.com/itsmylife44/LcdFusion), MIT © 2026 itsmylife44.
The full notice is in `ValkyrieV360Protocol.cs` and `LICENSES.md`.
