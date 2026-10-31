# 0033. The PVE API client pins a given CA, otherwise uses the system trust roots

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human (2026-10-30: "if CA is given then treat it as pinned CA. otherwise accept system trust root")

- **Context:**
  - The v1 PVE client talks to the Proxmox API over HTTPS. A `.vv` file carries its own CA; the API endpoint
    does not.
  - The spec's TLS rule (trust only the `.vv` CA, never the system store) covers the SPICE connection.
- **Decision:**
  - When the user gives a CA for a PVE server, the API client trusts only that CA, the same way the viewer pins
    the `.vv` CA (`CustomRootTrust`).
  - Without one, the API client uses the Windows system trust roots with normal hostname validation.
  - The SPICE connection's rule is unchanged: the viewer still trusts only the `.vv` CA.
- **Reason:**
  - A pinned CA is the strongest trust when the user has one, for example Proxmox's self-signed cluster CA.
  - System trust covers servers behind a public certificate (ACME) without extra setup.

## Amendment (the human, 2026-10-31, through agentdesk): a pinned leaf certificate skips the host check

- Decided by: the human (2026-10-31): "Pin CA -> verify host / Pin terminal cert -> ignore host validation".
- A pinned CA keeps the host check: the chain must end at the pinned CA and the certificate must name the host.
- A pinned end-entity (leaf) certificate is matched exactly (the presented leaf must be that certificate), and then
  the host name is not checked, since only the holder of that key can present it. This covers DNS aliases, reverse
  proxies and addresses missing from the certificate.
- A pinned PEM is a CA when its basic constraints say CA=true, otherwise it is treated as a leaf.
- Implemented 2026-10-31 (agent; details spec silent, not yet reviewed):
  - `Pve.Term.PveTls`, used by the launcher's API client and by PveTerm (termproxy and VNC), splits the pinned PEM:
    each certificate whose basic constraints say CA=true is a root, every other one is a pinned leaf.
  - The presented certificate is compared byte for byte with each pinned leaf first. A match is accepted without the
    host name or chain checks, but only within the pinned certificate's validity dates: the pinned-CA path already
    refused a certificate outside them (the chain build checks time), so the leaf path keeps that rule. A PEM with
    both kinds keeps both rules; a presented certificate that matches no leaf falls through to the CA rule, and with
    no CA in the PEM it is refused ("not the pinned certificate").
  - `tools/pve-vv.py` (`PVE_CA_FILE`) does the same for its own requests: a file with a CA uses only its CAs (hostname
    checked); a file without one pins its certificates exactly, checked right after the handshake inside
    `wrap_socket`, before any request goes out. A file holding both kinds uses only the CAs there (a dev helper; the
    descriptor it hands PveTerm still carries the whole PEM).
  - The launcher's field is "CA or server certificate to pin"; the README says a pinned server certificate must be
    pasted again when it renews.
  - Amended after review R2 ([../reviews/2026-10-31-launcher-answers.md](../reviews/2026-10-31-launcher-answers.md)):
    `tools/pve-vv.py` reads UTCTime with X.509's century rule and checks a pinned leaf's dates at every handshake.
