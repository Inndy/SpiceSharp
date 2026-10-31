namespace Pve.Api;

/// <summary>What a VM's display offers (docs/decisions/0043).</summary>
public enum GuestDisplay
{
    /// <summary>A SPICE display: <c>qxl</c>..<c>qxl4</c> or <c>virtio*</c>. Its VNC console works too.</summary>
    Spice,
    /// <summary>Only Proxmox's VNC console: <c>std</c> (the default), <c>cirrus</c>, <c>vmware</c>.</summary>
    Vnc,
    /// <summary><c>serialN</c>: the display is a serial terminal.</summary>
    Serial,
    /// <summary><c>none</c>: no display at all.</summary>
    None,
}

public static class GuestDisplays
{
    /// <summary>
    /// qemu-server's rule for a VM config's <c>vga</c> property (<c>[type=]TYPE[,clipboard=..][,memory=..]</c>):
    /// SPICE for <c>qxl</c>, <c>qxl2</c>..<c>qxl4</c> and anything starting with <c>virtio</c>
    /// (<c>vga_conf_has_spice</c> and the status API's <c>spice</c> flag); a serial terminal for <c>serialN</c>; no
    /// display for <c>none</c>; otherwise VNC, including no <c>vga</c> at all (<c>std</c> on x86).
    /// </summary>
    public static GuestDisplay Of(string? vga)
    {
        string type = "";
        foreach (var part in (vga ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) { type = part; break; }
            if (part[..eq] == "type") { type = part[(eq + 1)..]; break; }
        }
        if (type is "qxl" or "qxl2" or "qxl3" or "qxl4" || type.StartsWith("virtio", StringComparison.Ordinal)) return GuestDisplay.Spice;
        if (type is "serial0" or "serial1" or "serial2" or "serial3") return GuestDisplay.Serial;
        return type == "none" ? GuestDisplay.None : GuestDisplay.Vnc;
    }
}
