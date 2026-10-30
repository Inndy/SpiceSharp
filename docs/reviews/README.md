# Review records

Codex reviews the code and writes tests; Claude (and Claude subagents) implement. Each review file lists findings
with severity, the implementer appends the fixes, and the next round verifies them.

| File | Scope |
|---|---|
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
