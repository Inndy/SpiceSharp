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
