# Synthetic H.264 clips

Inputs of the software H.264 tests (Spice.Native.Tests `H264DecoderTests`, Spice.Replay.Tests
`SoftwareVideoSessionTests`; docs/decisions/0037, amended 2026-10-31). No guest content: every frame is drawn by
`make.py`, the same picture as Spice.Video.Tests' `TestClip` (four flat quadrants and a light 32x32 block moving 8 pixels
right per frame).

| File | Profile | Format | Size | Frames |
|---|---|---|---|---|
| `clip-444.h264` | High 4:4:4 Predictive (244), what spice-server sends by default | yuv444p, 8-bit | 320x240 | 30 |
| `clip-420.h264` | Constrained Baseline (66) | yuv420p, 8-bit | 320x240 | 30 |
| `clip-444-10bit.h264` | High 4:4:4 Predictive (244) | yuv444p10le | 64x48 | 4 |

Made on 2026-10-31 with `python -I make.py <ffmpeg.exe>`, using the gyan.dev "full" FFmpeg 9.0.2 build for Windows
and its libx264 (core 165 r3223 0480cb0; x264's settings string is in each clip's SEI). The source RGB is converted to
BT.601 studio-range YUV by swscale (accurate rounding, full chroma interpolation), which the streams signal. x264 runs
as spice-server's x264enc does (`server/gstreamer-encoder.c`): Annex B with access unit delimiters, `ultrafast`,
`zerolatency`, sliced threads (4 slices), periodic intra refresh, at a fixed QP of 12. Each access unit starts with its
delimiter; the tests split the stream there. Remaking them with another x264 gives different bytes; the tests check
decoded colours, not bytes.
