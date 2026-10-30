# Hardware video decoding: live check on win7 (2026-10-31)

Host: Windows 11 IoT Enterprise LTSC 2024, AMD Radeon 8060S, 3840x2160, no Store VP9 or HEVC Video Extensions. Guest:
`win7` (Windows 7 SP1), video streaming already enabled on the VM (`videostreaming=all`), so no Proxmox setting was
changed. The PVE node runs libspice-server1 0.15.2 with the GStreamer plugin packages the human installed on
2026-10-30. Workload: Windows Media Player playing the guest's own sample video (`Wildlife.wmv`, 1280x720, about 30 s),
started in the interactive session through a scheduled task. Every connection used a fresh `.vv` from
`tools/pve-vv.py`. Builds: portable zips from `pwsh tools/package.ps1` (Native AOT, no publish warnings):

| Build | Tree | Used for |
|---|---|---|
| A | `4779ed3` | run A |
| B1 | `4779ed3` plus the format check and fallback of `9c81fd6`, without its device release | menu, "Server default" run, first memory figures |
| B | `9c81fd6` (zip sha256 `e44d536a672da35d44586970a48600fe75cb3b40abe7eeff4a78a7c23043e64c`) | run B, software profile, final figures |
| main | the x64 release zip from 2026-10-30 (before hardware video) | memory and CPU baseline |

## Summary

| Check | Result | Notes |
|---|---|---|
| Does the server send H.264 when asked? | **yes, but as 4:4:4** | H.264 High 4:4:4 Predictive (profile 244); the hardware decoder probed here (Microsoft H264 Video Decoder MFT on the H264_VLD_NOFGT profile) doesn't take it |
| Hardware H.264 pictures from a live server | not possible on this host | the H.264 pipeline rests on the synthetic tests (Spice.Video.Tests) |
| Build A with that stream | fails safely, but badly | every H.264 stream failed on its first frame (`ProcessOutput` E_FAIL) and was reported; the server sent the video area as images |
| Build B: format check and MJPEG fallback | pass | profile 244 seen on the first frame; H.264 avoided for the session; the server opened MJPEG streams 0.15 s later |
| MJPEG playback | pass, no regression | 716 and 462 frames, 0 dropped; correct colours and orientation; CPU about the same as main |
| Probe and advertising log lines | pass | see below |
| "Video codec" menu | pass | radio items, VP9/HEVC grayed, each choice logged and saved |
| `SPICEVIEWER_FORCE_WARP=1` | pass | MJPEG only advertised |
| Idle after playback | pass | 0 ms CPU and 0% GPU over 60 s |
| Decoding device released when no stream decodes | pass | memory after playback within a few MB of "Server default" |
| Spice.Viewer.Tests | 98/98 | at `4779ed3` and again at `9c81fd6` |

## What was run

### Run A: build A, Automatic (default)

The probe and link:

```
video: H.264: hardware decoding with 'Microsoft H264 Video Decoder MFT' on 'AMD Radeon(TM) 8060S Graphics'
video: VP9: 'AMD Radeon(TM) 8060S Graphics' decodes it, but Media Foundation has no Direct3D 11 decoder for it (Microsoft's "VP9 Video Extensions" provide one)
video: HEVC: 'AMD Radeon(TM) 8060S Graphics' decodes it, but Media Foundation has no Direct3D 11 decoder for it (Microsoft's "HEVC Video Extensions" provide one)
video: probe took 141 ms
display: video codecs advertised: MJPEG, H264
display: asked the server to prefer video codecs H264
```

