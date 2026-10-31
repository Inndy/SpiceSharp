# Review records

Codex reviews the code and writes tests; Claude (and Claude subagents) implement. Each review file lists findings
with severity, the implementer appends the fixes, and the next round verifies them.

| File | Scope |
|---|---|
| [2026-10-31-vnc-bridge.md](2026-10-31-vnc-bridge.md) | Five-commit VNC bridge security review: peer identity, handshake deadlines and bounds, framing/injection, hand-off, viewer commands, launcher routing, and offline regressions |
| [2026-10-31-h264-software.md](2026-10-31-h264-software.md) | Software H.264 native safety, SPS routing and hardware/software handoff, energy/allocation, FFmpeg licensing/packaging, and window-free regressions |
| [2026-10-31-windows-terminal.md](2026-10-31-windows-terminal.md) | Decision 0042 Windows Terminal detection, PATH alias identity, reparse validation, settings/Auto hand-offs, Python CLI/command construction, and offline regressions |
| [2026-10-31-monitor-inference.md](2026-10-31-monitor-inference.md) | L16 Linux monitor inference: draw/clip containment, video withdrawal, candidate lifetime, kept fallback identity, agent gate, settings/threading, and window-free regressions |
| [2026-10-31-launcher-smoke-fixes.md](2026-10-31-launcher-smoke-fixes.md) | Launcher B1-B6 fixes: sign-in cleanup, per-sign-in version cache, exception redaction/recovery, AOT patterns, energy, and loopback regressions |
| [2026-10-31-l13-l15.md](2026-10-31-l13-l15.md) | L13-L15 status/detail layout, shared append-only log, truncation, I/O errors, handle lifetime, and window-free regressions |
| [2026-10-31-idle-memory.md](2026-10-31-idle-memory.md) | K1 idle-memory trimming, decision 0041, native ownership and optional export, bounded pixel pool, trim concurrency, and window-free regressions |
| [2026-10-31-launcher.md](2026-10-31-launcher.md) | v1 PVE launcher auth/TFA, TLS, Credential Manager, hand-offs, packaging/licenses, energy/AOT, and window-free regressions |
| [2026-10-31-hardware-video.md](2026-10-31-hardware-video.md) | Hardware video protocol, MF/D3D11 ownership and recovery, resource budgets, settings, and window-free regressions |
| [2026-10-30-pveterm.md](2026-10-30-pveterm.md) | PveTerm protocol, console input/lifecycle, descriptor hand-off, TLS policy, allocation, and window-free regressions |
| [2026-10-30-display-off-and-monitor-area.md](2026-10-30-display-off-and-monitor-area.md) | Guest display-off state, monitor-area geometry, input identity/edges, and window-free regressions |
| [2026-10-30-audio-detach.md](2026-10-30-audio-detach.md) | Audio stream/device lifetime, detached expiry and reattach timing, retry energy policy, notification callbacks, and window-free regressions |
| [2026-10-30-stream-tail.md](2026-10-30-stream-tail.md) | Stream-tail visibility/counting, destroy-time drawing and ordering, agent replies, and window-free regressions |
| [2026-10-30-clock-av-sync.md](2026-10-30-clock-av-sync.md) | Clock-mode audio sync: coherent clock rebases, queued-audio retiming, device handoff, and window-free regressions |
| [2026-10-30-pre-release-commits.md](2026-10-30-pre-release-commits.md) | The pre-release commits, later squashed into the initial commit; native LZ ABI/allocation, occlusion, pacing memory/reporting, and uncommitted-fix rechecks |
| [2026-10-30-native-codecs.md](2026-10-30-native-codecs.md) | Native codec correctness, retained memory, allocation failure, C ABI bounds, and hot-path allocations |
| [codex-core.md](codex-core.md) | Transport, wire, session, canvas |
| [codex-viewer.md](codex-viewer.md) | Viewer window, presenter, input, clipboard |
| [codex-tests.md](codex-tests.md) | Test coverage review and known-failing tests |
| [codex-verify.md](codex-verify.md) | Verification rounds, including tasks 10 (MJPEG stream tests) and 11 (allocation tests) |

These are history. Line references in older rounds point at the files as they were then; the spec was
`CLAUDE_DRAFT.md` at the repository root (now [../design/spec.md](../design/spec.md)) and the decisions were a single
`docs/decisions.md` (now [../decisions/](../decisions/README.md)).
