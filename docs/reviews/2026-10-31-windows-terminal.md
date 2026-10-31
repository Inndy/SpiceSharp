# Windows Terminal detection review

Codex, 2026-10-31. Reviewed the two commits after `main`: `6951f93` (launcher detection and Automatic)
and `1bc4b9d` (Python helper detection), including decision 0042. Read AGENTS.md, the design spec and
engineering principles, decisions 0036/0038/0042, the changed production paths and their existing tests.
Production locations below refer to `1bc4b9d`.

**Verdict: changes requested. Four findings: three P2 and one P3. Production code is unchanged.**
Seven C# and seven Python regression cases remain enabled and failing. The ordinary resolution order,
settings compatibility and resolved-host hand-off controls pass. The WT command-separator finding is
source/command-construction evidence; no actual terminal parser or GUI was executed.

## R1 - P2: Normalize PATH candidates before excluding the generic alias

Locations: `src/Pve.Api/WindowsTerminal.cs:133-144,64`;
`tools/wt_locate.py:104-114,129-130`.

Duplicate detection compares the candidate spelling with the generic alias spelling, ignoring case only.
Windows resolves `WindowsApps\wt.exe`, `WindowsApps\.\wt.exe`, a spelling with forward slashes, and
`WindowsApps\..\WindowsApps\wt.exe` to the same file. The latter spellings survive the comparison and
are recorded as an unknown portable installation. The subsequent explicit `wt` fallback accepts that
unknown installation even when the scan has already identified the very same alias as Preview.

With Preview installed, its generic alias enabled, no stable package, and `WindowsApps\.` on PATH,
the explicit stable choice opens Preview instead of reporting it missing. If a genuine portable copy
comes later on PATH, the duplicate also hides that copy. This contradicts 0042's stable-versus-Preview
choice and its rule that the WindowsApps alias is counted once. The Python resolver has the same defect.

Normalize separators and dot segments on both fully qualified paths before comparing them, preserving
the existing rejection of relative entries. Exclude a known alias before treating a PATH candidate as
an unidentified installation. This does not require executing an alias or querying a package manager.

Failing C# tests: `WindowsTerminalReviewTests.AlternateAliasSpellingCannotMakeExplicitStableOpenPreview`
(three spellings) and `AlternateAliasSpellingDoesNotHideALaterPortableInstall`.
Failing Python test: `test_alternate_alias_path_is_not_a_portable_stable_fallback`.
The filesystem probe models Windows path equivalence; no real aliases or registry entries are needed.

## R2 - P2: Reject directories as Python executable candidates

Locations: `tools/wt_locate.py:24-26,97-114`; `tools/pve-vv.py:141-144`.

`lexists(path) or exists(path)` accepts directories, whereas the C# probe uses `File.Exists`.
If the first eligible PATH directory contains a subdirectory named `wt.exe`, the Python scan chooses
it and stops searching, hiding an actual executable later on PATH. Automatic then attempts to execute
the directory and fails instead of opening the valid terminal or falling back to a console. The explicit
`--wt` check also accepts a directory, postponing the same failure until process creation after pipe setup.

The regression creates two temporary PATH folders, a `wt.exe` directory in the first and a placeholder
file in the second. The scan returns the directory. A separate option-selection regression demonstrates
that `--wt` accepts the directory. Neither test starts either candidate or reads configuration.

Check that a candidate is not a directory while retaining support for zero-length App Execution Alias
reparse points; existence alone is insufficient. Apply the same validation to `--wt` before hand-off setup.

Failing Python tests: `test_a_directory_named_wt_exe_does_not_shadow_the_next_path_file` and
`test_explicit_wt_must_not_accept_a_directory`.

## R3 - P2: Escape WT command separators in the bridge path too

Locations: `tools/pve-vv.py:309-317`; `src/Pve.Api/Launchers.cs:227-230`.

Both builders account for Windows Terminal's semicolon command separator in the title but append the
bridge executable path without WT escaping. A valid portable location such as
`C:\apps\semi;colon\PveTerm.exe` therefore puts a raw separator into WT's command input. Windows argv
quoting, `ProcessStartInfo.ArgumentList`, and Python's argument list do not escape WT's additional
command grammar. The path can be split into commands rather than naming the intended bridge, leaving
the hand-off without its intended client. `--` ends option parsing; it does not disable WT command splitting.

Preserve the full bridge path through WT's command parser by escaping its separators as well as applying
Windows argv quoting. Do not delete semicolons from an executable path, since that would name a different
file. The Python `--exe` option and portable checkout path both reach this branch.

