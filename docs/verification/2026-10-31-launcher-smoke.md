# 2026-10-31: PVE launcher smoke test (AOT zip, no Proxmox)

The first run of the v1 launcher window ([0038](../decisions/0038-v1-pve-launcher-winui3-unpackaged-self-contained-aot.md),
[0039](../decisions/0039-pve-sign-in-token-or-password-with-tfa-secrets-in-credential-manager.md)), without any real
Proxmox server or credential.

- Build: `main` at de0c475, `pwsh tools/package.ps1` (win-x64), which ran clean: no warnings and a 31.35 MB zip. Run
  from the unzipped layout as `PveLauncher\PveLauncher.exe` (8,998,400 bytes). `PveLauncher\` holds 236 files and
  65.6 MB, as 0038 measured.
- Client: Windows 11 build 26100, dark app mode, 150 % scaling, two monitors. The window opened at 1120x700 DIPs
  (1662x1041 px).
- Servers: a refused port (`https://127.0.0.1:8006`, nothing listening) and a loopback fake pveproxy on
  `127.0.0.1:18006`. The fake was a throwaway Python HTTPS server with a per-run CA, pinned in the server entry. It
  answered `/version` and a canned `cluster/resources` list for any API token: three guests, one template and one
  storage entry. It answered `spiceproxy` with `500`, so no viewer was started. The real PVE server was not contacted.
- Driving: UI Automation and real mouse clicks. Screenshots were taken per window handle with Windows.Graphics.Capture.
  The tools and the fake stayed in a scratch folder.

## Results

| Check | Result |
|---|---|
| Window opens | yes, `PVE Launcher`. Empty state shows "Add a Proxmox VE server to begin." |
| Start time: process start to window handle / to UI tree reachable (`Add` button found) | 295 / 419 ms first run after unzip (display was off), then 184 / 259, 182 / 256, 206 / 283 ms |
| Rendering | Dark theme and a readable layout at 150 % (screenshots). **The title bar stays light** (B2). |
| Add server dialog | **fails: no dialog appears** (B1) |
| Edit (button and double-click), Remove, Forget saved… | **fail the same way: no dialog** (B1) |
| Select a server with no saved secret | **no sign-in prompt, no message**. The server stays selected with an empty list (B1). |
| Refused server, secret pre-seeded in Credential Manager | Clean error in the status bar: "Could not list the guests / Could not reach 127.0.0.1:8006: (Windows' connection-refused text) (127.0.0.1:8006)". Logged once, no crash. |
| Fake server, pinned CA, pre-seeded secret | TLS against the pinned CA works. `GET /version` 200 ×2 (B6), `GET /cluster/resources` 200, "4 guests." The storage entry was skipped, the VMID sent as a string was read, and stopped guests and the template were dimmed. Heading reads "fake (smoke test) · PVE 8.4.1 · smoke-test@pve!launchersmoke". |
| Filter | `node2` gave 102 and 9000. `ALPHA` gave 100 (case-insensitive). `10` gave 100, 101 and 102. `zzz` gave nothing, and empty restored all four. |
| Double-click a running VM | `POST /nodes/node1/qemu/100/spiceproxy` with the token. The 500 is shown as "Could not open VM 100 (alpha-win) / 500 fake: spiceproxy disabled in the smoke test". No viewer started. |
| Double-click the template | Warning "VM 9000 (tmpl-debian) is a template; it has no console." No request. |
| Restart with saved state | The last server was re-selected and its list fetched 76 ms after the `start` log line. The terminal choice was kept. |
| Memory, 10 s after start (display on) | Empty settings: private 107.0 MB, working set 152.7 MB (peak 160.7), 81 threads, 1,803 handles. Saved server with list loaded: 109.0 / 161.5 MB (peak 165.7), 2,033 handles. After all the clicks above: 143.7 / 205.1 MB. |
| Idle 60 s, right after the spiceproxy call | CPU time +15.6 ms (one GetProcessTimes tick). The one socket, the pooled keep-alive connection to 127.0.0.1:18006, closed by itself during the minute, as `PooledConnectionIdleTimeout` (60 s) intends. No TCP or UDP endpoint afterwards. |
| Idle 60 s, pool empty | CPU time +0 ms. 16.4 M cycles (`QueryProcessCycleTime`), a few milliseconds. Threads 77 → 75 as idle pool threads retired. No TCP or UDP endpoint before or after, and no outbound connection in any snapshot, telemetry included. Snapshots can't see a connection that opens and closes between them. |
| Close | WM_CLOSE exited in 106 ms with code 0. The caption Close button exited in 59 ms and 61 ms with code 0. `exit` was logged each time. |

