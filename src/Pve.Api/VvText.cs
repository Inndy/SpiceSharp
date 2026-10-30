using System.Text;
using System.Text.Json;
using VvFile;

namespace Pve.Api;

/// <summary>Turns a <c>spiceproxy</c> answer into .vv text for <c>SpiceViewer.exe --vv-stdin</c>.</summary>
public static class VvText
{
    /// <summary>
    /// Writes <c>[virt-viewer]</c> and one <c>key=value</c> line per field, the way <c>tools/pve-vv.py</c> writes a
    /// file. Proxmox already escapes the CA's newlines as <c>\n</c> (pve-access-control AccessControl.pm
    /// remote_viewer_config); any raw line break left in a value is escaped the same way, so no value can add a line.
    /// Keys outside [A-Za-z0-9-] and non-scalar values are dropped. The result is checked with
    /// <see cref="VvConnection.Parse"/>, so a VM without a usable SPICE answer fails here, not in the viewer.
    /// </summary>
    public static string FromSpiceproxy(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new PveApiException("The server's SPICE answer is empty; does the VM have a SPICE display?");
        var sb = new StringBuilder(4096);
        sb.Append("[virt-viewer]\n");
        foreach (var p in data.EnumerateObject())
        {
            if (p.Name.Length is 0 or > 64 || !p.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) continue;
            string? value = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null,
            };
            if (value is null) continue;
            sb.Append(p.Name).Append('=');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case < ' ': break; // other control characters have no place in a .vv value
                    default: sb.Append(c); break;
                }
            }
            sb.Append('\n');
        }
        string text = sb.ToString();
        try { _ = VvConnection.Parse(text); }
        catch (VvFormatException e) { throw new PveApiException("The server's SPICE answer can't be used: " + e.Message); }
        return text;
    }
}
