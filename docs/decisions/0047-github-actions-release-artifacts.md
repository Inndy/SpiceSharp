# 0047. GitHub Actions builds portable release artifacts with Clang codecs

- Status: Accepted
- Date: 2026-11-01
- Decided by: the human (2026-11-01: "ok let's still using clang to build by default.
  merge this with GitHub CI branch and push to main"). Initial workflow by agent;
  compiler selection amended at integration to follow this instruction.

- **Context:** Source is hosted on GitHub; GitLab CI requires a separate prepared runner. The human requested
  GitHub CI while another agent improves MSVC native build performance. Decision 0046 allows MSVC codecs.
- **Decision:** Add a GitHub Actions workflow on hosted Windows 2025 x64 runners, with x64 and ARM64 targets.
  Use `native/build.sh` with Clang/llvm-mingw, the existing PE verification script, .NET SDK selection from global.json, and the existing
  Native AOT package script including the launcher, terminal, licenses and native rebuild sources. Run the seven
  window-free test projects sequentially on x64; cross-build ARM64 without claiming ARM64 execution coverage.
  Trigger on main pushes, version tags, pull requests and manual dispatch. Keep zip/checksum and TRX artifacts
  for 14 days. Pin actions to commits, grant read-only repository access and disable checkout credential persistence.
  Do not publish a GitHub Release automatically or introduce signing credentials. Existing GitLab CI stays available.
  Pin the Windows-hosted llvm-mingw 20261020 UCRT archive by SHA-256 before extraction; its x64 and ARM64
  target compilers share the existing CMake toolchain contracts. MSVC setup follows codec compilation and is
  used for the .NET Native AOT linker, not as the default native compiler.
- **Reason:** Hosted disposable runners make downloadable packages available without a private build machine or
  Proxmox access. Reusing the existing Clang build retains the faster measured codec path (0048) without
  duplicating compiler flags. Actions artifacts can later be promoted to a release after review. MSVC codec
  builds remain opt-in. No GUI, guest or hardware performance validation is implied.
