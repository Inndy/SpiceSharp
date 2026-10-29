# 0012. Channel sends are queued, never blocking

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** input is sent from the UI thread; a server that stops reading would block a synchronous TLS write and with it the release hotkey and window close (viewer review finding 3).
- **Decision:** `SpiceChannel.Send` appends the frame to a double-buffered queue and returns; one pool thread at a time writes the queue in order. More than 8 MiB unsent, or any write failure, ends the channel (no further sends, stream closed); losing main, display or inputs ends the session.
- **Reason:** the UI thread never waits on the network, without per-message allocations (two reusable buffers, no queue nodes). Dropping single frames would silently break the stream (a lost key-up sticks a key), so a stalled or failed channel is terminal instead (verification finding N1/N2).