Playback started. The server opened H.264 streams for the video and for two small regions (WMP's title and seek bar),
and each one failed on its first frame:

```
video: decoding device on 'AMD Radeon(TM) 8060S Graphics'
video: H.264 stream decoder 'Microsoft H264 Video Decoder MFT' for 1280x720
display: stream 45 H264 1280x720 to 1280x720 on surface 0
video: H.264 decoding failed: ProcessOutput (0x80004005)
display: asked the server to send stream 45 as images
(the same for stream 40, 288x55, and stream 46, 1280x9)
```

So the server answers the preference with H.264 (its GStreamer plugins load), and the report makes it fall back to
images as designed. The cause: spice-server's pipeline is `appsrc ! videoconvert ! x264enc`, and videoconvert picks
Y444 for BGRx input unless `SPICE_CONVERTER_PREFERRED_FORMAT` is set (`server/gstreamer-encoder.c` 1043-1070,
1142-1144). Images instead of MJPEG would have made the default worse than before, so build B adds a format check and
a fallback (decision 0037, "Declared format first" and "A codec that can't be decoded is avoided").

### Run B: build B, Automatic (default)

```
video: H.264 stream decoder 'Microsoft H264 Video Decoder MFT' for 64x51
display: stream 40 H264 64x51 to 64x51 on surface 0
display: stream 40 is H.264 High 4:4:4 Predictive (profile 244), 4:4:4, 8-bit, which the hardware decoder can't decode
display: H264 video is off for this session (the server encodes it as H.264 High 4:4:4 Predictive (profile 244), 4:4:4, 8-bit); asking the server for other codecs
display: asked the server to prefer video codecs Mjpeg
video: no stream is decoding; decoding device released
display: asked the server to send stream 40 as images
display: stream 40 ended after 0 frames, 0 dropped, 0 hidden by its clip
display: stream 44 Mjpeg 1280x720 to 1280x720 on surface 0
display: stream 45 Mjpeg 1280x9 to 1280x9 on surface 0
display: stream 45 ended after 462 frames, 0 dropped, 0 hidden by its clip
display: stream 44 ended after 716 frames, 0 dropped, 0 hidden by its clip
```

The video played as MJPEG from 0.15 s after the first H.264 stream was refused. A screenshot during playback showed
the video with correct colours, right way up, inside WMP's window.

### "Video codec" menu (build B1, after the fallback)

Opened from the title bar's window menu. "Automatic (hardware decoding first)" was checked; "Server default",
"MJPEG" and "H.264" were enabled; "VP9" and "HEVC" were grayed. Choices and their log lines:

| Choice | Log | settings.json |
|---|---|---|
| MJPEG | `display: asked the server to prefer video codecs Mjpeg` | `"videoCodec": "mjpeg"` |
| Server default | `display: the server keeps the requested video codec order until the next connection` | `"server"` |
| Automatic | `display: asked the server to prefer video codecs Mjpeg` (H.264 is avoided for this session) | `"auto"` |

A new connection with "Server default" sent no preference, and the server streamed MJPEG (its own order), as before
this work.

### Software profile

With `SPICEVIEWER_FORCE_WARP=1`:

```
video: software profile (SPICEVIEWER_FORCE_WARP): MJPEG only
display: video codecs advertised: MJPEG
display: none of the preferred video codecs (H264, Vp9, H265) can be decoded here; the server's order stands
presenter: adapter 'Microsoft Basic Render Driver' software=True level=D3D_FEATURE_LEVEL_11_0
```

### CPU, GPU and memory

CPU is the viewer's processor time over wall time, as a share of all 32 logical processors. GPU is the mean of the
viewer's "GPU Engine" utilization counters, sampled every 5 s. Same video, same window size, same guest.

| Build, mode | During playback (about 25 s) | Idle, 60 s after playback | Private bytes after playback |
|---|---|---|---|
| main (before) | 1.16% CPU, 0.63% GPU | 16 ms CPU, 0% GPU | 302 MB |
| B1, Server default (no decoding device made) | not measured | 0 ms CPU, 0% GPU | 294 MB |
| B, Automatic (fallback to MJPEG) | 1.07% CPU, 0.65% GPU | 0 ms CPU, 0% GPU | 300 MB |
| B1, Automatic (decoding device kept), after three plays | not measured | 0 ms CPU, 0% GPU | 338 MB |

Build B1 kept its decoding device for the session after the refused stream, and its row is about 40 MB above the
others. That suggests the device's cost but doesn't isolate it: the B1 Automatic session had also played the video
three times. Build B releases the device when its last decoder closes; these idle figures cover that normal path only,
not a release after a device loss (see the follow-up review's N4, fixed later with a synthetic test). The high memory after playback (about 300 MB, also on
main) is the known retention after audio and video (see [2026-10-30-v0-dod.md](2026-10-30-v0-dod.md)), not this work.
These are dev-machine numbers; re-measure in a clean environment.

## Not done

- **Hardware H.264 pictures from a live server.** This needs a host whose spice-server encodes 4:2:0, for example
  QEMU started with `SPICE_CONVERTER_PREFERRED_FORMAT=I420`. Changing the PVE node is the human's call.
- **VP9:** this machine has no Media Foundation VP9 decoder, so VP9 wasn't advertised. From the cited pipeline, the
  server's VP9 is expected to be profile 1 (4:4:4) too; that wasn't observed.
- **HEVC:** only reachable through spice-streaming-agent, which the viewer doesn't use.
- **win81 and the `ubuntu` guest:** not run. Both use the same spice-server and pipeline, so the same encoder
  format is expected, but another guest's negotiated format wasn't measured. win81 has video streaming off, and
  turning it on would have needed a config change and a reboot.
- **Moving a window over the video (the clip):** not run live. The clip is the same code path as for MJPEG, and the
  synthetic tests cover it for hardware streams.

## Guest state afterwards

- Windows Media Player's first-run wizard blocked the first playback. `AcceptedPrivacyStatement` under the account's
  `Software\Microsoft\MediaPlayer\Preferences` was set to 1 for the test and set back to its original 0 afterwards.
  The wizard process was closed, not completed.
- The scheduled task used to start the player was deleted after each start, and the player was closed.
- No Proxmox setting was changed, and no VM was rebooted.
- Every viewer was closed, and the `.vv` files are gone: the viewer deletes them as their `delete-this-file` key asks.
