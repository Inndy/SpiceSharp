# 0013. Outbound agent queue is bounded

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** a guest that requests clipboard data but never grants agent tokens could make us queue without limit (review finding 5).
- **Decision:** clipboard payloads above 32 MiB are sent as empty; while earlier messages are still queued, newer clipboard data replaces older unsent data; if more than 40 MiB would be queued, the agent is stopped.
- **Reason:** bounded memory with no UI blocking.
