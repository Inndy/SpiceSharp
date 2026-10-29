# 0028. Fast JPEG decoding and the preferred image compression are settings; LZ4 is advertised

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human (2026-10-30: a fast JPEG switch, off by default; decode LZ4 images and let the user prefer
  them, with the server's choice as the default). The details are by agent.

- **Context:**
  - libjpeg-turbo's `TJPARAM_FASTDCT=1` with `TJPARAM_FASTUPSAMPLE=1` decodes 720p JPEG about 12–13% faster. The
    output differs slightly from the accurate decoder: PSNR about 44.7 dB, maximum difference 7 (the human's
    measurement, 2026-10-30). The golden images use the accurate decoder.
  - [0027](0027-native-dlls-trimmed-with-gc-sections-and-full-lto-lz4-decoder-in-spicecodec.md) added
    `sc_lz4_decode` to spicecodec.dll. [0004](0004-lz4-is-not-advertised.md) had declined to advertise LZ4.
  - spice-server sends LZ4 images only when its image compression is LZ4, set by its own configuration or by the
    client's `SPICE_MSGC_DISPLAY_PREFERRED_COMPRESSION`, and the client advertises
    `SPICE_DISPLAY_CAP_LZ4_COMPRESSION`.
- **Decision:**
  - **`fastJpeg`** (global bool in settings.json, default `false`):
    - It applies to every JPEG decode: MJPEG stream frames (the session's decoder), and JPEG and JPEG_ALPHA images
      (the canvas's decoder).
    - `JpegDecoder.Fast` is a flag that any thread may set. The next decode compares it with what the tj3 handle has
      and calls `tj3Set` for `TJPARAM_FASTDCT` (10) and `TJPARAM_FASTUPSAMPLE` (9) only when it changed. There is no
      per-frame `tj3Set` and no allocation. The values are the enum positions in libjpeg-turbo 3.1.2's `turbojpeg.h`.
    - Parsing is lenient: JSON `true` or the string `"true"` is on, anything else is off, and the rest of the file is
      kept.
    - The window menu has a checkable "Fast JPEG decoding (slightly lower quality)" item after "Video pacing". A
      change applies to the running session at its next JPEG decode and is saved as the other globals are
      ([0024](0024-settings-portable-by-default-opt-in-install.md)).
  - **LZ4 images** (`ImageDecoder`, following spice-common's `canvas_get_lz4`):
    - `data[0]` is top_down (nonzero means top-down) and `data[1]` is the SPICE bitmap format: 16BIT (x1r5g5b5),
      24BIT, 32BIT (xRGB) or RGBA. Any other format is a `CanvasException`.
    - The rest decodes to tightly packed rows, bottom row first unless top-down. The decoded size must be exactly
      width × height × bytes per pixel, or the image is a `CanvasException`.
    - Conversion to B8G8R8A8: 32BIT is made opaque; RGBA keeps its alpha and sets `HasAlpha`; 24BIT is expanded;
      16BIT is expanded as bitmaps are (5 to 8 bits as `c << 3 | c >> 2`, the top bit ignored).
    - 32-bit top-down data decodes straight into the image. The other cases decode into an `ArrayPool` scratch
      buffer and are converted from there, flipping bottom-up rows. Nothing is allocated per image once warm.
  - **Advertising:** the display channel advertises `SPICE_DISPLAY_CAP_LZ4_COMPRESSION` when spicecodec.dll exports
    `sc_lz4_decode`. The export is probed once with `NativeLibrary.TryGetExport`, because users may replace
    spicecodec.dll with an older build. This supersedes 0004.
  - **`imageCompression`** (global string in settings.json):
    - Values: `auto` (default) and `lz4`. Anything else reads as `auto`, which leaves room for more values later.
    - `auto` sends nothing, so the server decides.
    - `lz4` makes the session send `SPICE_MSGC_DISPLAY_PREFERRED_COMPRESSION` (103) with one byte,
      `SPICE_IMAGE_COMPRESSION_LZ4` (7). It is sent right after `DISPLAY_INIT`, as spice-gtk does, and again when
      the user changes the setting. One Info line is logged each time.
    - The message is sent only if the server's display channel has `SPICE_DISPLAY_CAP_PREF_COMPRESSION` (6) and the
      LZ4 decoder is present. Otherwise an Info line says why nothing was sent.
    - Switching back to `auto` during a session sends nothing: the protocol has no "server default" value. The server
      keeps LZ4 until the next connection, and an Info line says so. Sending `AUTO_GLZ` instead was rejected. It is
      only QEMU's default, so it would change a server configured otherwise.
    - The window menu has an "Image compression" submenu after the fast JPEG item, with "Server default" and
      "LZ4 (fast, more bandwidth)".
  - References for the message:
    - `refs/spice-protocol/spice/enums.h`: `SpiceImageCompression` (`INVALID`=0 … `LZ4`=7) and
      `SPICE_MSGC_DISPLAY_INIT = 101, STREAM_REPORT, PREFERRED_COMPRESSION`.
    - `refs/spice-common/spice.proto`: `message { image_compression image_compression; } @declare
      preferred_compression`, where `image_compression` is an `enum8`.
    - `refs/spice-gtk/src/channel-display.c`: `spice_display_channel_change_preferred_compression` checks
      `SPICE_DISPLAY_CAP_PREF_COMPRESSION`, and the display init sends the preference after `DISPLAY_INIT`.
- **Reason:**
  - Fast JPEG trades a little quality for speed, which ranks first in [principles.md](../design/principles.md). It
    stays off by default so the goldens and the accurate output stay unchanged; the human chose that default.
  - LZ4 decodes faster than GLZ or QUIC but uses more bandwidth. That suits a LAN, not every link, so the server's
    choice stays the default.
  - Costs:
    - 16- and 24-bit LZ4 images and bottom-up 32-bit ones pass through a pooled scratch buffer of width × height ×
      bytes per pixel, kept by `ArrayPool`.
    - Fast JPEG differs most on sharp saturated colour edges, where fast upsampling repeats chroma instead of
      interpolating it. A synthetic 256×144 test image with such an edge differs by 1.32 on average and by at most 63.
- **Measurements:**
  - `AllocationTests.Lz4ImageDrawDoesNotAllocateAfterWarmup`: 1000 warmed DRAW_COPYs of 160×90 LZ4 images allocate
    0 B for 32-bit top-down, RGBA bottom-up and 24-bit bottom-up.
