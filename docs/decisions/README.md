# Decision records

Choices made where the spec ([../design/spec.md](../design/spec.md)) is silent, and departures from it the human
approved. One file per decision, numbered in the order they were made. The spec says open questions belong to the
human: record the human's answer here too, saying so in "Decided by".

To add one: copy [template.md](template.md) to the next number, fill it in, and add a row below. Never renumber.
When a decision changes, write a new record and set the old one's status to `Superseded by NNNN`.

| # | Decision |
|---|---|
| [0001](0001-native-toolchain-msys2-clang64-instead-of-clang-cl.md) | Native toolchain: msys2 clang64 instead of clang-cl |
| [0002](0002-codecs-stay-in-c-for-v0.md) | Codecs stay in C for v0 |
| [0003](0003-tls-without-a-ca-is-refused.md) | TLS without a CA is refused |
| [0004](0004-lz4-is-not-advertised.md) | LZ4 is not advertised (superseded by 0028) |
| [0005](0005-clipboard-data-is-fetched-eagerly.md) | Clipboard data is fetched eagerly |
| [0006](0006-test-fixtures-are-recorded-by-our-own-client.md) | Test fixtures are recorded by our own client |
| [0007](0007-1-1-scaling-keeps-the-window-at-the-guest-size-where-it-can.md) | 1:1 scaling keeps the window at the guest size where it can |
| [0008](0008-scroll-rects-for-copy-bits.md) | Scroll rects for COPY_BITS |
| [0009](0009-allocation-scope-of-the-hot-paths.md) | Allocation scope of the hot paths |
| [0010](0010-mouse-mode-is-u16-on-the-wire.md) | `mouse_mode` is u16 on the wire |
| [0011](0011-server-mouse-mode-cursor-is-configurable.md) | Server mouse mode cursor is configurable |
| [0012](0012-channel-sends-are-queued-never-blocking.md) | Channel sends are queued, never blocking |
| [0013](0013-outbound-agent-queue-is-bounded.md) | Outbound agent queue is bounded |
| [0014](0014-native-aot-links-with-msvc-on-windows.md) | Native AOT links with MSVC on Windows |
| [0015](0015-windows-tfm-net10-0-windows10-0-17763-0.md) | Windows TFM `net10.0-windows10.0.17763.0` |
| [0016](0016-hand-declared-getwindowlongptrw-setwindowlongptrw.md) | Hand-declared `GetWindowLongPtrW`/`SetWindowLongPtrW` |
| [0017](0017-two-presentation-paths-direct-and-one-letterbox-shader.md) | Two presentation paths: direct and one letterbox shader |
| [0018](0018-presenter-owns-its-graphics-objects-device-loss-recreates-on.md) | Presenter owns its graphics objects; device loss recreates once |
| [0019](0019-viewport-beyond-the-texture-limit.md) | Viewport beyond the texture limit |
| [0020](0020-v0-ships-as-a-portable-zip.md) | v0 ships as a portable zip |
| [0021](0021-guest-resize-past-the-guest-s-video-memory.md) | Guest resize past the guest's video memory |
| [0022](0022-windows-10-22h2-best-effort-human-2026-10-29.md) | Windows 10 22H2: best effort (human, 2026-10-29) |
| [0023](0023-per-vm-window-state-per-vm-keyed-by-proxy-host-title.md) | Per-VM window state: per VM, keyed by proxy/host + title |
| [0024](0024-settings-portable-by-default-opt-in-install.md) | Settings: portable by default, opt-in install |
| [0025](0025-video-frames-paced-by-their-spacing-on-the-guest-clock.md) | Video pacing is a setting: by default frames keep their spacing on the guest clock (proposed) |
| [0026](0026-x64-builds-target-x86-64-v3.md) | x64 builds target x86-64-v3 (human, 2026-10-30) |
| [0027](0027-native-dlls-trimmed-with-gc-sections-and-full-lto-lz4-decoder-in-spicecodec.md) | Native DLLs trimmed with gc-sections and full LTO; LZ4 decoder in spicecodec.dll (human, 2026-10-30) |
| [0028](0028-fast-jpeg-and-preferred-image-compression-are-settings-lz4-is-advertised.md) | Fast JPEG decoding and the preferred image compression are settings; LZ4 is advertised (human, 2026-10-30) |
| [0029](0029-vendored-codec-patches-allowed-with-differential-verification.md) | Vendored codec patches are allowed when a differential test proves them (human, 2026-10-30) |
