using System.Globalization;
using System.Text.Json;

namespace Pve.Api;

public enum PveGuestType { Qemu, Lxc }

/// <summary>One guest from <c>GET /cluster/resources?type=vm</c> (pve-manager API2/Cluster.pm resources).</summary>
public sealed record PveVm(int Vmid, string Name, string Node, PveGuestType Type, string Status, bool IsTemplate)
{
    public bool IsRunning => Status == "running";
    public string TypeText => Type == PveGuestType.Lxc ? "lxc" : "qemu";

    /// <summary>
    /// Reads the <c>data</c> array. Perl's JSON may write numbers as strings, so VMIDs and the template flag are read
    /// either way. Entries that are not qemu or lxc, or lack a VMID or node, are skipped. Sorted by VMID.
    /// </summary>
    public static IReadOnlyList<PveVm> ParseResources(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) throw new PveApiException("The server's guest list is not a list.");
        var list = new List<PveVm>(data.GetArrayLength());
        foreach (var e in data.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var type = Text(e, "type") switch { "qemu" => PveGuestType.Qemu, "lxc" => PveGuestType.Lxc, _ => (PveGuestType?)null };
            if (type is null || Int(e, "vmid") is not int vmid || vmid is < 100 or > 999_999_999) continue;
            string? node = Text(e, "node");
            if (node is null || !IsSafeId(node)) continue;
            string name = Text(e, "name") ?? "";
            if (name.Length > 128) name = name[..128];
            list.Add(new PveVm(vmid, name, node, type.Value, Text(e, "status") ?? "unknown", Int(e, "template") is int t && t != 0));
        }
        list.Sort((a, b) => a.Vmid.CompareTo(b.Vmid));
        return list;
    }

    static string? Text(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    static int? Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out int i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int i) => i,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    /// <summary>Proxmox's safe ID (node names): ^[A-Za-z0-9_][A-Za-z0-9._-]*$, as <see cref="Pve.Term.TermDescriptor"/> checks it.</summary>
    public static bool IsSafeId(string s)
    {
        if (s.Length is 0 or > 64 || s[0] == '.' || s[0] == '-') return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return false;
        return true;
    }
}
