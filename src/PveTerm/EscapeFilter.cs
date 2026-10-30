namespace PveTerm;

/// <summary>What a typed character asks the bridge to do besides sending bytes.</summary>
enum EscapeCommand { None, Quit, Help }

/// <summary>
/// The ssh-style escape on the input side, the bridge's only interpretation of input (docs/decisions/0036; the
/// human's decision of 2026-10-30). Following ssh: only the escape character (default <c>~</c>) typed at the start
/// of a line, that is first in the session or right after Enter (<c>\r</c> or <c>\n</c>), is held until the next
/// key. Then <c>.</c> quits the bridge, <c>?</c> shows help, the escape character sends itself once, and anything
/// else sends the held escape character followed by that key. There is no timeout. Like ssh, a paste is just typing:
/// pasted <c>"\r~."</c> quits. Text inside a bracketed paste (<c>ESC[200~</c> ... <c>ESC[201~</c>, which the terminal
/// sends when the guest asked for it) is never taken as an escape. Output is never looked at.
/// </summary>
sealed class EscapeFilter
{
    readonly char _escape;
    readonly bool _enabled;
    bool _atLineStart = true, _held, _inPaste;
    int _marker; // characters of "\e[20" matched so far, 5 after '0'/'1' (then _markerEnds says which)
    bool _markerEnds;

    /// <summary>Characters that can't be the escape character: they are its commands (<c>.</c> quit, <c>?</c> help).</summary>
    public static bool IsCommand(char c) => c is '.' or '?';

    /// <param name="escape">The escape character, or null to turn the escape off.</param>
    public EscapeFilter(char? escape)
    {
        _enabled = escape is not null;
        _escape = escape ?? '\0';
    }

    public bool Enabled => _enabled;
    public char Character => _escape;

    /// <summary>Takes one typed character; writes what to send (0 to 2 characters) to <paramref name="send"/>.</summary>
    public EscapeCommand Feed(char c, Span<char> send, out int count)
    {
        count = 0;
        bool wasInPaste = _inPaste;
        TrackPaste(c);
        if (!_enabled)
        {
            send[count++] = c;
            return EscapeCommand.None;
        }
        if (_held)
        {
            _held = false;
            // Doubling quotes first, so an escape character of '.' or '?' (which the command line refuses anyway)
            // still sends itself rather than run a command.
            if (c != _escape)
            {
                if (c == '.') return EscapeCommand.Quit;
                if (c == '?') return EscapeCommand.Help; // still at line start, as in ssh: "~?" then "~." quits
                send[count++] = _escape;
            }
        }
        else if (_atLineStart && c == _escape && !wasInPaste)
        {
            _held = true;
            return EscapeCommand.None;
        }
        _atLineStart = c is '\r' or '\n';
        send[count++] = c;
        return EscapeCommand.None;
    }

    /// <summary>Follows the bracketed-paste markers ESC[200~ (start) and ESC[201~ (end) through the input.</summary>
    void TrackPaste(char c)
    {
        switch (_marker)
        {
            case 0 when c == '\e':
            case 1 when c == '[':
            case 2 when c == '2':
            case 3 when c == '0':
                _marker++;
                return;
            case 4 when c is '0' or '1':
                _markerEnds = c == '1';
                _marker++;
                return;
            case 5 when c == '~':
                _inPaste = !_markerEnds;
                _marker = 0;
                return;
            default:
                _marker = c == '\e' ? 1 : 0;
                return;
        }
    }

    /// <summary>The help line for <c>~?</c>, made once.</summary>
    public string HelpText => _help ??= $"Escapes, typed at the start of a line: {_escape}. quit PveTerm (the guest " +
        $"session keeps running), {_escape}{_escape} send {_escape}, {_escape}? this help.";

    /// <summary>The hint for the startup line, or null when the escape is off.</summary>
    public string? Hint => _enabled ? $"{_escape}. to quit, {_escape}? for help" : null;

    string? _help;
}
