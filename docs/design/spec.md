# Native Windows SPICE Client: Tech Stack and Rationale

Oct 28, 2026 · @Inndy

## How to use this document

You are a coding agent implementing this client. This document is the source of truth for scope and technical decisions.

- **Binding words.** MUST and MUST NOT are requirements. SHOULD is the default unless you record a reason.
- **Don't swap decisions.** Never replace a choice with an option from Rejected alternatives. If a decision blocks you, stop and ask the human.
- **When this document is silent,** pick the simplest option that keeps the energy rules, and record it in `docs/decisions/` (context, decision, reason).
- **Never guess protocol details.** Read the reference sources at the end. Wrong struct layouts are the most likely bug.
- **Work in milestone order.** Each milestone has a definition of done; don't start the next until it passes.
- **Open questions belong to the human.** Ask before deciding anything listed under Open questions.

## Requirements

v0 is a `.vv` file viewer: the user double-clicks a Proxmox `.vv` file and lands in the VM's console, fast and smooth. Performance, energy efficiency and UX come before feature count.

| Requirement | What it means for the implementation |
| --- | --- |
| Opens Proxmox `.vv` files | Handles spiceproxy, short-lived tickets, CA pinning and the `.vv` hotkey keys |
| Fast to connect | Double-click to first guest frame with no visible stalls; targets in v0 viewer behavior |
| Native Windows behavior | Dark mode, snap layouts, FancyZones, per-monitor DPI; no GTK |
| Energy efficiency | Near-zero CPU and GPU use while the guest screen is static |
| System shortcut capture | Alt+Tab, the Win key and similar combos reach the guest, as in RealVNC and RustDesk |
| Works without a GPU | Usable in VMs and RDP jump hosts with only software rendering |
| Agent-maintainable | Memory-safe language, and replay tests against real traffic that catch mistakes |
| Few dependencies | One C# solution plus a few small native DLLs |

## Scope

Build the v0 viewer first. The Proxmox API launcher (v1) is optional and MUST NOT change the viewer's code paths.

| Version | Delivers | Notes |
| --- | --- | --- |
| v0 | `.vv` viewer: display, keyboard and mouse, system shortcuts, cursor, guest agent (clipboard, resize-follows-window), audio playback, MJPEG video | The product; optimize for speed, efficiency and UX |
| Post-v0 | Hardware video (H.264, VP9, HEVC), multiple monitors | Same viewer |
| v1 (optional) | WinUI 3 launcher: VM list from the PVE API, saved connections | Starts the viewer as a separate process; never embeds it |

