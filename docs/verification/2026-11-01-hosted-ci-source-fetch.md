# Hosted CI FFmpeg transport follow-up

2026-11-01. Main integration commit `2d2a0a6` started
[run 41271635535](https://github.com/Inndy/SpiceSharp/actions/runs/41271635535).
Both jobs passed checkout, SDK/MSYS2 setup and the pinned llvm-mingw download,
then failed during native dependency fetching: upstream FFmpeg reset curl's TLS
connection. No native compiler failure was reported; compilation had not started.

A local Windows HTTP client also failed to connect to the same upstream URL,
so changing only the HTTP client was not a sufficient fix. The workflow now
prefetches upstream with a bounded timeout and then tries Alpine Linux's source
cache. The filename and expected SHA-256 still come from native/fetch.sh. Either
successful transfer must match that pin; mismatch stops immediately instead of
falling through. The existing fetch script verifies the cached archive again
before extraction.

Executed the exact amended PowerShell block locally: upstream timed out, the
Alpine source cache succeeded, and its 12,040,788-byte archive matched
`8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e`.
Parsed the workflow YAML and all seven PowerShell run blocks. No downloaded code
was executed by this transport check. No production or test source changed;
the integration's full build, 1,292 tests and x64 AOT package remain applicable.
The follow-up hosted run must validate compilation and packaging on both targets.

## Follow-up run and managed platform selection

[Run 41272198852](https://github.com/Inndy/SpiceSharp/actions/runs/41272198852)
passed the corrected source fetch, both Clang native builds and both PE/ABI
checks. Its ARM64 job completed successfully, including Native AOT publishing,
archive/checksum validation and artifact upload. ARM64 binaries were not executed.

The x64 job stopped during the managed solution build: the developer environment
set `Platform=x64`, selecting the optional native vcxproj through SDK MSBuild.
This correctly refused the native build rather than replacing the Clang DLLs
with MSVC output. CI now explicitly selects solution `Any CPU` and test-project
`AnyCPU`; GitLab uses the same selection to avoid ambient developer-environment
leakage. No production source or test assertions changed.

Reproduced the ambient `Platform=x64` locally and ran the amended explicit
managed-platform commands: the full Release solution passed with zero warnings
or errors and all 38 native tests passed using the prebuilt Clang DLLs. Parsed
both workflow YAML files and their PowerShell blocks. The next hosted run must
complete the x64 test/package path as well as the already-successful ARM64 path.
