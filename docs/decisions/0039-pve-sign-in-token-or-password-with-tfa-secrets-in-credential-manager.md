# 0039. PVE sign-in: an API token or a password with TOTP; secrets in Credential Manager

- Status: Accepted
- Date: 2026-10-31
- Decided by: the human (2026-10-31: WinUI 3 per spec; token + username/password, passwords stored too); details by
  the agent (spec silent)

- **Context:**
  - The spec's v1 bullet asked for API tokens only, stored in Windows Credential Manager. The human added user name
    and password sign-in with Proxmox two-factor authentication (at least TOTP). Passwords may be stored too, and the
    user needs a way to forget what is stored.
  - Tickets and CSRF tokens stay in memory and are renewed before their 2-hour end. No secret or ticket may be logged.
  - The launcher must not wake while idle (energy rules). TLS follows
    [0033](0033-pve-api-tls-pins-a-given-ca-else-uses-system-trust.md).

- **Decision:**
  - **API token.**
    - Every request carries `Authorization: PVEAPIToken=USER@REALM!TOKENID=SECRET`; no cookie, no CSRF token.
    - The token ID must be `user@realm!name`. The name follows Proxmox's form `[A-Za-z][A-Za-z0-9._-]*`. The user
      part may itself contain `@`, as LDAP users do.
    - The secret must be printable ASCII without spaces.
    - `GET /version` checks the token at sign-in.
    - Amended 2026-10-31 after the smoke test (B5,
      [../verification/2026-10-31-launcher-smoke.md](../verification/2026-10-31-launcher-smoke.md)): a sign-in that
      fails for any reason but a refused secret (server unreachable, TLS refused, ...) signs the client out before
      the error goes to the caller. An unchecked token no longer counts as signed in. A secret the user typed without
      "Remember" is asked for again on the next try, as after a failed password sign-in.
    - Amended 2026-10-31 after review R4/R5 ([../reviews/2026-10-31-launcher-smoke-fixes.md](../reviews/2026-10-31-launcher-smoke-fixes.md)):
      - A sign-in attempt starts by signing the client out, so cancelling the prompt or a failure while reading the
        saved secret or asking also leaves no refused token behind. Remembering the secret after the server accepted
        it stays outside this: a failed save doesn't undo the sign-in.
      - The version kept with a sign-in (`ServerVersion`) is cleared whenever the session ends: sign-out, expiry, a
        renewal answered with 401, or a 401 on any call.
  - **Password**, read from pve-access-control `src/PVE/API2/AccessControl.pm` (`create_ticket`,
    `create_ticket_do`) and `src/PVE/AccessControl.pm` (`authenticate_user`, `authenticate_2nd_new`,
    `verify_ticket`) at commit dff26b4, pve-common `PVE/Ticket.pm` (`assemble_rsa_ticket`), pve-manager
    `www/manager6/window/LoginWindow.js` and proxmox-widget-toolkit `src/window/TfaWindow.js`:

    | Step | Request | Answer |
    |---|---|---|
    | First factor | `POST /access/ticket`, form `username=USER@REALM`, `password` | `username`, `ticket`, `CSRFPreventionToken`; with TFA also `NeedTFA: 1` |
    | TFA? | The ticket starts with `PVE:!tfa!`; the data up to the next `:` is a `TfaChallenge` in JSON, with `%` and `:` percent-escaped | `totp` (bool), `recovery` (indices of unused keys), `webauthn`, `u2f`, `yubico` |
    | Second factor | `POST /access/ticket`, form `username` (from step 1), `tfa-challenge` (the whole half ticket), `password=totp:CODE` or `password=recovery:KEY` (never `otp` together with `tfa-challenge`) | the full ticket and a CSRF token |
    | Renewal | `POST /access/ticket`, `username`, `password=<current ticket>` (a valid ticket of the same user needs no second factor) | a new ticket and CSRF token |
    | Use | `Cookie: PVEAuthCookie=<ticket>`; `CSRFPreventionToken: <token>` on every request that isn't a GET | |

    - A refused code keeps the challenge, so the user can try again (three tries, then the sign-in starts over).
    - The second step's CSRF token is used; if it has none, the first step's (the web UI does the same).
    - A `NeedTFA` without a `PVE:!tfa!` ticket is the pre-7.0 flow. It is refused with a message rather than guessed.
    - WebAuthn, U2F and Yubico OTP are not answered. If the account has only those, and no unused recovery key, the
      message names them. Recovery keys are offered when some are left.
  - **Ticket lifetime and renewal.** Lazy, with no timer:
    - Proxmox's ticket lifetime is 2 hours (`$ticket_lifetime`).
    - Before any request, a ticket older than 15 minutes (the web UI's renewal interval) is first traded for a new one.
    - A ticket older than 1 h 55 min (2 h minus a 5-minute margin) is dropped without sending anything.
    - A renewal answered with 401 signs out. Another failure keeps the still-valid ticket and logs it.
    - A 401 on any call clears the ticket.
    - The window then signs in again once, with the saved password if there is one; TFA asks for a new code.
    - The age is measured on the local monotonic clock from when the ticket arrived, so clock skew doesn't matter.
    - Effect: a session stays alive while the user does something at least every 1 h 40 min. After a longer pause the
      next action signs in again.
  - **Credential Manager** (`CredentialStore`):
    - Each secret is a generic credential (`CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`: this user, this
      machine; it doesn't roam with a domain profile).
    - The target is `SpiceViewer.PVE/<host:port>/token/<ACCOUNT>` or `SpiceViewer.PVE/<host:port>/password/<ACCOUNT>`,
      so tokens and passwords, and servers, never share an entry. The host is lowercased (DNS ignores case).
    - **Account spelling** (amended 2026-10-31 after review L2): Windows compares target names without regard to
      case, but Proxmox user and token names are case-sensitive, so `alice@pve` and `Alice@pve` must not share an
      entry.
      - `<ACCOUNT>` keeps lowercase ASCII letters, digits and `@ ! . - _`; every other UTF-16 unit, uppercase letters
        and `%` included, becomes `%xxxx` (four lowercase hex digits). For example `Alice@pve` becomes
        `%0041lice@pve`, and `alice@pve!viewer` stays as it is.
      - The mapping is one-to-one and its output has no letter whose case could fold onto another
        (`CredentialStore.EncodeAccount`).
      - Moving an entry after an edit refuses targets Windows would treat as the same, so it never deletes what it
        just wrote.
    - The user name field is the account as typed, and the blob is the secret in UTF-16, as Credential Manager's own
      UI writes it (at most 1,280 characters).
    - Calls go through CsWin32 (`CredWriteW`, `CredReadW`, `CredDeleteW`, `CredEnumerateW`, `CredFree`) with
      marshaling off. The secrets in every buffer `CredRead` or `CredEnumerate` returns are cleared before
      `CredFree`.
    - **Calls take turns** (amended 2026-10-31 after a flaky test). Windows doesn't keep overlapping calls from one
      process consistent. A write that overlaps another thread's call can be missing from the next read and
      enumeration and turn up later, and a delete can be undone. So one process-wide lock covers every call, for
      every store. The launcher's calls already run one at a time on its UI thread; parallel test classes were the
      ones that overlapped. The lock doesn't cover other processes.
    - Saving is opt-in per sign-in ("Remember in Windows Credential Manager"), and happens only after the server
      accepted the secret.
    - A saved secret is used without asking. If the server refuses it, the user is asked again and the entry stays
      until a new one is saved or the user forgets it.
    - Editing a server's address or account moves its entry to the new target name; removing a server deletes it.
    - "Forget saved..." deletes this server's entry, or every `SpiceViewer.PVE/*` entry. The entries are also visible
      and removable in Windows' Credential Manager.
  - **In memory only:** the ticket, the CSRF token, the half ticket during TFA and the token header. They go to PveTerm
    only through the hand-off pipe of [0036](0036-v1-terminal-bridge-to-the-users-terminal-first.md). `ToString`,
    exception messages and the log never include them. A test checks the log and `ToString` after a full sign-in,
    TFA, renewal, list, spiceproxy and descriptor.
  - **Server error text is untrusted** (amended 2026-10-31 after review L1). A server or proxy may echo what it
    received in its reason phrase or `errors` fields.
    - Before such text goes into an exception, and from there into the log, each secret that request carried is
      replaced with `[redacted]`:
      - the password or the ticket used as one;
      - the half ticket and the second-factor response, with its code alone;
      - the token header and the token secret alone;
      - the cookie ticket and the CSRF token.
    - Each is matched as sent, URL-escaped and form-encoded, ignoring case.
    - The status code and the rest of the message stay, so errors remain useful.
    - Amended 2026-10-31 after review R1 ([../reviews/2026-10-31-launcher-smoke-fixes.md](../reviews/2026-10-31-launcher-smoke-fixes.md)):
      transport errors are redacted the same way. HttpClient's message can quote a malformed answer, for example a
      header line that echoes the request's `Authorization`. The raw transport exception isn't kept as the inner
      exception: a log of the whole exception chain would print it unredacted. A broken body keeps only the inner
      exception's type name.
  - **The sign-in flow is UI-free** (`PveSignIn` with an `ISignInPrompt`), so other front ends ask through their own
    dialogs.

- **Reason:**
  - Passwords with TOTP cover users without a token. Tokens remain the narrowest grant (`VM.Console` is enough).
  - Lazy renewal meets the 2-hour limit for an active user without waking an idle launcher. A timer that renewed
    every 15 minutes would keep a session alive indefinitely, but it is a wake-up while idle and would be a change to
    the energy rules, so it is left to the human.
  - Credential Manager is the Windows store the spec names. It encrypts with the user's logon (DPAPI), and the user
    can inspect it.
  - Cost:
    - Same-user processes can read these secrets (true of any per-user store on Windows).
    - A long pause means a new TOTP code.
    - WebAuthn and U2F would need a browser-style CTAP stack, which is out of scope here.
