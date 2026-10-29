# 0002. Codecs stay in C for v0

- Status: Accepted
- Date: 2026-10-29
- Decided by: the human (deferred the C# codec port to the next phase)

- **Context:** porting QUIC/LZ/GLZ to C# was discussed.
- **Decision:** v0 uses the vendored C through spicecodec.dll. A clean-room C# port is a post-v0 option.
- **Reason:** the human agreed to defer it; the C code is the reference behavior and is already fuzzed upstream.
