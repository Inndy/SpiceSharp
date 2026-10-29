# 0004. LZ4 is not advertised

- Status: Superseded by [0028](0028-fast-jpeg-and-preferred-image-compression-are-settings-lz4-is-advertised.md)
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Decision:** the display channel does not advertise `SPICE_DISPLAY_CAP_LZ4_COMPRESSION`.
- **Reason:** Proxmox/QEMU builds rarely enable it, and it would add a managed dependency for no measured gain.
