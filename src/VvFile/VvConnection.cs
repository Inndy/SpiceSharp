using System.Text;

namespace VvFile;

/// <summary>Thrown for any .vv file that cannot be used. The message is safe to show and never contains the password.</summary>
public sealed class VvFormatException(string message) : Exception(message);

/// <summary>The <c>proxy=</c> endpoint: Proxmox tunnels every channel through pveproxy's HTTP CONNECT proxy.</summary>
public sealed record HttpConnectProxy(string Host, int Port)
{
    /// <summary>Accepts <c>http://host[:port][/]</c> and scheme-less <c>host[:port]</c>; the port defaults to 3128.</summary>
    public static HttpConnectProxy Parse(string value)
    {
        var rest = value.Trim();
        int scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            if (!rest[..scheme].Equals("http", StringComparison.OrdinalIgnoreCase))
                throw new VvFormatException($"Unsupported proxy '{value}': only http:// is supported.");
            rest = rest[(scheme + 3)..];
        }
        rest = rest.TrimEnd('/');
        int port = 3128;
        // IPv6 literal: [::1]:3128
        int colon = rest.LastIndexOf(':');
        if (colon >= 0 && (!rest.StartsWith('[') || rest.LastIndexOf(']') < colon))
        {
            if (!int.TryParse(rest.AsSpan(colon + 1), out port) || port is <= 0 or > 65535)
                throw new VvFormatException($"Bad proxy port in '{value}'.");
            rest = rest[..colon];
        }
        if (rest.StartsWith('[') && rest.EndsWith(']')) rest = rest[1..^1];
        if (rest.Length == 0) throw new VvFormatException($"No proxy host in '{value}'.");
        return new HttpConnectProxy(rest, port);
    }
}

[Flags]
public enum HotkeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>A virt-viewer hotkey such as <c>Shift+F11</c>. <see cref="VirtualKey"/> is 0 for a modifier-only combo (allowed for release-cursor).</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, ushort VirtualKey)
{
    public static readonly Hotkey DefaultToggleFullscreen = new(HotkeyModifiers.Shift, 0x7A);           // Shift+F11
    public static readonly Hotkey DefaultReleaseCursor = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, (ushort)'R');
    public static readonly Hotkey DefaultSecureAttention = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x2D); // Ins

    /// <summary>Parses virt-viewer's spice hotkey syntax: '+'-separated, case-insensitive names.</summary>
    public static bool TryParse(string text, out Hotkey hotkey)
    {
        hotkey = default;
        var mods = HotkeyModifiers.None;
        ushort vk = 0;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctrl_l" or "ctrl_r" or "control_l" or "control_r": mods |= HotkeyModifiers.Ctrl; continue;
                case "alt" or "alt_l" or "alt_r" or "meta": mods |= HotkeyModifiers.Alt; continue;
                case "shift" or "shift_l" or "shift_r": mods |= HotkeyModifiers.Shift; continue;
                case "super" or "super_l" or "super_r" or "win": mods |= HotkeyModifiers.Win; continue;
            }
            if (vk != 0) return false;
            vk = KeyNameToVirtualKey(part);
            if (vk == 0) return false;
        }
        if (vk == 0 && mods == HotkeyModifiers.None) return false;
        hotkey = new Hotkey(mods, vk);
        return true;
    }

    static ushort KeyNameToVirtualKey(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        var n = name.ToLowerInvariant();
        if (n.Length is 2 or 3 && n[0] == 'f' && int.TryParse(n.AsSpan(1), out int f) && f is >= 1 and <= 24)
            return (ushort)(0x70 + f - 1);
        return n switch
        {
            "ins" or "insert" => 0x2D,
            "del" or "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pgup" or "pageup" or "page_up" or "prior" => 0x21,
            "pgdn" or "pagedown" or "page_down" or "next" => 0x22,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "tab" => 0x09,
            "return" or "enter" => 0x0D,
            "backspace" => 0x08,
            "pause" or "break" => 0x13,
            "print" or "sysrq" => 0x2C,
            "scroll_lock" or "scrolllock" => 0x91,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ => 0,
        };
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) sb.Append("Win+");
        if (VirtualKey == 0) { if (sb.Length > 0) sb.Length--; return sb.ToString(); }
        sb.Append(VirtualKey switch
        {
            >= 0x70 and <= 0x87 => "F" + (VirtualKey - 0x6F),
            0x2D => "Ins", 0x2E => "Del", 0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
            0x1B => "Esc", 0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x08 => "Backspace",
            _ => ((char)VirtualKey).ToString(),
        });
        return sb.ToString();
    }
}

/// <summary>
/// A parsed virt-viewer connection file. The file is untrusted input: every field is validated and
/// lengths are bounded. <see cref="ToString"/> never includes the password.
/// </summary>
public sealed class VvConnection
{
    public const int MaxFileBytes = 256 << 10;
    const int MaxValueChars = 64 << 10;

