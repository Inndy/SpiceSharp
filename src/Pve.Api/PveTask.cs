using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pve.Api;

/// <summary>
/// A Proxmox worker task as <c>GET /nodes/{node}/tasks/{upid}/status</c> reports it (pve-manager API2/Tasks.pm
/// <c>read_task_status</c>): <c>status</c> is <c>running</c> or <c>stopped</c>, and a stopped task has
/// <c>exitstatus</c>: <c>OK</c>, <c>WARNINGS: N</c>, or the error (pve-common PVE/UPID.pm <c>read_status</c>).
/// </summary>
public sealed partial record PveTask(string Upid, bool Running, string? ExitStatus)
{
    /// <summary>Stopped with an error: anything but OK or warnings (PVE::UPID <c>status_is_error</c>, which also
    /// counts an unreadable status as an error).</summary>
    public bool Failed => !Running && !(ExitStatus == "OK" || ExitStatus is not null && WarningsStatus().IsMatch(ExitStatus));

    /// <summary>The server's error text, trimmed for one line.</summary>
    public string Error => ExitStatus is { Length: > 0 } s ? (s.Length > 300 ? s[..300] + "..." : s) : "the task ended without a status";

    /// <summary>The UPID form PVE::UPID <c>decode</c> accepts:
    /// <c>UPID:node:pid:pstart:starttime:type:id:user:</c>.</summary>
    public static bool IsUpid(string s) => s.Length <= 512 && UpidPattern().IsMatch(s);

    internal static PveTask Parse(string upid, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new PveApiException("The server's task status is not an object.");
        string? status = data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        string? exit = data.TryGetProperty("exitstatus", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        return status switch
        {
            "running" => new PveTask(upid, true, null),
            "stopped" => new PveTask(upid, false, exit),
            _ => throw new PveApiException("The server's task status is not running or stopped."),
        };
    }

    [GeneratedRegex(@"^UPID:[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?:[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8,9}:[0-9A-Fa-f]{8}:[^:\s/]+:[^:\s/]*:[^:\s/]+:\z")]
    private static partial Regex UpidPattern();

    [GeneratedRegex(@"^WARNINGS: [0-9]+\z")]
    private static partial Regex WarningsStatus();
}
