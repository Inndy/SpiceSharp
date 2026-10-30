using System.Buffers;
using System.Text.Json;

namespace Pve.Term;

/// <summary>What a termproxy console is opened for: the node's shell, a VM's serial console or a container.</summary>
public enum TermTarget { Node, Qemu, Lxc }

/// <summary>Why a descriptor was rejected; the message never contains a secret.</summary>
public sealed class TermDescriptorException(string message) : Exception(message);

/// <summary>
/// The connection data a launcher hands to PveTerm over the hand-off pipe (docs/decisions/0036): where the API is,
/// which console to open, how to authenticate, and optionally a termproxy session the launcher already requested.
/// It is JSON, read with <see cref="Utf8JsonReader"/> (no reflection). <see cref="ToString"/> shows no secret.
/// </summary>
public sealed class TermDescriptor
{
    public const int MaxBytes = 64 * 1024;
    public const int CurrentVersion = 1;

    /// <summary>The API's scheme, host and port, for example https://pve.example:8006 (always https).</summary>
    public required Uri ApiUrl { get; init; }
    public required string Node { get; init; }
    public required TermTarget Target { get; init; }
    public int? Vmid { get; init; }
    /// <summary>qemu only: serial0..serial3; empty lets Proxmox pick the VM's serial display.</summary>
    public string? Serial { get; init; }

    /// <summary>An API token, USER@REALM!TOKENID=SECRET, sent as Authorization: PVEAPIToken=....</summary>
    public string? ApiToken { get; init; }
    /// <summary>A login ticket (the PVEAuthCookie value), the alternative to <see cref="ApiToken"/>.</summary>
    public string? AuthCookie { get; init; }
    /// <summary>The CSRFPreventionToken that goes with <see cref="AuthCookie"/>; needed only to request termproxy.</summary>
    public string? CsrfToken { get; init; }

    /// <summary>A termproxy session the launcher already opened (user, ticket and port come together or not at all).
    /// termproxy accepts its connection only for about 10 s after the request, so launchers normally leave these out
    /// and let PveTerm request the session itself.</summary>
    public string? TermUser { get; init; }
    public string? TermTicket { get; init; }
    public int? TermPort { get; init; }

    /// <summary>PEM certificates to pin as the only trusted roots (docs/decisions/0033); null uses system trust.</summary>
    public string? CaPem { get; init; }
    public string? Title { get; init; }

    public bool HasSession => TermTicket is not null;

    public string DisplayName => Title ?? Target switch
    {
        TermTarget.Node => $"{Node} shell",
        TermTarget.Qemu => $"VM {Vmid} {(string.IsNullOrEmpty(Serial) ? "serial" : Serial)} on {Node}",
        _ => $"CT {Vmid} on {Node}",
    };

    public override string ToString() => $"{Target} {Node}{(Vmid is int v ? "/" + v : "")} via {ApiUrl.Authority}";

    /// <summary>The API path of the target, below /api2/json: /nodes/{node}[/qemu|/lxc/{vmid}].</summary>
    public string TargetPath => Target switch
    {
        TermTarget.Node => $"/nodes/{Node}",
        TermTarget.Qemu => $"/nodes/{Node}/qemu/{Vmid}",
        _ => $"/nodes/{Node}/lxc/{Vmid}",
    };

    public Uri TermproxyUri => new(ApiUrl, "/api2/json" + TargetPath + "/termproxy");

    /// <summary>wss://host:port/api2/json/.../vncwebsocket?port=P&amp;vncticket=T (pve-xtermjs main.js).</summary>
    public Uri WebSocketUri(int port, string ticket)
    {
        var b = new UriBuilder(ApiUrl) { Scheme = "wss", Path = "/api2/json" + TargetPath + "/vncwebsocket" };
        b.Query = $"port={port}&vncticket={Uri.EscapeDataString(ticket)}";
        return b.Uri;
    }

