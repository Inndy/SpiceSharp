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