## Findings

**B1 (blocker): every dialog throws in the AOT build, and the exception disappears.** Add, Edit, Remove, Forget
saved… and the sign-in and second-factor prompts all build their content with `Dialogs.Note()`. That helper casts
`Application.Current.Resources["TextFillColorSecondaryBrush"]` to `Brush`, and `ErrorText()` does the same with
`SystemFillColorCriticalBrush`. In the Native AOT exe both lookups come back typed only as
`Microsoft.UI.Xaml.DependencyObject`, so the cast throws `InvalidCastException` before `ShowAsync`. A user can't add
a server or sign in. This run got past it only by writing `launcher.json` by hand and putting test secrets into
Credential Manager.

The exception also leaves no trace. Nothing appears on screen, nothing reaches `launcher.log`, and the process keeps
running and responding. procdump, attached with a non-matching filter, saw three first-chance CLR exceptions and one
`0x40080201` (RoOriginateError) per click. The sign-in path is no different: the exception isn't a
`PveApiException`, so `RunAsync` doesn't catch it, and it leaves `OnServerSelected` the same silent way. The `finally`
does clear the busy state.

The cause comes from a diagnostic copy of the launcher, built and AOT-published in the scratch folder with a logging
`try`/`catch` around the dialog. The repository was not changed.

```
DIAG TextFillColorSecondaryBrush -> Microsoft.UI.Xaml.DependencyObject; is Brush: False
DIAG SystemFillColorCriticalBrush -> Microsoft.UI.Xaml.DependencyObject; is Brush: False
DIAG add threw: System.InvalidCastException: Arg_InvalidCastException
   at PveLauncher.Dialogs.Note(String) + 0x461
   at PveLauncher.Dialogs.<EditServerAsync>d__4.MoveNext() + 0xb01
--- End of stack trace from previous location ---
   at PveLauncher.MainWindow.<OnAddServer>d__34.MoveNext() + 0x77
```

Whether a JIT build behaves differently wasn't settled. A `dotnet build` output folder of the same copy crashed at
startup in `Microsoft.UI.Xaml.dll` with exception `0xc000027b`. That isn't the shipped layout and wasn't pursued.
No automated test covers this, because none runs the AOT exe's UI.

**B2 (cosmetic): the title bar is light in dark mode.** The caption stays light, active or not, over the dark
content.

**B3 (cosmetic): the Type column clips templates.** The 56-DIP column shows "qemu (template)" as "qemu (te".

**B4 (cosmetic): the signed-in heading wraps.** "fake (smoke test) · PVE 8.4.1 · smoke-test@pve!launchersmoke" takes
two lines. The filter box then stretches to the row's height and Refresh moves down. A real host name and account
make the heading as long or longer.

**B5 (minor): Sign out is enabled after a failed connect.** With a saved token the client counts as signed in once
the token is set, even when `/version` never answered (connection refused).

**B6 (minor): `/version` is fetched twice per sign-in.** `PveSignIn` checks the token with it, then
`LoadGuestsAsync` fetches it again for the heading.

## Notes on the method

- While the monitors were powered off (unattended machine), the window was created and its UI tree existed, but its
  content wasn't rendered. Captures showed only the caption over black until a zero-distance mouse move woke the
  display. That comes from the display being off, not from the app.
- The secrets were generic credentials named `SpiceViewer.PVE/127.0.0.1:8006/...` and
  `SpiceViewer.PVE/127.0.0.1:18006/...`, under the made-up token ID `smoke-test@pve!launchersmoke`. They were added with
  `cmdkey` and deleted afterwards (`cmdkey /list` shows none). No `.vv` file was written. The fake's key was deleted.
- Windows' own strings (the connection-refused text, the InfoBar's icon and close-button names) follow the OS display
  language. That is expected.

## Re-check: B1–B6 fixes, 2026-10-31 05:29–05:50