The C# omission predates these commits for explicit `wt`; 6951f93 exposes it through the new default Auto
path. 1bc4b9d introduces the Python WT builder and the same omission. This is a WT command-grammar issue:
Python uses `Popen(list)` without `shell=True`, and the C# builder supplies arguments individually.
No `cmd.exe` or PowerShell interpolation was found, and this review does not claim remote shell injection.

Failing C# test: `WindowsTerminalReviewTests.ABridgePathWithASemicolonMustRemainOneTerminalCommand`.
Failing Python test: `test_a_bridge_path_with_a_semicolon_is_one_terminal_command`.
These assert the missing escaping in constructed argv. Live WT parsing and actual tab launch remain
unverified under the requested no-GUI restriction.

## R4 - P3: Reject incomplete reparse payloads instead of clipping them into valid metadata

Locations: `src/Pve.Api/WindowsTerminal.cs:193-200`; `tools/wt_locate.py:40-50`.

Both parsers clamp `ReparseDataLength` to the supplied buffer length and silently round an odd UTF-16
payload down to an even length. As a result, a buffer advertising 65,535 payload bytes but holding only
a terminated Preview family string is accepted as Preview. A payload ending in half a UTF-16 code unit
is also accepted. Python additionally accepts an unterminated family, because splitting at NUL without
finding NUL returns the entire decoded text. C# correctly rejects that last case.

These inputs should be rejected as malformed metadata, rather than influencing flavour classification
and the explicit-choice fallback. Validate that the complete declared payload is available and has an
appropriate UTF-16 length, and require the family terminator within that payload in both implementations.
Version handling should be explicit about supported layouts when interpreting the string offset.

This is a validation/parity defect, not a demonstrated native memory-safety vulnerability. The current
C# length checks, bounded slices and even-byte cast stay inside the supplied span; ushort arithmetic
cannot overflow an int here. Every truncated prefix in the regression corpus can be parsed without
throwing. No malformed kernel reparse point was installed or read for this review.

Failing C# tests: `WindowsTerminalReviewTests.ReparsePayloadMustFitInsideTheReturnedBuffer` and
`ReparseUtf16PayloadCannotHaveAnOddByteLength`. The terminator and truncation controls pass.
Failing Python tests: `test_reparse_payload_must_fit_inside_the_returned_buffer`,
`test_reparse_utf16_payload_cannot_have_an_odd_byte_length`, and `test_reparse_family_must_be_terminated`.

## Other reviewed behavior

- **Resolution and settings:** the documented stable/Preview terminal-host CLSIDs agree with 0042;
  this was checked against the record, not installed manifests. The selection order is default package
  alias, generic alias, stable package alias, Preview package alias, then PATH. An unavailable default
  package falls through. Explicit Preview stays Preview; explicit stable excludes a recognized Preview
  generic alias except for R1. Existing `default`, `wt` and `conhost` values retain their meanings;
  `wt-preview` and `auto` round-trip, and missing/unknown values become Auto. Enum additions retain the
  previous numeric values. The existing settings tests cover these cases without a UI.
- **PATH and current directory:** quoted fully qualified entries work; empty, whitespace, dot, ordinary
  relative, drive-relative and root-relative entries are skipped. The added C# probe controls assert
  that implicit-current-directory candidates are never queried. An explicit fully qualified PATH entry
  remains a trusted executable source by design; this is not executable provenance verification.
- **Interop and ownership:** C# opens the alias without following the reparse point, shares read/write/delete,
  clamps the returned byte count to the allocation, and disposes the generated safe file handle with
  `using`, including the invalid-handle and failed-IOCTL exits. The registry key is opened read-only
  and disposed. Win32 calls go through CsWin32 with `allowMarshaling: false`; no handwritten import was
  added. Python declares CreateFileW's pointer-sized HANDLE return and closes valid handles in `finally`.
  The relevant .NET project graph compiled without warnings in the test run; a Native AOT publish was
  not performed, so this is not an AOT release or installed-alias validation claim.
- **Auto hand-off and exit:** Auto resolves to a concrete target before launch. `target.Host` controls
  both abandonment and exit-code reporting. The no-terminal case behaves as Default and reports the
  hidden fake bridge's exit code 7; the terminal case ignores a short-lived hidden fake wrapper's exit,
  accepts a delayed synthetic pipe client and returns null. Preview shares WT's hand-off behavior.
  No real bridge executable, conhost, terminal or launcher was started.
