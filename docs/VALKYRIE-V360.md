# Valkyrie V360 LCD (320×240)

The V360 screen uses the MacroSilicon MS9132 bridge (`345f:9132`). It is managed
under **VMAX / AuyiHomu / Valkyrie**, reusing the existing UYVY renderer and device
supervisor. Existing VMAX configuration names and XML remain compatible.

## Identification and access

The same USB ID appears on several unrelated screens. Discovery reads the first
EDID detailed timing descriptor: 320×240 selects V360, 320×960 selects VMAX,
and 376×960 belongs to the Jonsbo DS339 path. Unknown readable geometry is skipped.
An unreadable EDID does not select V360. A stable USB serial pairs the HID control
interface with the video interface; V360 open rechecks the geometry before init.
The existing `345f:9132` udev rules cover both USB and hidraw nodes.

## Transport

- HID feature reports use LinuxHidRawFeature, retaining the kernel HID driver.
  Both ioctl buffers include report ID 0 plus eight payload bytes.
- The V360-specific 77-command initialization is followed by complete UYVY frames
  on interface 3 / bulk OUT endpoint 0x04.
- Each frame is 153616 bytes: `FF 00 00 00 00 14 00 F0`, 153600 UYVY pixel bytes,
  and `FF C0 00 00 00 00 00 00`.
- Only after the first successful complete frame are the post-frame commands sent:
  `B5 F5 07` (read), `B6 F5 07 00`, `A6 05 01 00`, `B5 C5 55` (read).
- `B5 00 32` is polled every 30 subsequent frames. Static profiles are resent,
  and brightness/rotation/profile changes invalidate the cached conversion.
- Brightness is software dimming. JPEG quality is irrelevant to raw UYVY and is
  hidden in this family's panel options. No pump/fan/RGB commands are sent.

## Validation

I tested a standalone Linux implementation on my V360 under Fedora 44 / Linux 7.2
and confirmed that its test clock, text and color bars displayed correctly.
Its 77-command initialization was compared byte-for-byte with LCD Fusion.
The regression fixture digest includes every padded payload and read-reply flag.

I then tested the integrated InfoPanel AppHost and profile renderer on the same
hardware. Discovery selected V360 and excluded the Jonsbo path. My profile with
text, a live clock and CPU temperature streamed at approximately 9 FPS with a
10 FPS target, and I confirmed correct colors and orientation. I changed and
restored software brightness and 180-degree rotation during streaming. I also
verified that stopping and restarting the device task reacquired both interfaces
and that clean shutdown released them. I have not tested physical suspend/resume.

The solution test suite passes 351 tests, including geometry routing, the
initialization trace digest, post-frame command order and XML settings round-trip.
Other Valkyrie models or panels using the same USB ID have not been verified.
The [MC360](VALKYRIE-MC360.md) has a separate experimental bulk transport and does
not use this initialization. Unmapped generic resolutions are not model entries.

## Attribution

The initialization sequence is adapted from [LCD Fusion](https://github.com/itsmylife44/LcdFusion),
MIT © 2026 itsmylife44. The full notice is preserved in `ValkyrieV360Protocol.cs`.
