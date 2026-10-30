# 0037. H.264, VP9 and HEVC streams decode in hardware through Media Foundation; a codec is advertised only when decodable

- Status: Accepted (the decoding device, the codec preference default and the sync-MFT-only choice are provisional)
- Date: 2026-10-31
- Decided by: the human (2026-10-31: H.264 + VP9 + HEVC). The details below are by agent, marked provisional where
  the human should confirm them.

- **Context:**
  - The spec planned hardware H.264 after v0, on the presenter's D3D11 device, with `ID3D11VideoProcessor` converting
    and scaling, frames copied into the framebuffer. The human widened it to SPICE's other GStreamer codecs: H.264,
    VP9 and HEVC, each only when a hardware decoder exists. VP8 is not advertised. MJPEG stays as it is.
  - Protocol ground truth, read from cloned sources (not built or run): spice-gtk
    `4086377fb5379a849a7444a6b32686ae14856255`, spice-server `adeb620a04c43620416c5067b8bbfd1e4cf5eef5`,
    spice-protocol `499cc8326a672e9e5747efc017319b19e1594b42`.
    - Caps and codec numbers (spice-protocol `spice/enums.h`, `spice/protocol.h`): display caps MULTI_CODEC 8,
      CODEC_MJPEG 9, CODEC_VP8 10, CODEC_H264 11, PREF_VIDEO_CODEC_TYPE 12, CODEC_VP9 13, CODEC_H265 14;
      `SpiceVideoCodecType` MJPEG 1, VP8 2, H264 3, VP9 4, H265 5. `SPICE_MSGC_DISPLAY_PREFERRED_VIDEO_CODEC_TYPE`
      is 105; its body is `u8 num_of_codecs; u8 codecs[]`.
    - spice-gtk `src/channel-display.c`:
      - 978-989: advertises MULTI_CODEC, CODEC_MJPEG, and each GStreamer codec it can decode.
      - 619-639, 670-742: sends PREFERRED_VIDEO_CODEC_TYPE only when the application asks (the FIXME at 681 notes it
        should consider hardware decoding); nothing is sent by default.
      - 1387-1400: one decoder per stream, made at STREAM_CREATE from the stream's codec. 1438-1442: when none can be
        made, the stream is destroyed locally and `report_invalid_stream` is called.
      - 1333-1352: `report_invalid_stream` sends a STREAM_REPORT with num_frames 0 and num_drops UINT_MAX, the other
        fields zero.
      - 1493-1502: `stream_display_frame` flips bottom-up streams whatever the codec. 1812-1815: a frame the decoder
        refuses ends the stream the same way.
    - spice-gtk `src/channel-display-gst.c`: 869-873 an empty frame is ignored; 883-891 only MJPEG frames are dropped
      for lateness (the inter-frame codecs need every frame); 908-930 each STREAM_DATA becomes one buffer pushed to
      the decoder. `src/channel-display-priv.h` 176-188: H.264 and H.265 are Annex B byte streams
      (`stream-format=byte-stream`), VP9 is plain `video/x-vp9`.
    - spice-server `server/gstreamer-encoder.c`:
      - 914-931 and 2024-2027: encoders exist for MJPEG, VP8, VP9 and H.264 only; there is no H.265 encoder.
      - 1124: x264enc runs with `byte-stream=true aud=true ... speed-preset=ultrafast intra-refresh=true`, so H.264 is
        Annex B with access unit delimiters and periodic intra refresh rather than IDR frames. 1111: vp9enc with
        `lag-in-frames=0 deadline=1`, so no frame reordering.
      - 1709-1759: one raw frame in, one compressed buffer out: each STREAM_DATA carries one access unit or frame.
      - 1506-1509: rows are not reordered, so a bottom-up source reaches the client bottom-up. 1655-1679: a size
        change reconfigures the encoder in place (the bitstream carries the new size).
    - spice-server `server/reds.cpp` 3527-3531: the default codec list is
      `spice:mjpeg;gstreamer:mjpeg;gstreamer:h264;gstreamer:vp8;gstreamer:vp9;`. MJPEG comes first, so without a
      client preference (or a server configured otherwise) H.264 and VP9 are never chosen. 3640-3653: H.265 isn't
      accepted for the display channel.
    - spice-server `server/video-stream.cpp` 800-835: the encoder is the first codec of the (sorted) list that the
      client advertises, MJPEG as the fallback. 593-623: unlisted codecs sort after the listed ones.
      `server/dcc.cpp` 928-961: the list is sorted by the client's preference (stable); 975-988: a new preference
      stops the running streams so new ones use it; 870-877: a report with num_frames 0 and num_drops UINT32_MAX drops
      the stream's encoder, and `dcc-send.cpp` 1731 then sends the area as ordinary draws.
    - spice-server `server/stream-channel.cpp` 298-302: H.265 reaches a client only on the streaming-agent channel
      (spice-streaming-agent in the guest), which this viewer doesn't open. Advertising CODEC_H265 on the display
      channel is harmless and ready for a server that adds it.
    - spice-server `server/gstreamer-encoder.c` 1043-1070 and 1142-1144: the pipeline is `appsrc ! videoconvert !
      <encoder>`, and videoconvert picks the format closest to the BGRx input, so x264enc and vp9enc get Y444 unless
      the host sets `SPICE_CONVERTER_PREFERRED_FORMAT` (for example `I420`) in QEMU's environment. The result is H.264
      High 4:4:4 Predictive (profile 244) and, by the same pipeline, presumably VP9 profile 1 (not observed). Neither
      is covered by the DXVA profiles this viewer probes (`H264_VLD_NOFGT`, `VP9_VLD_PROFILE0`). The live check
      ([2026-10-31-hardware-video](../verification/2026-10-31-hardware-video.md)) found exactly that: the test host's
      spice-server sends H.264 when asked, as profile 244, and the Microsoft decoder fails its first output with
      E_FAIL.