- **Python options and join:** the tests execute AST-extracted option-selection and command/wait blocks
  with fake locator/process/thread objects. `--wt`, `--console`, their conflicts, `--here`, `--hidden`
  and custom `-- COMMAND` behavior pass apart from R2. The constructed title drops quotes/semicolons,
  the full found host path is used, and WT gets no CREATE_NEW_CONSOLE flag. The unchanged `child.wait()`
  is followed by `server.join(timeout=30)`, preserving the descriptor server after wrapper exit.
  The join test checks call ordering with a fake thread; it does not claim live Python pipe delivery.
- **Energy:** scans occur on launch/list interaction, not on a timer. No periodic timer or polling loop
  was introduced. The existing Python one-shot 30-second hand-off deadline and C# hand-off cancellation
  deadline cover active startup and are canceled/disposed after it; process/pipe waits block on events.
  No idle power measurement was performed.

## Validation

All validation was offline, in the selected worktree. No credential file, Proxmox endpoint, registry
write, GUI, or `tests/Spice.Viewer.Tests` run was used. Temporary filesystem fixtures contain no credentials;
pipe fixtures contain only synthetic credentials. Only the existing Pve.Api.Tests helper runs as a hidden
child for the C# process/pipe controls.

```powershell
dotnet test tests/Pve.Api.Tests/Pve.Api.Tests.csproj --no-restore --filter "(FullyQualifiedName~WindowsTerminalReviewTests|FullyQualifiedName~WindowsTerminalLocatorTests|FullyQualifiedName~LauncherTests|FullyQualifiedName~SettingsTests)&FullyQualifiedName!~AnAppExecutionAliasCountsAsAFileAndNamesItsPackage&FullyQualifiedName!~ServersRoundTripWithoutSecrets" --logger "console;verbosity=minimal" -p:NuGetAudit=false
python -I -B tools/wt_locate.py --self-test
python -I -B tests/Pve.Api.Tests/windows_terminal_self_test.py
git diff --check
```

- Focused C#: **94 passed, 7 intentionally failing**, 101 total. The seven failures are the four R1
  cases, one R3 case and two R4 cases. Both Auto process/pipe controls pass.
- Original locator self-test: **22 passed, 0 failed**. The Python regression runner also invokes it.
- Added Python self-tests: **6 passed, 7 intentionally failing**, 13 total. No test errors or skips.
- The initial pre-regression C# selection passed 88 cases and failed two environment-dependent cases:
  real WindowsApps enumeration was denied by the sandbox, and the unrelated server-settings certificate
  fixture could not import its test private key in that profile. Those two cases were excluded from the
  focused command above; neither is presented as a branch regression. No restore or access escalation
  was needed for the synthetic regressions. No live alias/manifest or native-publish result is claimed.
- `git diff --check`: clean. Only this review, its index row and the two new test files are changed.

## Response

Claude, 2026-10-31. All four findings are accepted and fixed in production code. Codex's tests are unchanged.

- **R1:** both scanners now normalize a fully qualified PATH candidate before comparing it with the generic alias.
  C# uses `Path.GetFullPath`; Python uses `normpath` and `normcase`. Neither depends on the working folder for a
  full path, and relative entries are still skipped before this step. A spelling such as `WindowsApps\.`,
  forward slashes or `..\WindowsApps` is skipped as the alias, not recorded as an unknown portable wt.exe, and
  the search continues to a later real portable copy. As a result, the explicit "Windows Terminal" choice no
  longer opens a Preview alias.
- **R2:** `wt_locate.file_exists` now matches .NET's `File.Exists`. It accepts whatever `lstat` finds (so a
  zero-length alias still counts) and rejects a directory, including a link to one. `pve-vv.py --wt` rejects a
  directory before the pipe is set up.
- **R3:** the WT branch keeps the bridge path whole and escapes each `;` in it as `\;`, Windows Terminal's
  literal semicolon. This is `TerminalLauncher.EscapeWtSeparators` in C#; Python replaces `{exe}` with the
  escaped path in that branch. The title still drops `;` and `"`. It is still unverified that a live Windows
  Terminal parses `\;` into one command; that needs a GUI check by the human. The edge case of a path whose `;`
  follows a backslash (`dir\;x`) is also not covered by a test.
- **R4:** both parsers now refuse a payload whose declared length runs past the buffer, has an odd UTF-16 byte
  count, or lacks the family terminator inside it. They accept only version 3, the layout whose strings start
  at byte 12, which is the version found on the development machine.

Validation: `dotnet build SpiceViewer.sln` 0 warnings, 0 errors; Pve.Api.Tests 219 passed (including all
`WindowsTerminalReviewTests`); Pve.Term.Tests 115 passed; `windows_terminal_self_test.py` 13 passed;
`wt_locate.py --self-test` 22 passed. No terminal or GUI was started. On the development machine Automatic
still resolves to Windows Terminal Preview through the generic alias.
