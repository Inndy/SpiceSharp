# 0038. The v1 PVE launcher: WinUI 3, unpackaged, self-contained, Native AOT; a separate process per console

- Status: Accepted
- Date: 2026-10-31
- Decided by: the human (2026-10-31: WinUI 3 per spec; token + username/password, passwords stored too); details by
  the agent (spec silent), listed under "Open questions" where they need the human

- **Context:**
  - The spec's v1 launcher is "a separate WinUI 3 executable [that] lists VMs through the PVE API, then starts
    `SpiceViewer.exe --vv-stdin`", and it MUST NOT change the viewer's code paths.
  - The human chose WinUI 3 over Win32 knowing it costs size and cold start, asked for a pinned Windows App SDK,
    unpackaged, self-contained (the portable zip of [0020](0020-v0-ships-as-a-portable-zip.md) must work without a
    runtime install), Native AOT if the SDK allows it, on x64 and ARM64.
  - Consoles stay separate processes ([0034](0034-v1-viewer-adds-vnc-and-stays-a-separate-process.md)); text
    consoles go through PveTerm ([0036](0036-v1-terminal-bridge-to-the-users-terminal-first.md)); the API's TLS rule is
    [0033](0033-pve-api-tls-pins-a-given-ca-else-uses-system-trust.md); sign-in and secrets are
    [0039](0039-pve-sign-in-token-or-password-with-tfa-secrets-in-credential-manager.md).
  - [../design/principles.md](../design/principles.md): flexibility asks for a UI-free core other front ends can use;
    transparency asks that limits are stated.