- **Decision:**
  - **Interface (Spice.Session, codec-neutral):** `IVideoDecoderFactory` (`Codecs`: a task with the decodable codecs;
    `Create(codec, width, height)`) and `IVideoDecoder` (`NeedsEveryFrame`; `Decode(data, output)` returning
    Picture, NoPicture, Dropped or Failed). `VideoOutput.Begin(width, height, mirrored)` hands the decoder the
    stream's frame buffer, under the session's frame budget. The app sets `SpiceSession.VideoDecoders`. MJPEG moved
    behind the same interface (`MjpegVideoDecoder`, libjpeg-turbo, unchanged behaviour). Only SpiceViewer touches
    Media Foundation and Direct3D.
  - **Advertising:** the display channel link waits for the factory's probe (at most 5 s, started at process start,
    121 ms here) and advertises CODEC_H264, CODEC_VP9, CODEC_H265 for each codec it reports. MULTI_CODEC and
    CODEC_MJPEG are always advertised; CODEC_VP8 never is. A codec is reported only when all hold:
    - a hardware D3D11 device with video support can be made on the default adapter, and the adapter isn't a software
      one (WARP, the Basic Render Driver, `SPICEVIEWER_FORCE_WARP=1`);
    - the adapter lists the codec's decoder profile (`H264_VLD_NOFGT`, `VP9_VLD_PROFILE0`, `HEVC_VLD_MAIN`) and
      decodes it to NV12;
    - `MFTEnumEx` finds a synchronous, local decoder MFT for it that reports `MF_SA_D3D11_AWARE`.
    Without `mfplat.dll` (Windows N without the Media Feature Pack, Server without Media Foundation) or when the
    probe fails, only MJPEG is advertised. Each probe step logs one `video:` line, including a hint when the adapter
    could decode VP9 or HEVC but the Store's VP9 or HEVC Video Extensions aren't installed.
  - **Synchronous MFTs only (provisional):** the inbox Microsoft decoders (H.264 inbox; VP9 and HEVC from the Store
    extensions) are synchronous and use DXVA when given a D3D11 device manager. Vendor asynchronous hardware MFTs
    aren't used; they'd need the async event model for no gain these sources show.
  - **Decoder (`MfVideoDecoder`, one per stream):** `MF_LOW_LATENCY`, `MFT_MESSAGE_SET_D3D_MANAGER`, input type with
    the stream's size as a hint, NV12 output (P010 accepted), output samples provided by the MFT (required). Each
    STREAM_DATA is one input sample (the sample and buffer are reused); the decoder is drained after each input, and a
    format change re-selects the output type. The picture (a texture array slice) goes through an
    `ID3D11VideoProcessor` straight to a BGRA texture at the stream's destination size (the VP scales, including
    STREAM_DATA_SIZED), then through one reused staging texture into the stream's frame buffer, which the session
    blits at the destination clipped by the current clip region and reports dirty, as MJPEG frames are. Colour: the
    type's YUV matrix and nominal range, else BT.709 from 720 lines up and BT.601 below, studio range. Input views
    are cached per (texture, slice), at most 32. The staging `Map` waits for that one frame's GPU work; there's no
    timer or polling.
  - **Output first:** the decoder takes the session's frame buffer (`VideoOutput.Begin`) before it creates or uses
    any conversion target. A size the session refuses (beyond 16384, the canvas's and D3D11's limit, or over the
    frame budget) drops that picture: the decoder has advanced, nothing is converted, and the processor and targets
    of an earlier size are released, so a refused stream holds no GPU conversion memory (review N1).
  - **Bottom-up streams:** the decoder writes rows already flipped when the stream isn't TOP_DOWN and says so
    (`mirrored`), so the blit doesn't flip again. This matches spice-gtk's flip for every codec.
  - **Frames not shown still decode:** H.264, VP9 and HEVC frames depend on earlier ones, so a frame dropped by pacing
    (late, superseded, queue overflow, a new timeline) or hidden by the clip is still passed to the decoder, in
    order, with no output (`Decode(data, null)`). It is counted as dropped or hidden as before; MJPEG is unchanged.
  - **Failure:** no decoder for a stream, or a decoder that fails (driver error, garbage the MFT rejects, a lost
    device), marks the stream unsupported and sends spice-gtk's invalid-stream report (when the server takes stream
    reports): the server drops the encoder and sends the area as images. Later frames for that stream are ignored.
    A frame the decoder merely can't make a picture of is counted as a drop. **Behaviour change:** a stream in a
    codec that wasn't advertised used to count every frame as a drop; it is now reported unsupported at creation.
    `DisplayStreamTests.UnsupportedCodecDoesNotDecodeEvenAValidJpeg` (a Codex test of the old behaviour) was updated
    to the new one.
  - **Declared format first:** before a new H.264 or VP9 stream's frames reach its decoder, the session reads the
    format the first frames declare and asks the factory (`IVideoDecoderFactory.CanDecode`). `VideoBitstream` reads
    the H.264 SPS prefix up to both bit depths, bounded to the SPS NAL unit, and rejects what H.264 7.3.1, 7.3.2.1.1 and
    7.4.2.1.1 rule out in the fields it reads (forbidden_zero_bit, reserved_zero_2bits, seq_parameter_set_id over 31,
    chroma_format_idc over 3, either depth over 14, a byte over 3 after an emulation prevention byte, a field cut
    short); it is not a full SPS validator. For VP9 it reads the profile bits only; the chroma and depth it reports are
    placeholders from the profile number. At most 8 frames are searched, and nothing is allocated. The
    hardware factory takes H.264 Baseline, Main and High at 8-bit 4:2:0 and VP9 profile 0, what its probed DXVA
    profiles cover. A stream in any other format is failed at once, before decoding, with one log line naming the
    format.
  - **A codec that can't be decoded is avoided for the session:** when a stream's format is refused, or its decoder
    fails before it has decoded any picture (shown or not: `IVideoDecoder.HasDecoded`, so a lost device after hidden
    pictures doesn't count), the session stops using that codec until the next connection. A decoder made outside the
    stream lock is checked again under it and discarded if its codec was avoided meanwhile; avoidance and preference
    sends are serialized, so the last preference sent never names an avoided codec. New streams of it
    are reported unsupported at creation, and the preference is sent again without it, with MJPEG named so that it
    ranks ahead of the avoided codec (which stays advertised: capabilities can't change during a session). With the
    test host's 4:4:4 H.264 this turns into MJPEG within one stream: the first stream's area comes as images for a
    moment, then the server opens an MJPEG stream.
  - **Decoding device (provisional, departs from the spec's "on the presenter's D3D11 device"):** decoding uses its
    own D3D11 device (`VIDEO_SUPPORT | BGRA_SUPPORT`, multithread protected) on the default adapter, the same one the
    presenter uses. It is made when a hardware stream starts, shared by the streams decoding at the same time, and
    released when the last decoder closes, of whichever device generation (after a loss, decoders of the old device can
    outlive the new one's), so an idle session holds none. A device costs tens of MB of driver memory (the
    2026-10-30 WARP-against-hardware comparison suggests about 45 MB; not isolated for this device), and making one
    takes a few milliseconds. Sharing
    the presenter's device would need dropping `D3D11_CREATE_DEVICE_SINGLETHREADED` and adding multithread
    protection to every present, and risks a deadlock: the display thread holds the stream lock while decoding and
    would wait for the device lock, which the presenter holds in `Present1`, which per
    [0018](0018-presenter-owns-its-graphics-objects-device-loss-recreates-on.md) can wait on the UI thread, which can wait on the stream lock
    (changing video pacing). Frames are copied back to the framebuffer anyway, so a shared device saves no copy in
    this first version. Direct presentation of decoded textures (zero-copy) would revisit this.
  - **Device loss** (0018's rule): a decoder that sees its device removed fails its stream (the server falls back to
    images) and drops the device; the next stream gets a new one, once. A recreated device lost again before it has
    decoded a picture turns hardware decoding off for the session. Any decoded picture counts, shown or not (a
    hidden, late or superseded frame's picture too): the clip doesn't decide whether the device works (review N3).
  - **COM apartment:** each operation that needs COM (the probe, decoder enumeration) initializes the
    multithreaded apartment on its calling thread and undoes it on the same thread when done, S_FALSE included;
    RPC_E_CHANGED_MODE took no count and is not undone. Device and device-manager creation need no COM. The decoders
    are free-threaded MF objects used under the session's stream lock from whichever thread receives the display
    channel; they rely on the process's MTA, which the runtime's threads and MF's work queues keep alive (review
    N2).
  - **Raw COM calls:** CsWin32's friendly wrappers throw on a failing HRESULT. `IMFTransform.ProcessInput`,
    `ProcessOutput`, `ProcessMessage`, `GetOutputAvailableType` and `IMFAttributes.GetUINT32`, `GetUINT64`, `GetGUID`,
    `GetBlob` fail as ordinary control flow (`MF_E_TRANSFORM_NEED_MORE_INPUT`, `MF_E_NOTACCEPTING`, a missing
    attribute), so `src/SpiceViewer/Video/MfInterop.cs` calls them through their vtable slots, as
    [0016](0016-hand-declared-getwindowlongptrw-setwindowlongptrw.md) hand-declares what CsWin32 can't give. No
    interface had to be hand-declared.
  - **Codec preference (`videoCodec`, global string in settings.json, provisional):**
    - Values: `auto` (default), `server`, `mjpeg`, `h264`, `vp9`, `h265` (`hevc` also reads as `h265`). Anything else
      reads as `auto`, and the rest of the file is kept.
    - Each value is an order sent as PREFERRED_VIDEO_CODEC_TYPE right after DISPLAY_INIT and again on each change:
      `auto` and `h264` ask for H.264, VP9, HEVC; `vp9` for VP9, H.264, HEVC; `h265` for HEVC, H.264, VP9; `mjpeg`
      for MJPEG; `server` sends nothing. Only codecs the session advertises are sent (MJPEG always counts); when none
      is left nothing is sent, so on a machine without hardware decoding `auto` changes nothing. The message needs
      the server's PREF_VIDEO_CODEC_TYPE cap. Going back to `server` takes effect on the next connection (there's no
      "server order" value), and an Info line says so.
    - The default asks for hardware codecs because the server's own list puts MJPEG first, which would leave the
      decoders unused. The cost is on the host: x264enc or vp9enc in software uses more server CPU than MJPEG, for
      much less bandwidth.
    - The window menu has a "Video codec" submenu after "Image compression": "Automatic (hardware decoding first)",
      "Server default", "MJPEG", "H.264", "VP9", "HEVC". Codecs without a hardware decoder here are grayed. About
      lists the decoders.
  - **Measured here** (AMD Radeon 8060S, Windows 11 IoT LTSC 2024, no Store codec extensions): probe 121 ms; H.264
    decodes in hardware with "Microsoft H264 Video Decoder MFT"; the adapter has VP9 and HEVC profiles but Media
    Foundation has no decoder for them, so they aren't advertised. A synthetic 320x240 H.264 clip (encoded at test
    time by the inbox MF encoder) decodes within 2 per channel of the source; 60 shown and 60 hidden frames
    allocate 0 bytes once warm. The live check against the test host is in
    [2026-10-31-hardware-video](../verification/2026-10-31-hardware-video.md): its H.264 is 4:4:4, so no picture
    came from the hardware path there, and the fallback to MJPEG was verified instead.
- **Reason:** the human chose H.264, VP9 and HEVC. Media Foundation is inbox, needs no new third-party component and
  keeps codecs in the OS (no new license row). Copying into the framebuffer keeps later draws that read those pixels
  correct, as the spec requires, and reuses MJPEG's clip, pacing and report paths unchanged. A separate device costs
  one more device while streams decode and keeps the presenter single-threaded; reporting failures the way spice-gtk
  does keeps the screen right when a decoder can't, and avoiding the codec afterwards keeps a server whose encoder
  output no hardware decoder takes (the stock spice-server) on MJPEG instead of images.
