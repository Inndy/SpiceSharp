# 0006. Test fixtures are recorded by our own client

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** replay fixtures need the server-to-client byte stream.
- **Decision:** `tools/spicerec` taps the plaintext stream after TLS inside our connector, instead of a MITM proxy.
- **Reason:** no extra certificates or proxy setup; the recording is exactly what the decoder sees.
