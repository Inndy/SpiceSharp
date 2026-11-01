# 0048. Tune MSVC QUIC inlining and include native codecs in the VS solution

- Status: Accepted
- Date: 2026-11-01
- Decided by: the human ("optimize the speed by toggling compiler flags" and
  "add vcxproj to solution, so VS2026 toolchain can build entire project on Windows";
  explicitly authorized Codex implementation). Build details by agent (spec silent),
  not yet reviewed independently.

- **Context:** [0046](0046-msvc-native-codecs.md) permits MSVC, but measured slower
  QUIC/LZ decoding and required a separate native build. The solution contained
  only managed projects. [principles.md](../design/principles.md) prioritizes
  measured speed over size and requires recording regressions and trade-offs.
- **Decision:**
  - Apply `/Ob3` only to the MSVC x64 QUIC translation unit. Preserve `/O2`,
    `/GL` + `/LTCG`, the AVX2 floor, static CRT, exact exports and PE protections.
    Leave ARM64 and the other codec compiler settings unchanged.
  - The flag increases the inline-expansion budget beyond `/O2`'s `/Ob2`;
    see [Microsoft's documentation](https://learn.microsoft.com/en-us/cpp/build/reference/ob-inline-function-expansion).
    No vendored source changes or new runtime dependencies.
  - Add a Makefile `native/Spice.Native.Codecs.vcxproj` to `SpiceViewer.sln`.
    VS 2026 `MSBuild.exe` builds it in x64/ARM64 solution configurations before
    native-DLL consumers. Both Debug and Release use optimized native codecs,
    as before. Any CPU retains the managed-only workflow with prebuilt DLLs.
  - Its PowerShell entry point discovers VS 2026 with vswhere, selects the
    matching x64-hosted developer environment, and invokes the same pinned-source
    MSYS2 build as the CLI. `SPICE_MSYS2_ROOT` selects MSYS2; Scoop's installation
    is the default. This reuses FFmpeg's supported POSIX configure/make workflow
    rather than maintaining a second list of upstream sources in MSBuild.
  - Rebuild/Clean clean only the selected MSVC architecture. Keep downloaded
    sources and the other architecture. Collect native content during build,
    after native dependencies, so a fresh checkout copies the generated DLLs too.
- **Reason and cost:** repeated 101-repetition local runs reduced QUIC fixture
  time by 3.5-3.7% and GLZ periodic time by about 3.5%. The QUIC desktop gain
  varied from 0.2% (within noise) to 1.2%. The DLL grew from 2,015,232 to 2,019,328
  bytes (4,096 bytes, 0.2%). The first long run showed a 0.7% LZ periodic cost
  (under 1 microsecond/batch), which did not persist in the final run. This small
  potential layout cost is accepted for the QUIC/GLZ gains. This is a local
  compiler/layout trade-off, not a promise for every CPU. MSVC remains slower
  than MinGW on QUIC and LZ; MinGW remains available and the CI default.
  Whole-target `/Ob3`, AMD tuning, disabling intrinsics/vectorization, disabling
  LTCG, size preference and reduced inlining did not produce a better balanced
  result. See [verification](../verification/2026-11-01-msvc-speed-vs-solution.md).

This refines the x64 MSVC flag choice in 0046 and extends the solution workflow;
it preserves the DLL ABI, licensing boundaries and existing MinGW build path.
