using Spice.Session;
using Spice.Wire;

namespace SpiceRec;

sealed class InputScript
{
    readonly record struct Command(string Name, string Text, int[] Values);
    readonly List<Command> commands = [];
    static readonly Dictionary<string, ushort> keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["win"] = 0xE05B, ["ctrl"] = 0x1D, ["alt"] = 0x38, ["shift"] = 0x2A,
        ["esc"] = 1, ["enter"] = 0x1C, ["tab"] = 0xF, ["space"] = 0x39,
        ["backspace"] = 0xE, ["delete"] = 0xE053, ["left"] = 0xE04B, ["right"] = 0xE04D,
        ["up"] = 0xE048, ["down"] = 0xE050, ["home"] = 0xE047, ["end"] = 0xE04F,
    };
    static InputScript()
    {
        for (int i = 1; i <= 10; i++) keys["f" + i] = (ushort)(0x3A + i);
        keys["f11"] = 0x57; keys["f12"] = 0x58;
    }
    static (ushort Key, bool Shift) Character(char c)
    {
        const string letters = "qwertyuiopasdfghjklzxcvbnm";
        ReadOnlySpan<byte> scans = [16,17,18,19,20,21,22,23,24,25,30,31,32,33,34,35,36,37,38,44,45,46,47,48,49,50];
        int i = letters.IndexOf(char.ToLowerInvariant(c));
        if (i >= 0) return (scans[i], char.IsUpper(c));
        const string plain = "1234567890-=[];'`,./\\ ";
        const string shifted = "!@#$%^&*()_+{}:\"~<>?| ";
        ReadOnlySpan<byte> codes = [2,3,4,5,6,7,8,9,10,11,12,13,26,27,39,40,41,51,52,53,43,57];
        i = plain.IndexOf(c); if (i >= 0) return (codes[i], false);
        i = shifted.IndexOf(c); if (i >= 0) return (codes[i], true);
        throw new FormatException($"Unsupported US-layout character U+{(int)c:X4}.");
    }
    static ushort Key(string name) => keys.TryGetValue(name, out ushort scan) ? scan : name.Length == 1 ? Character(name[0]).Key : throw new FormatException($"Unknown key: {name}");
    static MouseButton Button(string name) => name switch { "left" => MouseButton.Left, "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => throw new FormatException("Unknown mouse button.") };
    public static InputScript Load(string path)
    {
        var script = new InputScript();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            int split = line.IndexOf(' '); string name = split < 0 ? line : line[..split]; string text = split < 0 ? "" : line[(split + 1)..].TrimStart();
            int[] values = [];
            switch (name)
            {
                case "wait": case "move": case "drag":
                    values = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    if (values.Length != (name == "wait" ? 1 : name == "move" ? 2 : 4) || values.Any(x => x < 0 || x > 60000)) throw new FormatException("Invalid script coordinates or wait.");
                    break;
                case "key": foreach (string k in text.Split('+')) Key(k); break;
                case "type": foreach (char c in text) Character(c); break;
                case "click": case "dclick": Button(text); break;
                default: throw new FormatException($"Unknown script command: {name}");
            }
            script.commands.Add(new(name, text, values));
        }
        return script;
    }
    public async Task RunAsync(SpiceSession session, CancellationToken ct)
    {
        try
        {
            foreach (var c in commands)
            {
                ct.ThrowIfCancellationRequested();
                Console.WriteLine($"script: {c.Name}");
                switch (c.Name)
                {
                    case "wait": await Task.Delay(c.Values[0], ct); break;
                    case "key":
                        var combo = c.Text.Split('+').Select(Key).ToArray();
                        foreach (ushort k in combo) session.KeyEvent(k, true);
                        await Task.Delay(40, ct);
                        foreach (ushort k in combo.Reverse()) session.KeyEvent(k, false);
                        break;
                    case "type":
                        foreach (char ch in c.Text)
                        {
                            var (k, shift) = Character(ch);
                            if (shift) session.KeyEvent(0x2A, true);
                            session.KeyEvent(k, true); session.KeyEvent(k, false);
                            if (shift) session.KeyEvent(0x2A, false);
                            await Task.Delay(25, ct);
                        }
                        break;
                    case "move": Move(c.Values[0], c.Values[1]); break;
                    case "click": case "dclick":
                        for (int i = 0; i < (c.Name == "click" ? 1 : 2); i++)
                        { session.MouseButton(Button(c.Text), true); await Task.Delay(40, ct); session.MouseButton(Button(c.Text), false); await Task.Delay(80, ct); }
                        break;
                    case "drag":
                        Move(c.Values[0], c.Values[1]); await Task.Delay(100, ct);
                        session.MouseButton(MouseButton.Left, true);
                        for (int i = 1; i <= 20; i++) { Move(c.Values[0] + (c.Values[2]-c.Values[0])*i/20, c.Values[1] + (c.Values[3]-c.Values[1])*i/20); await Task.Delay(30, ct); }
                        session.MouseButton(MouseButton.Left, false);
                        break;
                }
            }
        }
        finally { session.ReleaseAllKeys(); session.ReleaseAllButtons(); }
        void Move(int x, int y)
        {
            if (session.MouseMode != MouseMode.Client) throw new InvalidOperationException("Absolute scripted mouse input requires client mouse mode.");
            session.MousePosition(x, y);
        }
    }
}
