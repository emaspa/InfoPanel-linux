# Valkyrie MC360 LCD (640×480, experimental)

This driver has not run on a physical MC360 yet. The device row on the Devices
page says so. It shares nothing with the V360 HID/UYVY path.

## Identification

A device has to pass all of these checks before it's added or started:

- USB `374A:A021` with product string `Vk-Copl-08` (case-insensitive).
- A valid mode-2 reply to the open handshake and the screen-info command.
- Screen info reporting width 640, height 480 and a 614400-byte framebuffer.
  The `F001` pixel-format variant is rejected.

The serial, when there is one, selects the device. Otherwise the USB device path
does. Geometry is queried again on every connection. Other BoWei product IDs,
product strings or geometries never get the MC360 name. A scan only handshakes
and reads screen info; it doesn't set brightness or send an image. The
`374a:a021` USB udev rule is enough, since there is no HID interface.

## Wire protocol

Interface 0 uses bulk OUT `02` and bulk IN `81`. A request is a little-endian
16-bit command followed by a 32-bit payload length. A reply echoes the command
ORed with `8000`, and its declared length must match what arrived. A nonzero
four-byte status reply fails the operation.

Opening sends command `1003` with payload `45 23 01 00`, then command `0001` for
the 28-byte screen-info structure. Width, height and framebuffer length sit at
byte offsets 12, 16 and 24. A streaming connection sets brightness (`0002`) to
100, and the profile's brightness setting dims the pixels in software.

Frames are little-endian BGR565. Command `1001` announces the four-byte byte
count, and raw pixels follow only after it's acknowledged, in chunks of up to
`0x20000` bytes. Every transfer that is a multiple of `0x1000` bytes is followed
by a zero-length packet. Static profiles are sent when their content or options
change, and idle connections send `000F` with `78 56 34 12` once a second.
Screen-off sends a black frame. On exit the driver releases interface 0 and
leaves the last image up. It doesn't reboot the device or send the vendor close
command, which blanks the screen.

## Sources and what's left to test

The [MythCool-Lite compatibility table](https://github.com/ZGMFX01A/MythCool-Lite/blob/342f8a330a5e8d83e229395fecf11bafd56a1158/README.md#硬件与驱动兼容性清单)
lists the MC360 with this USB descriptor and 640×480 geometry, and its author
reports testing it on Windows. That project's device code is private. The A021
mode-2 packets and BGR565 framing come from
[codehz/msdisplay](https://github.com/codehz/msdisplay/blob/34dd469d83c199a16832994755d2352495148a4e/bowei_aio/display.py).
This transport was written from those protocol facts and includes none of that
source, vendor binaries or firmware.

Tests cover the descriptor and geometry checks, command byte order, malformed
replies, screen-info rejection, BGR565 channel order and row stride, chunk and
zero-length-packet order, failed acknowledgements and settings serialization.
Someone with the hardware still has to check initialization, colors,
orientation, static-image retention, brightness, screen-off, reconnect and
suspend/resume on Linux.

The B360 and B360-GT are left out on purpose: their profiles share material
codes, and other MS resolutions aren't mapped to a retail model. The
[vendor download page](https://valkyrie.com.cn/) also lists R, AL, ML and DX air
coolers, but a family name or an advertised resolution isn't enough to know
which protocol a model speaks, so none of them are listed.
