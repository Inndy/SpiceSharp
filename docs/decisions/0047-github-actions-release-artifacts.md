# 0047. GitHub Actions builds portable release artifacts with MSVC

- Status: Proposed
- Date: 2026-11-01
- Decided by: agent (spec silent), not yet reviewed; human requested GitHub CI for release build artifacts

- **Context:** Source is hosted on GitHub; GitLab CI requires a separate prepared runner. The human requested
  GitHub CI while another agent improves MSVC native build performance. Decision 0046 allows MSVC codecs.
- **Decision:** Add a GitHub Actions workflow on hosted Windows 2025 x64 runners, with x64 and ARM64 targets.
  Use the existing MSVC build and PE verification scripts, .NET SDK selection from global.json, and the existing
  Native AOT package script including the launcher, terminal, licenses and native rebuild sources. Run the seven
  window-free test projects sequentially on x64; cross-build ARM64 without claiming ARM64 execution coverage.
  Trigger on main pushes, version tags, pull requests and manual dispatch. Keep zip/checksum and TRX artifacts
  for 14 days. Pin actions to commits, grant read-only repository access and disable checkout credential persistence.
  Do not publish a GitHub Release automatically or introduce signing credentials. Existing GitLab CI stays available.
- **Reason:** Hosted disposable runners make downloadable packages available without a private build machine or
  Proxmox access. Reusing build scripts picks up MSVC performance improvements without duplicating compiler flags.
  Actions artifacts can later be promoted to a release after review. This does not change the default local MinGW
  toolchain or claim that MSVC performs as well. No GUI, guest or hardware performance validation is implied.
