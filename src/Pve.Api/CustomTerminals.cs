using System.Text;

namespace Pve.Api;

/// <summary>A mintty that can be started: the full path of <c>mintty.exe</c> and where it came from.</summary>
public sealed record MinttyInstall(string Path, string DisplayName);

/// <summary>Where mintty was found: <see cref="Found"/> in order of preference (the configured one first, when it
/// exists), and <see cref="Problem"/> when a configured <c>minttyPath</c> was not used and detection took over.</summary>
public sealed record MinttyLookup(IReadOnlyList<MinttyInstall> Found, string? Problem);

/// <summary>The lookups a mintty scan makes, injectable for tests.</summary>
public sealed class MinttyProbe
{
    public required Func<string, bool> FileExists { get; init; }
    public required Func<string, string?> GetEnv { get; init; }

    public static MinttyProbe System { get; } = new() { FileExists = File.Exists, GetEnv = Environment.GetEnvironmentVariable };
}

/// <summary>
/// Finds mintty (docs/decisions/0042, amended): file checks only, at each launch and when the terminal list opens,
/// never on a timer. In order: <c>mintty.exe</c> in a fully qualified PATH folder (the user's own choice), MSYS2 (scoop,
/// per user then global; then <c>%SystemDrive%\msys64</c>), then Git for Windows (scoop; <c>%ProgramFiles%\Git</c>;
/// a per-user install in <c>%LOCALAPPDATA%\Programs\Git</c>). Each has it in <c>usr\bin</c>.
/// </summary>
public static class MinttyLocator
{
    public const string ExeName = "mintty.exe";

    /// <summary>The display name of the mintty that <c>minttyPath</c> in launcher.json names.</summary>
    public const string ConfiguredName = "minttyPath";

    public static IReadOnlyList<MinttyInstall> Scan() => Scan(MinttyProbe.System);

    /// <summary>
    /// The mintty to use with the user's <c>minttyPath</c> (docs/decisions/0042, the human's answer
    /// D-9ef18eff723c): empty or absent detects as <see cref="Scan()"/> does; a path that names an existing file (or a
    /// folder that holds <c>mintty.exe</c>) comes first, before what detection finds; any other path is reported in
    /// <see cref="MinttyLookup.Problem"/> and detection takes over. File checks only.
    /// </summary>
    public static MinttyLookup Find(string? configuredPath) => Find(configuredPath, MinttyProbe.System);

