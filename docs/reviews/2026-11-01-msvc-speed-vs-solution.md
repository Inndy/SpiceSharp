# MSVC flags and VS solution self-review

Scope: `build/msvc-speed`, compiler flags, VS 2026 native project and solution
build graph, native content copying, and architecture-dependent test paths.
The human explicitly authorized Codex implementation. This is self-review,
not an independent review. No desktop, guest, Proxmox or real credentials used.

## Findings and response

- **P2, resolved: solution-only dependencies did not order transitive builds.**
  A clean parallel solution build could build the viewer through Viewer.Tests
  before the native project, warning about missing DLLs. Native-DLL consumers now
  carry a non-assembly project reference in full-MSBuild x64/ARM64 builds, in
  addition to the solution dependencies. x64 maps managed projects to x64;
  Any CPU stays managed-only. Native content is collected after dependencies,
  during AssignTargetPaths. Verified after removing generated native files:
  all 24 DLL/metadata copies across six x64 consumers matched the native outputs.
  The corresponding 24 ARM64 consumer files also matched.
- **P2, resolved: fixed-depth test paths broke under `bin/x64`.** Six helper
  locations in four terminal test files assumed the Any CPU output depth.
  The new architecture configurations add a path component. Tests now find the
  repository by walking to its solution file; assertions and Python definitions
  under test are unchanged. The old path is provably wrong for platform builds:
  it selected `tests/tools/pve-vv.py`, which does not exist.
- **P3, resolved: whole-target inlining regressed other workloads.** Retained
  `/Ob3` only for MSVC x64 QUIC. Other codec flags, ARM64, static CRT, LTCG,
  AVX2 and PE hardening remain unchanged. The small LZ periodic/layout cost and
  binary-size increase are recorded in decision 0048 alongside the gains.

## Validation limits

See [verification](../verification/2026-11-01-msvc-speed-vs-solution.md).
ARM64 was cross-built and inspected, not executed. Viewer tests were built but
not run because this task does not own the desktop. No algorithm or golden-image
changes, and no new third-party dependency.