    public required string Host { get; init; }
    /// <summary>Plain-text port; null when absent or 0.</summary>
    public int? Port { get; init; }
    public int? TlsPort { get; init; }
    public string? Password { get; init; }
    public string? CaPem { get; init; }
    public string? HostSubject { get; init; }
    public string? Title { get; init; }
    public HttpConnectProxy? Proxy { get; init; }
    public bool DeleteThisFile { get; init; }
    public bool Fullscreen { get; init; }
    public Hotkey ToggleFullscreen { get; init; } = Hotkey.DefaultToggleFullscreen;
    public Hotkey ReleaseCursor { get; init; } = Hotkey.DefaultReleaseCursor;
    public Hotkey SecureAttention { get; init; } = Hotkey.DefaultSecureAttention;
    /// <summary>Keys this parser does not use, for debug logging (values omitted).</summary>
    public IReadOnlyList<string> IgnoredKeys { get; init; } = [];

    public bool UseTls => TlsPort is not null;
    public int TargetPort => TlsPort ?? Port ?? 0;
    public string DisplayTitle => string.IsNullOrEmpty(Title) ? Host : Title;

    public override string ToString() =>
        $"spice://{Host}:{TargetPort} tls={UseTls} proxy={(Proxy is null ? "-" : $"{Proxy.Host}:{Proxy.Port}")} title={Title}";

    /// <summary>
    /// Reads the file in one bounded read and deletes it afterwards if it says <c>delete-this-file=1</c>.
    /// The file is deleted even when parsing fails, since the ticket in it is then useless anyway.
    /// </summary>
    public static VvConnection Load(string path)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
        {
            data = new byte[MaxFileBytes + 1];
            int total = 0, n;
            while (total < data.Length && (n = fs.Read(data, total, data.Length - total)) > 0) total += n;
            if (total > MaxFileBytes) throw new VvFormatException("The connection file is larger than 256 KB.");
            Array.Resize(ref data, total);
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { throw new VvFormatException("The connection file is not UTF-8 text."); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var fields = ReadSection(text);
        if (fields.TryGetValue("delete-this-file", out var del) && del.Trim() == "1")
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return FromFields(fields);
    }

    public static VvConnection Parse(string text) => FromFields(ReadSection(text));

    /// <summary>GKeyFile-style: the [virt-viewer] group, "key = value" lines, '#' comments, later duplicates win.</summary>
    static Dictionary<string, string> ReadSection(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inSection = false, sawSection = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                inSection = line.TrimEnd() == "[virt-viewer]";
                sawSection |= inSection;
                continue;
            }
            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].TrimStart();
            if (value.Length > MaxValueChars) throw new VvFormatException($"The value of '{key}' is too long.");
            fields[key] = Unescape(value);
        }
        if (!sawSection) throw new VvFormatException("This is not a virt-viewer connection file (no [virt-viewer] section).");
        return fields;
    }

    /// <summary>GKeyFile string escapes: \s \n \t \r \\. Proxmox writes the CA with literal "\n".</summary>
    static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 == value.Length) { sb.Append(c); continue; }
            char e = value[++i];
            switch (e)
            {
                case 's': sb.Append(' '); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(e); break;
            }
        }
        return sb.ToString();
    }

    static readonly HashSet<string> KnownKeys =
    [
        "type", "host", "port", "tls-port", "password", "proxy", "ca", "host-subject", "title",
        "delete-this-file", "fullscreen", "toggle-fullscreen", "release-cursor", "secure-attention",
    ];

    static VvConnection FromFields(Dictionary<string, string> f)
    {
        string? Get(string key) => f.TryGetValue(key, out var v) && v.Trim().Length > 0 ? v.Trim() : null;

        var type = Get("type");
        if (type is null || !type.Equals("spice", StringComparison.OrdinalIgnoreCase))
            throw new VvFormatException(type is null
                ? "The connection file does not say which protocol to use (no type=spice)."
                : $"This viewer only supports SPICE connections, but the file asks for '{type}'.");

        var host = Get("host") ?? throw new VvFormatException("The connection file has no host.");

        int? ParsePort(string key)
        {
            var raw = Get(key);
            if (raw is null) return null;
            if (!int.TryParse(raw, out int p) || p is < 0 or > 65535)
                throw new VvFormatException($"The connection file has an invalid {key}: {raw}.");
            return p == 0 ? null : p;
        }
        int? port = ParsePort("port"), tlsPort = ParsePort("tls-port");
        if (port is null && tlsPort is null) throw new VvFormatException("The connection file has no port.");

        Hotkey Key(string name, Hotkey fallback)
        {
            var raw = Get(name);
            return raw is not null && Hotkey.TryParse(raw, out var hk) ? hk : fallback;
        }

        var password = f.TryGetValue("password", out var pw) && pw.Length > 0 ? pw : null;
        return new VvConnection
        {
            Host = host,
            Port = port,
            TlsPort = tlsPort,
            Password = password,
            CaPem = Get("ca"),
            HostSubject = Get("host-subject"),
            Title = Get("title"),
            Proxy = Get("proxy") is { } proxy ? HttpConnectProxy.Parse(proxy) : null,
            DeleteThisFile = Get("delete-this-file") == "1",
            Fullscreen = Get("fullscreen") == "1",
            ToggleFullscreen = Key("toggle-fullscreen", Hotkey.DefaultToggleFullscreen),
            ReleaseCursor = Key("release-cursor", Hotkey.DefaultReleaseCursor),
            SecureAttention = Key("secure-attention", Hotkey.DefaultSecureAttention),
            IgnoredKeys = f.Keys.Where(k => !KnownKeys.Contains(k)).ToArray(),
        };
    }
}