    public static MinttyLookup Find(string? configuredPath, MinttyProbe probe)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return new(Scan(probe), null);
        if (CheckPath(configuredPath, probe, out var configured) is { } problem) return new(Scan(probe), problem + "; looking for mintty as usual");
        var found = new List<MinttyInstall> { configured! };
        found.AddRange(Scan(probe).Where(m => !string.Equals(m.Path, configured!.Path, StringComparison.OrdinalIgnoreCase)));
        return new(found, null);
    }

    /// <summary>
    /// Checks a <c>minttyPath</c>: surrounding quotes and spaces are dropped and <c>%VARIABLE%</c>s expanded; it must
    /// then be a full path to an existing file, or to a folder with <c>mintty.exe</c> in it. Returns why not, or null
    /// with the install in <paramref name="install"/>. Any file name is accepted (a renamed or wrapped mintty).
    /// </summary>
    public static string? CheckPath(string configuredPath, MinttyProbe probe, out MinttyInstall? install)
    {
        install = null;
        string written = configuredPath.Trim().Trim('"').Trim();
        string path = ExpandVariables(written, probe);
        if (path.Length == 0) return $"{ConfiguredName} is empty";
        if (!Path.IsPathFullyQualified(path)) return $"{ConfiguredName} \"{written}\" is not a full path";
        try { path = Path.GetFullPath(path); }
        catch (ArgumentException) { return $"{ConfiguredName} \"{written}\" is not a valid path"; }
        if (probe.FileExists(path)) install = new MinttyInstall(path, ConfiguredName);
        else if (probe.FileExists(Path.Combine(path, ExeName))) install = new MinttyInstall(Path.Combine(path, ExeName), ConfiguredName);
        else return $"{ConfiguredName} \"{written}\" was not found";
        return null;
    }

    /// <summary><c>%NAME%</c> becomes the variable's value; an unknown one, and <c>%%</c>, stay as written.</summary>
    internal static string ExpandVariables(string text, MinttyProbe probe)
    {
        if (!text.Contains('%')) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('%', i);
            int end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (end < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            sb.Append(text, i, start - i);
            string name = text[(start + 1)..end];
            if (name.Length > 0 && probe.GetEnv(name) is { } value)
            {
                sb.Append(value);
                i = end + 1;
            }
            else
            {
                sb.Append(text, start, end - start + 1);
                i = end + 1;
            }
        }
        return sb.ToString();
    }

    public static IReadOnlyList<MinttyInstall> Scan(MinttyProbe probe)
    {
        var found = new List<MinttyInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Check(string? dir, string name, params string[] parts)
        {
            if (dir?.Trim().Trim('"') is not { Length: > 0 } d || !Path.IsPathFullyQualified(d)) return;
            string path;
            try { path = Path.GetFullPath(Path.Combine([d, .. parts, ExeName])); }
            catch (ArgumentException) { return; }
            if (seen.Add(path) && probe.FileExists(path)) found.Add(new MinttyInstall(path, name));
        }

        foreach (string entry in (probe.GetEnv("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Check(entry, "mintty on PATH");
        string? profile = probe.GetEnv("USERPROFILE");
        string? scoop = probe.GetEnv("SCOOP") is { Length: > 0 } s ? s : profile is { Length: > 0 } ? Path.Combine(profile, "scoop") : null;
        string? programData = probe.GetEnv("ProgramData");
        string? scoopGlobal = probe.GetEnv("SCOOP_GLOBAL") is { Length: > 0 } g ? g : programData is { Length: > 0 } ? Path.Combine(programData, "scoop") : null;
        Check(scoop, "MSYS2 (scoop)", "apps", "msys2", "current", "usr", "bin");
        Check(scoopGlobal, "MSYS2 (scoop, global)", "apps", "msys2", "current", "usr", "bin");
        if (probe.GetEnv("SystemDrive") is { Length: > 0 } drive) Check(drive + @"\", "MSYS2", "msys64", "usr", "bin");
        Check(scoop, "Git for Windows (scoop)", "apps", "git", "current", "usr", "bin");
        Check(scoopGlobal, "Git for Windows (scoop, global)", "apps", "git", "current", "usr", "bin");
        Check(probe.GetEnv("ProgramFiles"), "Git for Windows", "Git", "usr", "bin");
        Check(probe.GetEnv("LOCALAPPDATA"), "Git for Windows (per user)", "Programs", "Git", "usr", "bin");
        return found;
    }

    /// <summary>
    /// The child's <c>MSYS</c> variable: the user's own options with <c>enable_pcon</c> added last, so mintty gives a
    /// native program such as PveTerm a pseudo console (ConPTY) instead of pipes. The MSYS runtime reads the options in
    /// order, so a <c>disable_pcon</c> the user set is overridden for this window only.
    /// </summary>
    public static string MsysWithPcon(string? existing) =>
        existing?.Trim() is { Length: > 0 } e ? e + " enable_pcon" : "enable_pcon";
}

/// <summary>
/// A custom terminal command (docs/decisions/0042, amended): a program and its arguments with <c>{exe}</c> (PveTerm),
/// <c>{pipe}</c>, <c>{nonce}</c> and <c>{title}</c>, the same placeholders as <c>tools/pve-vv.py --term ... --
/// COMMAND</c> plus the title. No shell is involved: the arguments go to the program as they are.
/// </summary>
public static class TerminalCommand
{
    static readonly string[] Names = ["exe", "pipe", "nonce", "title"];

    /// <summary>
    /// Splits a command line written as one string the way the Microsoft C runtime splits its own (and
    /// <c>CommandLineToArgvW</c>, apart from its special first argument): white space separates arguments, double
    /// quotes group, <c>""</c> inside quotes is a literal quote, and backslashes are literal except before a quote:
    /// 2n backslashes and a quote are n backslashes and a quote mark that groups, 2n+1 are n backslashes and a
    /// literal quote.
    /// </summary>
    public static List<string> Split(string line)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inArg = false, quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < line.Length && line[i] == '\\') { n++; i++; }
                if (i < line.Length && line[i] == '"')
                {
                    cur.Append('\\', n / 2);
                    if (n % 2 == 1) cur.Append('"');
                    else quoted = !quoted;
                }
                else
                {
                    cur.Append('\\', n);
                    i--;
                }
                inArg = true;
            }
            else if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else quoted = !quoted;
                inArg = true;
            }
            else if (!quoted && c is ' ' or '\t' or '\r' or '\n')
            {
                if (inArg) args.Add(cur.ToString());
                cur.Clear();
                inArg = false;
            }
            else
            {
                cur.Append(c);
                inArg = true;
            }
        }
        if (inArg) args.Add(cur.ToString());
        return args;
    }

    /// <summary>Fills the placeholders in one pass, so text a value brings in (a VM named "{nonce}") is never read as
    /// a placeholder. Unknown <c>{...}</c> stay as written.</summary>
    public static string Expand(string template, string exe, string pipe, string nonce, string title)
    {
        var sb = new StringBuilder(template.Length + 64);
        int i = 0;
        while (i < template.Length)
        {
            int open = template.IndexOf('{', i);
            if (open < 0) break;
            sb.Append(template, i, open - i);
            string? value = null;
            int close = template.IndexOf('}', open + 1);
            if (close > open)
            {
                value = template[(open + 1)..close] switch
                {
                    "exe" => exe,
                    "pipe" => pipe,
                    "nonce" => nonce,
                    "title" => title,
                    _ => null,
                };
            }
            if (value is null)
            {
                sb.Append('{');
                i = open + 1;
                continue;
            }
            sb.Append(value);
            i = close + 1;
        }
        sb.Append(template, i, template.Length - i);
        return sb.ToString();
    }

    /// <summary>Refuses a command that can't work, before any ticket is spent: empty, or without <c>{pipe}</c> and
    /// <c>{nonce}</c>, which PveTerm needs to collect its descriptor.</summary>
    public static void Check(IReadOnlyList<string>? command)
    {
        if (command is not { Count: > 0 } || command[0].Trim().Length == 0)
            throw new PveApiException("The custom terminal command is empty. Set terminalCommand in launcher.json, or choose another terminal.");
        foreach (string name in new[] { "pipe", "nonce" })
            if (!command.Any(a => a.Contains("{" + name + "}", StringComparison.Ordinal)))
                throw new PveApiException($"The custom terminal command has no {{{name}}}; PveTerm needs {{pipe}} and {{nonce}} (and usually {{exe}} console --pipe {{pipe}} --nonce {{nonce}}).");
    }

    /// <summary>The placeholders, for messages and the README.</summary>
    public static string Placeholders => string.Join(", ", Names.Select(n => "{" + n + "}"));
}