- Build: this branch on top of b16987d. The launcher's own AOT publish (`dotnet publish src/PveLauncher -c Release
  -r win-x64`) for the step-by-step checks, then `pwsh tools/package.ps1` (win-x64): no warnings in any of the three
  native compilations, a 31.38 MB zip, 236 files in `PveLauncher\`. The last round ran from the unzipped zip.
- Same client, method and fake as above: the loopback fake on `127.0.0.1:18006` with a fresh per-run CA, and the
  refused port `127.0.0.1:8006`. This time the servers were added through the Add dialog, and the token secret was
  typed into the sign-in prompt with "Remember" ticked, which saved `SpiceViewer.PVE/127.0.0.1:18006/token/...`.
  "Forget saved…" → "Forget this one" deleted it at the end; `cmdkey /list` shows no `SpiceViewer.PVE/*` entry.

| Check | Result |
|---|---|
| B1: Add, Edit, Remove, Forget saved… and the sign-in prompt | **fixed.** Every dialog appears, in the step build and in the zip. Its notes are in the secondary text colour, and the prompt's "Enter the token secret." is in the critical colour. No fallback was logged: once the code names `SolidColorBrush`, the AOT build resolves both theme brushes as brushes. That supports the guess that the class's projection had been trimmed. |
| B1: the full flow through the dialogs | Add (name, address, API token, token ID, pasted CA PEM) → Save → sign-in prompt → Sign in with Remember → "4 guests." Edit and Remove → Cancel. Remove → Remove deleted the refused server. |
| B1: unexpected exceptions are visible | Checked with a scratch build that threw on purpose in three places, and was then reverted. A throw in the Add dialog (async handler, guard) showed "Could not add the server / The launcher hit an unexpected error (InvalidCastException: Arg_InvalidCastException)". One in Sign out (sync handler, `Application.UnhandledException`) showed "Unexpected error / … (InvalidOperationException: sync handler test)". One in the sign-in prompt (inside `RunAsync`) showed "Could not list the guests / …" with Sign out disabled. Each was logged as `unexpected: <what>: <type>: <message>` with its stack trace, and the window kept running. |
| B2: title bar | **fixed.** The caption is dark in dark mode (screenshots, active window). |
| B3: Type column | **fixed.** "qemu (template)" fits in the 110-DIP column. |
| B4: heading | **fixed.** "fake (smoke test)" at subtitle size, then "PVE 8.4.1 · smoke-test@pve!launchersmoke" in smaller secondary text, on one line. The filter box keeps its height. In a window 727 DIPs wide, the heading is trimmed to "fake (sm…". |
| B5: refused server | **fixed.** After "Could not reach 127.0.0.1:8006: …", Sign out is disabled. |
| B6: `/version` | **fixed.** The fake logged one `GET /version` and one `GET /cluster/resources` per sign-in, over seven sign-ins (7 `/version`, 9 lists). Re-selecting a signed-in server fetched only the list. |
| Context menu (new finding) | A right-click on a row that wasn't selected opened the menu for no row: both items were disabled, and "Open SPICE console" also showed on a container. The `ContextRequested` handler on the list never ran. It now sits on the row template. Right-clicking the container selects it and shows only "Open terminal", the running VM shows both items enabled, and the template shows both disabled. |
| Open console on a VM | `POST .../spiceproxy`; the fake's 500 is shown as before. No viewer was started. |
| Start time, process start to window / to `Add` reachable | 163–351 ms / 243–436 ms in the step build. The first run from the fresh zip took 490 / 564 ms. |

**The idle "+15.6 ms, one tick" after use is not the launcher's code.** Per-thread cycle counts (`QueryThreadCycleTime`
with thread descriptions and start modules) were taken over 60 s windows, right after a spiceproxy call:

| Window | Cycles in 60 s |
|---|---|
| Keep-alive connection still pooled, cursor resting over the window | 10.7 M: `DWM Master Input Thread` (`dwmcorei.dll`) 8.6 M; three pool and COM threads 2.1 M |
| Pool emptied during the window, cursor over the window | 16.9 M, almost all the DWM input thread |
| Pool empty, cursor over the window | 10.7 M: DWM input thread 10.7 M, everything else 19 K |
| Pool empty, cursor moved off the window | 0.12 M |

- The launcher has no timer, delay or polling of its own; the code was checked for each.
- While the keep-alive connection is pooled, the HTTP pool's cleanup timer and thread-pool threads cost about 2 M
  cycles (under 1 ms) in its last minute. The connection then closes, as 0038 says. Removing that would mean dropping
  keep-alive, so a fresh TLS handshake on every action, which costs far more.
- The rest comes from WinUI's input thread, and only while the cursor rests over the window. That is the likely source
  of the smoke run's single 15.6 ms tick, since the cursor stayed on the row it had double-clicked. With the cursor
  elsewhere the process is close to silent. It isn't the launcher's code, and the app can't turn it off.

**Left as found:** in a window about 730 DIPs wide, the Name column shrinks to nothing, since the window has no minimum
size. The pinned-CA box's header is drawn in Consolas, because the box's font applies to its header too.
