# 0003. TLS without a CA is refused

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** a .vv may carry `tls-port` without `ca`.
- **Decision:** the connector fails with `Untrusted` instead of falling back to the system store. Likewise a CA without `host-subject` is refused (no hostname-check fallback), and the leaf must be valid for TLS server authentication (EKU).
- **Reason:** the draft pins trust to the .vv CA and the exact `host-subject` and forbids system-trust and hostname fallbacks; Proxmox always sends both.
