# 0032. Zig is allowed as a build-only tool for vendored native libraries

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human ("allow zig", 2026-10-30), on the v1 terminal survey

- **Context:** the v1 built-in terminal would use libghostty-vt as its VT engine. libghostty-vt builds only with Zig.
  The spec's rejected alternatives list Zig "for the app" because of stale model output.
- **Decision:** Zig may be used as a build tool for vendored native libraries such as libghostty-vt.
  - The version is pinned, and the libraries are built from pinned sources like the other native code.
  - The app itself stays C#.
  - We don't write or maintain Zig code of our own beyond build glue.
  - The built library exposes a C ABI and is called through `[LibraryImport]`.
- **Reason:**
  - libghostty-vt is MIT-licensed, builds from source and is the strongest engine in the survey
    (transparency, see [../design/principles.md](../design/principles.md)).
  - Allowing its build tool is cheaper than writing a VT engine ourselves.
  - Cost: a second native toolchain to pin and to cross-build for x64 (x86-64-v3) and ARM64.