- **Targets:** Proxmox VE VMs with the SPICE (QXL) display. Plain QEMU and libvirt `.vv` files work too.
- **Platforms:** Windows 11 on x64 and ARM64. Windows 10 22H2 is best effort, without Windows 11-only visuals.
- **Out of scope:** USB redirection, smartcards, microphone input, file transfer, folder sharing. Ignore the related `.vv` keys.
- **macOS:** not part of this project. [SpiceSee](https://github.com/aaronpollock/SpiceSee) covers it, and its architecture is the reference for this one.

## v0 viewer behavior

Double-clicking a `.vv` file MUST show a window at once and reach the first guest frame with no further user action.

**Launch flow**

1. Windows starts `SpiceViewer.exe <path>` through the `.vv` file association.
2. Read the file into memory, parse it, and delete it if `delete-this-file=1`. Never log the password.
3. Show the window immediately: dark background, status line "Connecting to \<title>…" painted with GDI. The swap chain isn't needed yet.
4. Start connecting at once, before any other UI work. The ticket expires quickly.
5. When the display channel creates the primary surface, size the window to the guest resolution (clamped to the monitor's work area) and present the first frame.
6. If `fullscreen=1`, enter full screen on the monitor the window opened on.

**Performance targets** (initial; adjust from measurements)

| Metric | Target |
| --- | --- |
| Process start to visible window | ≤ 300 ms |
| `.vv` opened to first guest frame, LAN | ≤ 1 s |
| Added input-to-screen latency | ≤ 1 display refresh |
| Static guest desktop | Under 0.5% CPU, no GPU activity |

**`.vv` keys** (section `[virt-viewer]`)

| Key | Handling |
| --- | --- |
| `type` | MUST be `spice`; otherwise show an error |
| `host`, `port`, `tls-port` | Target. With a proxy, `host` can be an opaque token; send it verbatim |
| `password` | Short-lived ticket |
| `proxy` | `http://host:3128`; tunnel every channel with HTTP `CONNECT` |
| `ca` | PEM with literal `\n` escapes; the only trusted root |
| `host-subject` | Required certificate subject, compared as a distinguished name |
| `title` | Window title |
| `delete-this-file` | `1` = delete after reading |
| `fullscreen` | `1` = start in full screen |
| `toggle-fullscreen`, `release-cursor`, `secure-attention` | Hotkeys in virt-viewer syntax, e.g. `Shift+F11`, `Ctrl+Alt+R`, `Ctrl+Alt+Ins` |
| Anything else | Ignore; log at debug level |

**Window, menus and errors**

- One process per `.vv` file, so sessions are isolated and startup stays simple.
- Commands live in the window's system menu (title-bar right-click, Alt+Space): Send Ctrl+Alt+Del, Send Win+L, Full screen, Release keyboard, Scaling (fit or 1:1), System shortcuts (always, full screen only, never), About.
- In full screen, a thin strip at the top edge (a separate small window) shows the title, the menu and an exit button on hover.
- Errors use `TaskDialogIndirect` with one actionable sentence. Rejected ticket: "This console link has expired. In Proxmox, choose Console → SPICE again." Certificate mismatch: refuse, with no override.
- On disconnect, dim the last frame and show "Disconnected" with a Close button. Don't auto-reconnect; the ticket is gone.

## Stack at a glance

One C# solution compiled with Native AOT, native Windows APIs throughout, and a few small native DLLs for codecs.

| Layer | Choice | Key reason |
| --- | --- | --- |
| Language and runtime | C# on .NET 10 (LTS), Native AOT | Memory-safe parsing, fast cold start, no JIT warm-up |
| Viewer UI | Win32 window, system menu, `TaskDialogIndirect`; DWM attributes for dark title bar and Mica | Starts fast; nothing idles in the background |
| Launcher UI (v1, optional) | WinUI 3, separate executable | Native look for the VM list; keeps the viewer lean |
| Win32 and COM bindings | CsWin32, marshaling disabled | One Microsoft-maintained generator for Win32, D3D11, DXGI and Media Foundation; works with Native AOT |
| Presentation | Direct3D 11, DXGI flip model (`FLIP_SEQUENTIAL`), `Present1` with dirty and scroll rectangles, waitable swap chain | Bypasses desktop composition, partial recomposition, low latency, works on WARP |
| Compositor | CPU, C# | SPICE draw commands are 2D: mostly copies, fills and scrolls |
| SPICE image codecs (QUIC, LZ, GLZ, LZ4) | `spicecodec.dll`, built from C vendored from spice-common and spice-gtk, and lz4's `lib/lz4.c` | No .NET implementation of QUIC, LZ or GLZ exists; the server defaults to GLZ. LZ4 joins them: one native decoder DLL, no managed package |
| JPEG and MJPEG | libjpeg-turbo (`turbojpeg.dll`) | Fast, and identical on every Windows edition |
| zlib | .NET `ZLibStream` | Managed; no extra native code |
| Audio | libopus (`opus.dll`), WASAPI shared event-driven mode | Native output that can stop between playback |
| TLS | .NET `SslStream` with custom trust | CA pinning and subject check from the `.vv` |
| Keyboard and mouse | `WH_KEYBOARD_LL`, Raw Input, `ClipCursor` | System shortcuts reach the guest; relative mouse without an agent |
| Tests | xUnit, recording proxy, golden-image replay, SharpFuzz | Catches protocol mistakes before they reach a VM |

## Solution layout

Use this layout. Dependencies point downward only, and everything below `SpiceViewer` runs headless.

```text
SpiceViewer.sln
├─ src/
│  ├─ Spice.Wire/          message types, bounds-checked reader and writer (no I/O)
│  ├─ Spice.Transport/     TCP, HTTP CONNECT, TLS, link handshake, channels
│  ├─ Spice.Canvas/        surfaces, draw commands, image cache, codec routing
│  ├─ Spice.Media/         MJPEG and Opus decoding, media clock
│  ├─ Spice.Session/       the one API the app uses
│  ├─ Spice.Native/        LibraryImport bindings for the native DLLs
│  ├─ VvFile/              .vv parser
│  └─ SpiceViewer/         Win32 app: window, presenter, input, audio output
├─ native/                 CMake: spicecodec.dll, turbojpeg.dll, opus.dll
├─ tools/
│  ├─ spicerec/            recording proxy, one file per channel
│  └─ spice-cli/           connect, dump a frame to PNG
├─ tests/
│  ├─ Spice.Wire.Tests/
│  ├─ Spice.Replay.Tests/  recordings → golden PNGs
│  └─ fixtures/            recordings, golden images, sample .vv files with secrets removed
└─ `docs/decisions/`
```

- `Spice.Wire` MUST NOT perform I/O or depend on any other project.
- Only `SpiceViewer` may use Win32 windowing, Direct3D, Media Foundation or WASAPI. Hardware video decoding (post-v0) lives there because it uses Direct3D 11 on the presenter's adapter.
- `Spice.Session` exposes events (surface dirty rectangles, cursor changes, audio frames, clipboard) and methods for input; the app never touches channels directly.
- `VvFile` has no dependencies, so the v1 launcher can reuse it.

## Language and UI framework

C# on .NET 10 for the whole app, plain Win32 for the viewer, and WinUI 3 only in the optional v1 launcher.

**Why C#**

- **Safe parsing of untrusted data.** The server could be hostile. Parse with bounds-checked `Span<byte>` readers; a malformed message MUST raise an error, never corrupt memory.
- **Windows integration.** CsWin32 generates bindings for Win32, D3D11, DXGI and Media Foundation from Microsoft's metadata.
- **Agent fluency.** Models write C# and these APIs well.
- **SpiceSee as a reference.** Its Swift layers map one-to-one onto the projects in Solution layout.

**Native AOT rules**

- Publish with `PublishAot=true` for win-x64 and win-arm64.
- No reflection-based serialization. Where JSON appears (v1 only), use source-generated `System.Text.Json`.
- Configure CsWin32 with `"allowMarshaling": false` in `NativeMethods.json`, so COM calls compile to raw function pointers.
- Native DLL calls use `[LibraryImport]`, not `[DllImport]`.
- No allocations on per-frame or per-message paths: rent from `ArrayPool<byte>` or keep long-lived buffers.

**Why no UI framework in the viewer**

- Cold start is the first thing a `.vv` user feels, and a plain Win32 window adds almost nothing to it.
- A flip-model swap chain on a plain window is the most direct route to Windows skipping desktop composition. XAML's `SwapChainPanel` adds a composition layer.
- `DwmSetWindowAttribute` gives the window a dark title bar, Mica and rounded corners.
- Declare per-monitor DPI awareness v2 in the app manifest.

**v1 launcher (optional)**

A separate WinUI 3 executable lists VMs through the PVE API, then starts `SpiceViewer.exe --vv-stdin` and writes the connection data as `.vv` text to its standard input. Tickets never touch disk or the command line.

## Rendering and presentation

The normal presentation path MUST NOT use shaders: copy dirty rectangles from the CPU framebuffer into a flip-model swap chain and present them with `Present1`.

No 3D pipeline is needed. Guest 3D (Proxmox's VirGL) renders on the host and arrives as ordinary images.

**Presenter**

1. Create the D3D11 device with `D3D_DRIVER_TYPE_HARDWARE`; fall back to `D3D_DRIVER_TYPE_WARP`.
2. Create the swap chain with `CreateSwapChainForHwnd`: `FLIP_SEQUENTIAL`, 2 buffers, `B8G8R8A8_UNORM`, `DXGI_SCALING_STRETCH`, `DXGI_ALPHA_MODE_IGNORE`, and the frame-latency waitable object flag. Call `SetMaximumFrameLatency(1)`.
3. Size the swap chain to the guest's primary surface. Call `ResizeBuffers` only when the guest resolution changes.
4. Per update: wait on the waitable object; copy this update's and the previous update's dirty rectangles into the back buffer with `UpdateSubresource`; call `Present1(1, 0, …)` with this update's rectangles. Pass SPICE copy-bits operations as the DXGI scroll rectangle.
5. Present only when something changed, at most once per refresh. Merge rectangles that arrive between presents.
6. While the window is minimized or `Present1` returns `DXGI_STATUS_OCCLUDED`, skip presents but keep applying draw commands.

Why: flip model lets Windows send frames straight to the screen with [lower power use](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model). [Dirty and scroll rectangles](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-1-2-presentation-improvements) let Windows recompose only changed pixels and cut RDP traffic. `B8G8R8A8` matches SPICE's 32-bit pixels, so there's no conversion.

**Scaling**

- With the guest agent: resize the guest to the window, so output stays 1:1.
- Without an agent: stretch scaling, with the window's aspect ratio locked to the guest's during resize (`WM_SIZING`).
- When the aspect ratio can't be locked (maximized, snapped): one letterbox path with a single full-screen-triangle shader. It's the only shader in v0.
- Log `IDXGIOutput6::CheckHardwareCompositionSupport` results, to see whether the display scales in hardware.

**Software rendering**

At startup, check the adapter for `DXGI_ADAPTER_FLAG_SOFTWARE` (or vendor ID 0x1414). If set, use the software profile: 1:1 only, MJPEG only, at most 30 presents per second.

## Protocol, codecs and compositor

Write the protocol and compositor in C#. Take SPICE's image codecs from upstream C, compiled into `spicecodec.dll`.

**SPICE QUIC is not the QUIC network protocol.** SPICE's QUIC is a lossless image codec. `System.Net.Quic` and MsQuic implement the HTTP/3 transport and MUST NOT be used for it.

**Codec DLL**

- No .NET implementation of SPICE's QUIC, LZ or GLZ exists. Vendor `quic.c` and `lz.c` from spice-common and the GLZ decoder from spice-gtk, as SpiceSee does.
- The upstream files use a few spice-common and GLib macros. Provide a small shim header; MUST NOT link GLib. SpiceSee's `CSpiceCodec` package is the reference.
- Export a flat C ABI: caller-allocated output buffers with explicit stride, an opaque handle for the stateful GLZ window, integer error codes, no callbacks into managed code.
- Keep one GLZ window per session; GLZ images reference earlier images, possibly from other display channels.
- Build with CMake and clang-cl for win-x64 and win-arm64, with `/O2`, Control Flow Guard and ASLR.
- Record each vendored file's upstream path and commit in `native/VENDORED.md`.
- MUST NOT rewrite these decoders. A C# port is allowed later only as a separate LGPL assembly, verified by differential fuzzing against the C DLL.

**Compositor**

- Surfaces are `B8G8R8A8` buffers in memory. `Spice.Canvas` applies draw commands with clipping and the image cache.
- Implement copies, fills, copy-bits and blends with `Span<T>` and `System.Runtime.Intrinsics` vectors. Handle rarer raster operations correctly, without special optimization.
- Every draw command reports its dirty rectangles to the session, which forwards them to the presenter.
- spice-common's software canvas (`canvas_base.c`, `sw_canvas.c`) defines correct behavior. MUST NOT add a pixman dependency.

**Ground truth**

The official SPICE spec is outdated. Use the `spice-protocol` headers for wire layouts and spice-gtk's channel code for behavior. Follow SpiceSee's Swift sources for structure.

## Video and audio

v0 decodes MJPEG streams and Opus audio; hardware H.264, VP9 and HEVC come after v0.

Proxmox sends video streams only when video streaming is enabled in the VM's SPICE settings, so many sessions never use this path.

- **MJPEG (v0):** decode with libjpeg-turbo into the framebuffer at the stream's destination rectangle, clipped by its clip region, then report the rectangle as dirty.
- **H.264, VP9 and HEVC (post-v0; the human, 2026-10-31):** Media Foundation hardware decoder on the presenter's D3D11 device; `ID3D11VideoProcessor` converts and scales. The first version copies frames into the framebuffer, so later draw commands that read those pixels stay correct. It currently decodes on its own device on the same adapter, pending the human ([decisions/0037](../decisions/0037-hardware-video-decoding-h264-vp9-hevc-through-media-foundation.md)). VP8 is not decoded.
- **Codec advertising:** advertise H.264, VP9 and HEVC each only when the adapter is hardware, has a DXVA decoder profile for that codec, and Media Foundation has a Direct3D 11 decoder for it (VP9 and HEVC usually need Microsoft's Store video extensions). VP8 is never advertised. Windows "N" editions and Windows Server without Media Foundation get MJPEG only.
- **Pacing:** display frames against the guest's multimedia clock, as SpiceSee does.
- **Audio (v0):** decode Opus with libopus (also accept raw PCM) and play through WASAPI in shared, event-driven mode. Open the stream on playback start and close it on playback stop; an open stream wakes the CPU every few milliseconds.
- **Not in v0:** microphone input, DirectComposition video overlays.

## Input and system shortcuts

Use a low-level keyboard hook so Alt+Tab, the Win key and similar combos reach the guest, as RealVNC, RustDesk and SDL do.

**Keyboard**

- Install `WH_KEYBOARD_LL` when the viewer window gains focus with capture on. Uninstall it on focus loss or release.
- Run the hook on a dedicated thread with its own message loop. The callback MUST only enqueue and return; Windows silently removes slow hooks.
- In the callback, check the `.vv` hotkeys first: `release-cursor` (default Ctrl+Alt+R), `toggle-fullscreen` (default Shift+F11), `secure-attention` (default Ctrl+Alt+Ins, sends Ctrl+Alt+Del).
- Send scancodes, never characters: `KBDLLHOOKSTRUCT.scanCode`, with the 0xE0 prefix when `LLKHF_EXTENDED` is set. These are the codes SPICE's inputs channel expects.
- Drop the synthetic Left Ctrl that Windows sends with AltGr.
- On focus loss, send a key-up for every key still down.
- Ctrl+Alt+Del and Win+L can't be hooked; the system menu sends them.
- The "System shortcuts" setting chooses always (default), full screen only, or never.

**Mouse and cursor**

- Agent connected: client mouse mode, absolute positions in guest coordinates.
- No agent: server mouse mode. Confine and hide the pointer with `ClipCursor`, and send relative motion from Raw Input (`WM_INPUT`). The release hotkey frees it.
- Build an `HCURSOR` from each cursor-channel image with `CreateIconIndirect`, cached by cursor ID. Mouse movement never triggers a present.
- Merge mouse motion to one message per input batch, and respect the inputs channel's motion acknowledgements.

## Connection pipeline

v0 connects from `.vv` data alone. The PVE API belongs to the optional v1 launcher, and the viewer never calls it.

1. With `proxy` set, open TCP to the proxy and send `CONNECT <host>:<tls-port> HTTP/1.1`, with `host` exactly as given. Require a 200 response.
2. Wrap the stream in `SslStream`. Trust only the `.vv` CA (`X509ChainTrustMode.CustomRootTrust` with `CustomTrustStore`) and require the leaf certificate's subject to equal `host-subject`. MUST NOT fall back to system trust or hostname checks.
3. Run the main channel's link handshake, read the server's RSA public key, and send the ticket encrypted with RSA-OAEP (SHA-1).
4. Open the other channels the server lists (display, inputs, cursor, playback) the same way, each through its own tunnel and TLS session, with the connection ID from the main channel. Open them all right away, while the ticket is valid.
5. Without `proxy` and `tls-port`, connect directly to `host:port`. Plain-text connections are allowed only when the `.vv` has no `tls-port`.

**v1 launcher (optional)**

- Authenticate with a PVE API token (`Authorization: PVEAPIToken=USER@REALM!TOKENID=SECRET`, privilege `VM.Console`), or with a user name and password through `POST /api2/json/access/ticket`, including the TOTP second factor; keep the ticket and CSRF token in memory only and renew them before their 2-hour end. Store token secrets and, when the user asks, passwords in Windows Credential Manager, and let the user forget them ([decisions/0039](../decisions/0039-pve-sign-in-token-or-password-with-tfa-secrets-in-credential-manager.md), human 2026-10-31).
- List VMs with `GET /api2/json/cluster/resources?type=vm`.
- Fetch connection data with `POST /api2/json/nodes/{node}/qemu/{vmid}/spiceproxy`, then pass it to the viewer through `--vv-stdin`.

## Energy-efficiency rules

These are requirements: the viewer MUST do nothing while the guest screen is static.

- MUST present only on change; no render loop and no timer-driven presents.
- MUST use native cursors, so mouse movement never presents.
- MUST stop presenting while minimized or occluded, but keep decoding, because GLZ images reference earlier ones.
- MUST close the WASAPI stream between playback stop and start.
- MUST NOT poll: async sockets, `AddClipboardFormatListener` for clipboard changes, no timers while idle.
- MUST install the keyboard hook only while capturing.
- MUST NOT allocate per frame or per message on hot paths.
- SHOULD use hardware video decoding when available (post-v0).

**Acceptance test:** connected to a VM showing a static desktop for 60 seconds, the viewer averages under 0.5% CPU, causes no GPU activity, and Task Manager shows "Very low" power usage.

## Rejected alternatives

MUST NOT use any of these without the human's approval.

| Option | Why not |
| --- | --- |
| `System.Net.Quic` or MsQuic for SPICE QUIC | A network transport, unrelated to SPICE's image codec |
| Rewriting QUIC, LZ or GLZ in C# now | Intricate, already correct in C, and a likely source of agent mistakes |
| Wrapping libspice-client-glib (spice-gtk's engine) | Ships GLib and GStreamer DLLs and needs a bridged GLib main loop |
| Patching virt-viewer | GTK look and behavior; its Windows build hasn't moved since 2021 |
| WinUI 3, WPF or any UI framework inside the viewer | Slower cold start and a runtime dependency, for a window that needs only a menu and a dialog |
| Electron, Tauri or other webview UIs | A browser engine is never idle |
| Dear ImGui | Typically redraws every frame |
| SDL3 | An abstraction a Windows-only app doesn't need |
| Vortice.Windows | CsWin32 already covers D3D11, DXGI and Media Foundation, with Native AOT-friendly output |
| Direct3D 12 | One copy and one present per frame; more code, no energy gain |
| Direct2D | A drawing API; the compositor has already drawn everything |
| XAML `SwapChainPanel` | Adds a composition layer between the swap chain and the screen |
| GPU-side compositing | Complexity without visible gain; raster operations are awkward on the GPU |
| Rust, Zig, Go, C or C++ for the app | Weak WinUI support (Rust), stale model output (Zig), cgo everywhere (Go), no memory safety (C, C++) |

## Testing and verification

Every protocol feature lands with a replay test made from real traffic; that is how agent mistakes get caught.

- **Recording proxy (`tools/spicerec`).** Sits between remote-viewer and a real server and saves each channel's bytes to its own file. Capture every fixture with it.
- **Replay tests.** Feed recordings through `Spice.Canvas` headless and compare the framebuffer pixel-for-pixel with committed golden PNGs. Update a golden image only with the human's approval.
- **Wire tests.** Round-trip every message type. Fuzz `Spice.Wire` with SharpFuzz; malformed input MUST produce an error, never a crash or hang.
- **`.vv` tests.** Parse real Proxmox `.vv` samples with secrets replaced.
- **Present mode.** PresentMon SHOULD show "Independent Flip" in full screen, and at worst "Composed: Flip" when windowed.
- **Power.** Windows Performance Recorder and Analyzer show CPU wakeups and power while idle.
- **Matrix:** Windows guest (QXL driver), Linux guest, a client VM with only WARP, and the viewer inside an RDP session.

## Licensing and distribution

Release the app under MIT, with the LGPL codecs isolated in a DLL users can replace.

| Component | License | Handling |
| --- | --- | --- |
| This client | MIT | Matches SpiceSee and keeps porting simple |
| Code ported from SpiceSee | MIT | Keep SpiceSee's copyright notice |
| QUIC, LZ and GLZ decoders | LGPL-2.1-or-later | Separate DLL with source and notices, replaceable by users |
| libjpeg-turbo | BSD-style (IJG and BSD) | Notice in the About screen |
| libopus | BSD | Notice in the About screen |

Ship a code-signed installer through GitHub Releases and winget. Signing avoids SmartScreen warnings.

## Milestones

Do these in order. Each ends with a definition of done (DoD) that MUST pass before the next starts.

1. **Connect.** `.vv` parser, proxy tunnel, TLS with pinning, link handshake and ticket. `spice-cli connect file.vv` prints the channel list.
   - DoD: works against a fresh Proxmox `.vv`; a wrong CA or subject is refused.
2. **Pixels.** Display channel, `spicecodec.dll`, compositor. `spice-cli dump file.vv out.png` saves a frame.
   - DoD: replay tests pass for a Windows guest and a Linux guest recording, covering QUIC, LZ and GLZ.
3. **Window.** Viewer window, presenter, event-driven presents, native cursor, mouse and keyboard without the hook.
   - DoD: the idle acceptance test passes; PresentMon shows flip-model presents.
4. **Double-click UX.** File association, launch flow, system menu, error dialogs, disconnect state, Native AOT publish.
   - DoD: performance targets met on a LAN; an expired `.vv` shows the expired-ticket dialog.
5. **Agent.** Clipboard both ways (text and images), resize-follows-window, client mouse mode.
   - DoD: clipboard round-trips; resizing the window resizes the guest.
6. **System shortcuts.** Keyboard hook, `.vv` hotkeys, full-screen strip, server mouse mode.
   - DoD: Alt+Tab and the Win key reach the guest; the release hotkey always works; no stuck keys after focus loss.
7. **Audio and MJPEG.** Playback channel and MJPEG streams.
   - DoD: audio starts and stops cleanly; the idle test still passes after playback.
8. **v0 release.** Software-rendering profile, signed installer, WARP and RDP checks.
   - DoD: the full test matrix passes.

After v0: H.264, VP9 and HEVC through Media Foundation, multiple monitors, then the optional v1 launcher.

## Risks and open questions

The largest risk is wrong protocol details in agent-written code; replay tests are the mitigation.

| Risk | Mitigation |
| --- | --- |
| Invented struct layouts or message IDs | Read the `spice-protocol` headers; replay tests against real recordings |
| Ticket expires during a slow connect | Connect before any UI work; open all channels immediately |
| Keyboard hook silently removed | Dedicated hook thread; the callback only enqueues |
| `Present1` rectangle bookkeeping across buffers | Golden tests of presented output, plus a debug switch that presents full frames |
| CsWin32 lacks a COM API | Hand-write a minimal definition for that interface only, and note it in `docs/decisions/` |
| Windows 10 lacks Mica and rounded corners | Feature-detect and fall back to standard styling |

**Open questions** (ask the human; don't decide)

- How far should Windows 10 22H2 support go?
- Should window size and position be remembered per VM, and keyed by what?
- Installer format: MSIX or a classic installer?

## Reference sources

- [spice-protocol](https://gitlab.freedesktop.org/spice/spice-protocol): wire layouts and enums (`spice/protocol.h`, `spice/enums.h`)
- [spice-common](https://gitlab.freedesktop.org/spice/spice-common): `quic.c`, `lz.c`, software canvas behavior
- [spice-gtk](https://gitlab.freedesktop.org/spice/spice-gtk): GLZ decoder, channel behavior
- [virt-viewer](https://gitlab.com/virt-viewer/virt-viewer): `.vv` file keys and hotkey syntax
- [SpiceSee](https://github.com/aaronpollock/SpiceSee): architecture, codec vendoring, recording proxy, replay tests
- [For best performance, use DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model) (Microsoft)
- [DXGI 1.2 presentation improvements](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-1-2-presentation-improvements) (Microsoft)
- [IDXGISwapChain1::Present1](https://learn.microsoft.com/en-us/windows/desktop/api/DXGI1_2/nf-dxgi1_2-idxgiswapchain1-present1) (Microsoft)
