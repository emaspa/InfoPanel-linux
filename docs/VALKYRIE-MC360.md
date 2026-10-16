# Valkyrie MC360 LCD

This is an **experimental Linux implementation** for one identified retail model.
The device title is `Valkyrie MC360 LCD`; the device-row subtitle marks its
experimental status. No physical MC360 has been tested with this implementation.
The existing V360 hardware-tested HID/UYVY path remains separate.

## Identification

All of these must match before a device can be added or started:

- USB `374A:A021`, product string `Vk-Copl-08` (case-insensitive).
- A valid mode-2 response to the open handshake and screen-info command.
- Explicit screen-info width 640, height 480, framebuffer length 614400 bytes.
- The `F001` pixel-format variant is rejected.

A serial, when available, selects the exact device. Otherwise its USB device path
is used. The screen geometry is queried again on every connection. Other BoWei
PIDs, other product strings and other geometries are not assigned an MC360 name.
Discovery only handshakes and reads info; it does not set brightness or send images.
The `374a:a021` USB udev rule covers this model; there is no HID requirement.

## Wire contract

Interface 0 uses bulk OUT `02` and bulk IN `81`. Requests have a little-endian
16-bit command and 32-bit payload length. Responses must echo command OR `8000`,
with the declared and actual response lengths matching. Four-byte nonzero status
responses fail the operation.

Open sends command `1003`, payload `45 23 01 00`, then command `0001` for the
28-byte screen-info structure. Width, height and framebuffer length are at byte
offsets 12, 16 and 24. A streaming connection sets brightness (`0002`) to 100;
the profile brightness control then dims pixels in software.

A frame is little-endian BGR565. Command `1001` announces its four-byte byte count;
only after its successful acknowledgement are raw pixels sent in chunks up to
`20000` hex bytes. Every transfer aligned to `1000` hex bytes gets a zero-length
packet. Static profiles update when content/options change; idle sessions send
`000F` with `78 56 34 12`. Screen-off sends a black image; exit retains the last
image and releases interface 0, without rebooting or sending the vendor close
command that blanks the screen.

## Evidence and scope

- The [MythCool-Lite compatibility table](https://github.com/ZGMFX01A/MythCool-Lite/blob/342f8a330a5e8d83e229395fecf11bafd56a1158/README.md#硬件与驱动兼容性清单)
  identifies MC360, its USB descriptor and 640×480 geometry, with author-reported
  physical validation on Windows. Its current device core is private.
- [Published BoWei wire research](https://github.com/codehz/msdisplay/blob/34dd469d83c199a16832994755d2352495148a4e/bowei_aio/display.py)
  documents A021 mode-2 packets and BGR565 framing. This transport was written
  independently from those protocol facts; it does not include that source,
  a private dependency, vendor binaries or firmware.
- Automated tests cover descriptor and geometry restrictions, command byte order,
  malformed replies, screen-info rejection, BGR565 channel order and row stride,
  transfer chunk/ZLP order, failed acknowledgements and XML settings.

These checks verify software behavior, not the physical MC360 display. A tester
must still confirm initialization, colors, orientation, static-image retention,
brightness, screen-off, reconnect and suspend/resume on Linux.

The catalog deliberately excludes B360/B360-GT shared material-code profiles and
unmapped MS resolutions. The [vendor download page](https://valkyrie.com.cn/)
mentions R/AL/ML/DX air-cooler families, but a family name or advertised resolution
is insufficient to assign a protocol to a retail model. No air-cooler entry is
added without an explicit model-to-protocol mapping.