    public static TermDescriptor Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes) throw new TermDescriptorException("The connection descriptor is too large.");
        int? version = null, vmid = null, port = null;
        string? api = null, node = null, target = null, serial = null, token = null, cookie = null, csrf = null;
        string? user = null, ticket = null, ca = null, title = null;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4 });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new TermDescriptorException("The connection descriptor is not a JSON object.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new TermDescriptorException("Malformed connection descriptor.");
                string name = reader.GetString()!;
                reader.Read();
                switch (name)
                {
                    case "v": version = Int(ref reader, name); break;
                    case "apiUrl": api = Str(ref reader, name); break;
                    case "node": node = Str(ref reader, name); break;
                    case "target": target = Str(ref reader, name); break;
                    case "vmid": vmid = Int(ref reader, name); break;
                    case "serial": serial = Str(ref reader, name); break;
                    case "apiToken": token = Str(ref reader, name); break;
                    case "authCookie": cookie = Str(ref reader, name); break;
                    case "csrfToken": csrf = Str(ref reader, name); break;
                    case "termUser": user = Str(ref reader, name); break;
                    case "termTicket": ticket = Str(ref reader, name); break;
                    case "termPort": port = Int(ref reader, name); break;
                    case "caPem": ca = Str(ref reader, name); break;
                    case "title": title = Str(ref reader, name); break;
                    default: reader.Skip(); break; // newer launchers may add fields
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
                throw new TermDescriptorException("Malformed connection descriptor.");
        }
        catch (JsonException) { throw new TermDescriptorException("The connection descriptor is not valid JSON."); }
        catch (InvalidOperationException) { throw new TermDescriptorException("The connection descriptor has a field of the wrong type."); }

        if (version != CurrentVersion) throw new TermDescriptorException($"Unsupported connection descriptor version ({version?.ToString() ?? "missing"}).");
        if (api is null || !Uri.TryCreate(api, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps
            || apiUri.AbsolutePath != "/" || apiUri.Query.Length > 0 || apiUri.Fragment.Length > 0 || apiUri.UserInfo.Length > 0)
            throw new TermDescriptorException("apiUrl must be an https URL with only a host and port.");
        var t = target switch
        {
            "node" => TermTarget.Node,
            "qemu" => TermTarget.Qemu,
            "lxc" => TermTarget.Lxc,
            _ => throw new TermDescriptorException("target must be node, qemu or lxc."),
        };
        var d = new TermDescriptor
        {
            ApiUrl = apiUri, Node = node ?? "", Target = t, Vmid = vmid, Serial = serial, ApiToken = token,
            AuthCookie = cookie, CsrfToken = csrf, TermUser = user, TermTicket = ticket, TermPort = port, CaPem = ca,
            Title = title,
        };
        d.Validate();
        return d;
    }

    /// <summary>Checks the rules <see cref="Parse"/> enforces; launchers call it before handing a descriptor over.</summary>
    public void Validate()
    {
        if (!IsSafeId(Node)) throw new TermDescriptorException("node is missing or not a valid node name.");
        if (Target == TermTarget.Node)
        {
            if (Vmid is not null || Serial is not null) throw new TermDescriptorException("A node shell takes no vmid or serial.");
        }
        else if (Vmid is not (>= 100 and <= 999_999_999)) throw new TermDescriptorException("vmid is missing or out of range.");
        if (Serial is not null && (Target != TermTarget.Qemu || Serial is not ("serial0" or "serial1" or "serial2" or "serial3")))
            throw new TermDescriptorException("serial must be serial0..serial3, and only for a qemu target.");
        if ((ApiToken is null) == (AuthCookie is null))
            throw new TermDescriptorException("Exactly one of apiToken and authCookie is required.");
        if (ApiToken is not null && (!ApiToken.Contains('!') || !ApiToken.Contains('=') || !IsHeaderSafe(ApiToken)))
            throw new TermDescriptorException("apiToken must look like USER@REALM!TOKENID=SECRET.");
        if (AuthCookie is not null && !IsHeaderSafe(AuthCookie) || CsrfToken is not null && !IsHeaderSafe(CsrfToken))
            throw new TermDescriptorException("authCookie or csrfToken contains characters a header can't carry.");
        bool anySession = TermUser is not null || TermTicket is not null || TermPort is not null;
        if (anySession)
        {
            if (TermUser is null || TermTicket is null || TermPort is null)
                throw new TermDescriptorException("termUser, termTicket and termPort come together.");
            // termproxy splits the first line at the first ':' and ends it at '\n' (termproxy main.rs read_ticket_line).
            if (TermUser.Length == 0 || TermUser.Contains(':') || !IsHeaderSafe(TermUser) || TermTicket.Length is 0 or > 512 || !IsHeaderSafe(TermTicket))
                throw new TermDescriptorException("termUser or termTicket is malformed.");
            // vncwebsocket's schema: 5900..5999 (qemu-server API2/Qemu.pm, pve-manager API2/Nodes.pm).
            if (TermPort is not (>= 5900 and <= 5999)) throw new TermDescriptorException("termPort must be 5900..5999.");
        }
        if (Title is not null && (Title.Length > 256 || Title.Any(char.IsControl)))
            throw new TermDescriptorException("title is too long or has control characters.");
    }

    /// <summary>Proxmox's safe ID format (pve-xtermjs main.js safeIdRegex: ^[A-Za-z0-9_][A-Za-z0-9._-]*$).</summary>
    static bool IsSafeId(string s)
    {
        if (s.Length is 0 or > 64 || s[0] == '.' || s[0] == '-') return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return false;
        return true;
    }

    static bool IsHeaderSafe(string s)
    {
        foreach (char c in s)
            if (c is < (char)0x21 or > (char)0x7E) return false;
        return true;
    }

    /// <summary>A string, or null for JSON null (an absent optional field).</summary>
    static string? Str(ref Utf8JsonReader r, string name) => r.TokenType switch
    {
        JsonTokenType.String => r.GetString(),
        JsonTokenType.Null => null,
        _ => throw new TermDescriptorException($"{name} must be a string."),
    };

    static int? Int(ref Utf8JsonReader r, string name) => r.TokenType switch
    {
        JsonTokenType.Number when r.TryGetInt32(out int v) => v,
        JsonTokenType.Null => null,
        _ => throw new TermDescriptorException($"{name} must be an integer."),
    };

    /// <summary>Serializes for the hand-off (launchers and tests).</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", CurrentVersion);
            w.WriteString("apiUrl", ApiUrl.GetLeftPart(UriPartial.Authority));
            w.WriteString("node", Node);
            w.WriteString("target", Target switch { TermTarget.Node => "node", TermTarget.Qemu => "qemu", _ => "lxc" });
            if (Vmid is int vmid) w.WriteNumber("vmid", vmid);
            if (Serial is not null) w.WriteString("serial", Serial);
            if (ApiToken is not null) w.WriteString("apiToken", ApiToken);
            if (AuthCookie is not null) w.WriteString("authCookie", AuthCookie);
            if (CsrfToken is not null) w.WriteString("csrfToken", CsrfToken);
            if (TermUser is not null) w.WriteString("termUser", TermUser);
            if (TermTicket is not null) w.WriteString("termTicket", TermTicket);
            if (TermPort is int port) w.WriteNumber("termPort", port);
            if (CaPem is not null) w.WriteString("caPem", CaPem);
            if (Title is not null) w.WriteString("title", Title);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