- **Decision:**
  - **Projects.**
    - `src/Pve.Api` (library, no UI): the API client, sign-in, ticket renewal, `cluster/resources`, `spiceproxy` to
      .vv text, the viewer and terminal hand-offs, Credential Manager, the server list and its paths. It uses
      `Pve.Term` for the TLS policy, the descriptor and the hand-off pipe, and `VvFile` to check the .vv text.
    - `src/PveLauncher` (`PveLauncher.exe`): only the window and its dialogs. The viewer is not touched.
    - `tests/Pve.Api.Tests`: window-free tests against a loopback HTTPS fake; the test exe doubles as a fake viewer and
      a fake PveTerm.
  - **Packaging.**
    - The components of **Windows App SDK 2.5.1** (published 2026-09-16), pinned to the versions that release names:
      `Microsoft.WindowsAppSDK.WinUI` 2.3.9, `Foundation` 2.3.12, `InteractiveExperiences` 2.1.9 and `Base` 2.0.4.
      WebView2 1.0.3719.77 comes with WinUI. The metapackage would add AI, ML, Widgets and Search, which the launcher
      doesn't use.
      - Version choice (coordinator, 2026-10-31): the newest release that has been out at least two weeks. 2.6.4
        (2026-10-27) was used first; nothing in it was needed.
      - All four are named explicitly: WinUI 2.3.9 alone asks for InteractiveExperiences 2.1.8, which was never
        published, so NuGet would pick 2.1.9 with a warning.
    - `WindowsPackageType=None` (unpackaged), `WindowsAppSDKSelfContained=true`, `SelfContained=true`.
      `EnableMsixTooling=true` builds no MSIX; it imports the target that publishes the app's `PveLauncher.pri`
      (compiled XAML is embedded there), which a plain .NET publish leaves out.
    - TFM `net10.0-windows10.0.19041.0`, minimum 10.0.17763.0, like the viewer
      ([0015](0015-windows-tfm-net10-0-windows10-0-17763-0.md)). x64 builds use x86-64-v3
      ([0026](0026-x64-builds-target-x86-64-v3.md)).
    - **Native AOT works.** `dotnet publish -r win-x64` and `-r win-arm64` succeed with no trim or AOT warnings
      (2026-10-31, MSVC 14.51 linker, both architectures on the x64 build machine). Bindings are `x:Bind` only (no
      reflection); dialogs are built in code; JSON uses a source-generated context.
    - Measured publish output (2026-10-31, 2.5.1 components, without symbols):

      | | Files | Total | `PveLauncher.exe` |
      |---|---|---|---|
      | win-x64 | 236 | 65.6 MB | 8.6 MB |
      | win-arm64 | 236 | 70.7 MB | 8.9 MB |

      About 90 of the files are the Windows App SDK's per-language resource folders.
  - **Zip layout** (`tools/package.ps1`, unless `-NoLauncher`):
    - `PveTerm.exe` beside `SpiceViewer.exe`;
    - `PveLauncher\` with `PveLauncher.exe` and everything the Windows App SDK needs, so the viewer's folder stays
      readable.
    - The launcher looks for `SpiceViewer.exe` in its own folder, then in its parent (`LauncherPaths.FindHome`), and
      takes `PveTerm.exe` from the same place. A flat folder works too.
    - Measured (2026-10-31, real native DLLs): the win-x64 zip grows from 3.1 MB (`-NoLauncher`) to 31.3 MB.
    - `LICENSE.txt` adds the Windows App SDK (Microsoft Software License Terms and its third-party notices), the
      WebView2 SDK, C#/WinRT and the Windows SDK .NET projection.
      - The projection's package has no license text, only a link. Its text is taken from
        `Microsoft.Windows.SDK.Win32Metadata` (`sdk_license.txt`), which CsWin32 already restores.
      - See [licenses/README.md](../../licenses/README.md).
    - Shipping `PveTerm.exe` "with the v1 launcher" is one of the options of 0036's open question 1; the terminal
      action needs it.
  - **Settings.**
    - The viewer's rules ([0024](0024-settings-portable-by-default-opt-in-install.md)) apply, from the folder that
      holds `SpiceViewer.exe`, so one portable copy has one `data\` folder.
    - The launcher's file is `launcher.json` there, next to the viewer's `settings.json`, which it doesn't touch.
      It is UTF-8 JSON, version 1, written with a source-generated context. It holds:
      - `servers`: each with `id`, `name`, `url`, `auth` (`token` or `password`), `user` and `realm`, or `tokenId`,
        and an optional `ca` (PEM);
      - `terminal`: `default`, `wt` or `conhost`;
      - `lastServer`.
    - No secret is in it. It is read once at startup and written (temp file, then an atomic move) only when the user
      changes something. Comments and trailing commas are accepted; unknown values fall back. A file that can't be
      read is moved to `launcher.json.bad` at the next save.
    - The log is `launcher.log` in the viewer's log folder (1 MB cap, then started over), with no secret, ticket or
      form value. Last writer wins if two launchers edit the list at once.
    - `--install` is the viewer's and copies only the viewer; the launcher stays portable.
  - **Window.**
    - The saved servers, with Add, Edit, Remove, Sign out, and "Forget saved..." (this server's secret, or all of
      them). Selecting a server signs in, if needed, and lists its guests.
    - The guest list shows VMID, name, node, type (qemu or lxc, templates marked) and status. Stopped guests are
      dimmed. A filter box matches the name, VMID or node.
    - **Refresh** (button or F5) fetches the list again. Nothing refreshes on a timer (energy rules); the list is also
      fetched after each sign-in.
    - **Open console** (double-click, Enter, or the button) on a VM:
      1. `POST .../spiceproxy` with `proxy` set to the API host, as the web UI does.
      2. The answer becomes .vv text, checked with `VvConnection.Parse`.
      3. `SpiceViewer.exe --vv-stdin` starts, and the text goes to its standard input through the inherited
         anonymous pipe, UTF-8 without a BOM. The pipe then closes.
      4. The ticket never touches a file or a command line. Errors, for example "500 VM 100 not running" or no SPICE
         display, are shown in the window's status bar.
    - **Open terminal** (button or context menu; the primary action for a container) builds a `TermDescriptor` and
      hands it to PveTerm, as `tools/pve-vv.py --term` does:
      - the descriptor holds the API token, or a freshly renewed ticket with its CSRF token, plus the pinned CA;
      - it goes over the one-instance, current-user, same-integrity pipe of 0036, with a 30 s deadline, and the
        hand-off ends if the bridge exits first;
      - PveTerm requests the termproxy session itself.

      A VM's terminal is its serial console (Proxmox picks the port). The terminal setting decides where PveTerm runs:
      - `default`: PveTerm started directly in a new console. Windows 11 opens it in the default terminal app.
      - `wt`: a new tab in the most recent Windows Terminal window (`wt.exe -w 0 new-tab --title ... --`).
      - `conhost`: `conhost.exe PveTerm.exe ...`.

      The command line carries only the pipe name and nonce. With `default`, a non-zero PveTerm exit code is reported
      in the window, waited for on the process handle.
    - Templates have no console action. VNC consoles are future work (0034); a VM without SPICE shows the server's
      error.
  - **Limits on each API request** (amended 2026-10-31 after review L3/L4,
    [../reviews/2026-10-31-launcher.md](../reviews/2026-10-31-launcher.md)):
    - one 30-second deadline covers connect, headers and body, so a server that stalls mid-body can't leave the
      window busy;
    - the body is read in pieces and refused once it passes 16 MiB, length given or not;
    - an error answer is read only up to 64 KiB, for its message.
  - **Energy.** The launcher has no timer of its own:
    - the request deadline exists only while a request runs;
    - ticket renewal is lazy ([0039](0039-pve-sign-in-token-or-password-with-tfa-secrets-in-credential-manager.md));
    - the HTTP pool closes idle connections after 60 s, after which its cleanup timer stops;
    - waiting for a PveTerm exit uses the process handle.

- **Reason:**
  - The human chose WinUI 3 and self-contained deployment. Native AOT keeps the launcher's cold start close to the
    other two programs.
  - Referencing the WinUI component alone avoids shipping AI and ML runtimes nobody uses.
  - A subfolder keeps the zip's root to the files a viewer user looks for, without changing the hand-off contract.
  - A UI-free `Pve.Api` lets another front end (a CLI, or a future in-viewer list) reuse the sign-in and hand-offs.
  - Costs:
    - About 28 MB more zip, 236 more files, and a second license regime in the package. The Windows App SDK is under
      Microsoft's proprietary terms, against the transparency principle's preference for components we can build.
    - Its license says it may collect usage data; nothing here has measured whether this build sends any.
    - The launcher's cold start was not measured: the desktop was not available to this change.

- **Open questions (human):**
  1. The zip now includes the launcher by default. The Windows App SDK's license asks distributors to have end users
     accept terms at least as protective as Microsoft's; `LICENSE.txt` says so, and it reserves rights the MIT parts
     don't. Ship it in the main zip, or as a separate launcher zip (`-NoLauncher` already gives the viewer-only one)?
  2. The `PveLauncher\` subfolder instead of the flat folder the task described: keep it?
  3. Keep all ~90 Windows App SDK language folders (the built-in controls' own strings), or ship English only to save
     space?
  4. Should a stopped VM offer "Start", and should the status refresh live? Today both are manual, by design: no
     action changes guest state, and nothing polls.
  5. `--install` copies only the viewer. Should it also install the launcher, or should the launcher get its own
     install?
