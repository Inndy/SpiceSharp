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
