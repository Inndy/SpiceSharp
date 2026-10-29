# Does the PVE node's spice-server encode video with GStreamer?

2026-10-30. Host: the PVE node (libspice-server1 0.15.2, linked against GStreamer core/app/base). The human installed
the GStreamer plugin packages (base, good, ugly, libav) on the node shortly before this test. Guest: `win7`, whose
VM has `streaming-video=all`; its QEMU process was **not** restarted after the install. Build: `main` after the
initial commit, with `spice-cli streams` added.

## Method

spice-server picks a stream's codec from its own list (`spice:mjpeg` first by default, then the GStreamer codecs)
among the codecs the client advertises. The viewer advertises only MJPEG, so it always gets the built-in encoder.
`spice-cli streams <file.vv> <codec> [seconds]` advertises one codec instead (session option
`VideoCodecCapsOverride`) and prints the streams the server opens. Any codec other than MJPEG can only come from a
GStreamer encoder; a codec the server can't encode opens no stream.

A PowerShell window in the guest's interactive session repainted a 480×320 photo-like area about 30 times a second
for 12 s (started over WinRM through a scheduled task), and each probe ran for 25 s from a fresh `.vv`.

## Result

| Advertised | Stream opened | Frames received |
|---|---|---|
| MJPEG | MJPEG 480×320 | 498 (a previous animation run overlapped this one) |
| H.264 | H.264 | 43 |
| VP8 | VP8 | 51 |
| VP9 | VP9 | 44 |

- The node encodes H.264, VP8 and VP9 through GStreamer. The QEMU process found the newly installed plugins without
  a restart: spice-server initializes GStreamer only when it first creates a GStreamer encoder, which this process
  had never done because the viewer only advertises MJPEG. A process that had already initialized GStreamer before
  plugins were installed would need a full VM stop and start (`qm reboot`, not a guest reboot).
- The probe can't decode these codecs, so it drops every frame and reports the drops back. The low frame counts
  (about 4 per second against the guest's 30) therefore don't measure encoder speed: spice-server lowers its frame
  rate when the client reports drops, and the GStreamer encoders run in software on the node's CPU. A real
  comparison needs a client that decodes them.
- Not measured: bandwidth per codec, the node's CPU cost per stream, and image quality.
