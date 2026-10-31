# 0045. GitLab CI uses a prepared Windows x64 runner and window-free tests

- Status: Proposed
- Date: 2026-11-01
- Decided by: agent (spec silent), not yet reviewed

- **Context:** The human requested public English and Traditional Chinese READMEs and GitLab CI. Native AOT
  packaging and WinUI compilation require Windows tooling. The agent guide makes desktop tests exclusive and
  requires window-free projects to run sequentially. Codec compilation with MSVC is separate ongoing work.
- **Decision:** Use one prepared Windows x64 PowerShell 7 runner, with `windows`, `x64` and `spice-build` tags.
  Build codecs through the integrated Clang entry point, verify DLLs, build the solution, run all seven window-free
  test projects sequentially, then package with the existing script. Keep TRX and package artifacts for 14 days.
  Configure the MSYS2 root through `SPICE_MSYS2_ROOT`. Do not install tools, run GUI tests, use guests, deploy or
  publish releases. Document runner prerequisites in `docs/ci.md` and avoid duplicate branch/MR pipelines using
  [GitLab's workflow rules](https://docs.gitlab.com/ci/yaml/workflow/).
- **Reason:** This exercises the existing Windows build and release path without changing production code or
  coupling CI to the unfinished MSVC codec work. It needs a prepared runner and does not validate ARM64 execution,
  GUI behavior or real-guest performance. Hosted GitLab runners and a GitLab mirror are not assumed to exist.
