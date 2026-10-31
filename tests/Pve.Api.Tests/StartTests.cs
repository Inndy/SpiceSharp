using System.Text.Json;
using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>Start for a stopped guest (docs/decisions/0038, answer 4): the request, its task, and the privilege
/// check, against the loopback fake.</summary>
public class StartTests
{
    const string QemuUpid = "UPID:node1:000A1B2C:0123ABCD:6720A1B2:qmstart:100:launcher@pve!viewer:";
    const string LxcUpid = "UPID:node1:000A1B2D:0123ABCE:6720A1B3:vzstart:101:alice@pve:";

    static PveVm Vm(int vmid, PveGuestType type = PveGuestType.Qemu) => new(vmid, "g", "node1", type, "stopped", false);

    [Fact]
    public async Task StartPostsToTheGuestsStatusStartAndReturnsTheTask()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(JsonSerializer.Serialize(QemuUpid, ApiTestJson.Default.String));
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        Assert.Equal(QemuUpid, await c.StartGuestAsync(Vm(100)));
        var r = Assert.Single(pve.Seen());
        Assert.Equal("POST", r.Method);
        Assert.Equal("/api2/json/nodes/node1/qemu/100/status/start", r.Path);
        Assert.Equal("", r.Body); // no parameters: skiplock, stateuri and the like are root's or migration's
    }

    [Fact]
    public async Task AContainerStartsThroughLxcWithTheTicketsCsrfToken()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path switch
        {
            "/api2/json/access/ticket" => Fake.TicketAnswer(Fake.Ticket),
            _ => FakeResponse.Data(JsonSerializer.Serialize(LxcUpid, ApiTestJson.Default.String)),
        };
        using var c = new PveClient(pve.Url, pve.CaPem);
        await c.LoginAsync("alice", "pve", Fake.Password);
        Assert.Equal(LxcUpid, await c.StartGuestAsync(Vm(101, PveGuestType.Lxc)));
        var r = pve.Seen()[^1];
        Assert.Equal("/api2/json/nodes/node1/lxc/101/status/start", r.Path);
        Assert.Equal(Fake.Csrf, r.Header("CSRFPreventionToken"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"not a upid\"")]
    [InlineData("\"UPID:node1:000A1B2C:0123ABCD:6720A1B2:qmstart:100:root@pam\"")] // no closing ':'
    [InlineData("\"UPID:node1:000A1B2C:0123ABCD:6720A1B2:qmstart:100:root@pam:\\n\"")]
    [InlineData("{\"upid\":\"x\"}")]
    public async Task AnAnswerWithoutAUsableTaskIdIsAnError(string data)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(data);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.StartGuestAsync(Vm(100)));
        Assert.Contains("no task ID", e.Message);
    }

    [Fact]
    public async Task ARefusedStartShowsTheServersReason()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => new FakeResponse(403, "Permission check failed (/vms/100, VM.PowerMgmt)", "{\"data\":null}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.StartGuestAsync(Vm(100)));
        Assert.Equal("403 Permission check failed (/vms/100, VM.PowerMgmt)", e.Message);
        Assert.Equal(403, e.StatusCode);
    }

    [Fact]
    public void TaskIdsFollowTheServersFormat()
    {
        Assert.True(PveTask.IsUpid(QemuUpid));
        Assert.True(PveTask.IsUpid(LxcUpid));
        Assert.True(PveTask.IsUpid("UPID:pve-1:0000ABCD:1234ABCDE:6720A1B2:hastart:100:root@pam:")); // 9-digit pstart
        Assert.False(PveTask.IsUpid("UPID:node1:000A1B2C:0123ABCD:6720A1B2:qm/start:100:root@pam:"));
        Assert.False(PveTask.IsUpid("UPID:-node:000A1B2C:0123ABCD:6720A1B2:qmstart:100:root@pam:"));
        Assert.False(PveTask.IsUpid(""));
    }

    [Theory]
    [InlineData("""{"status":"running","upid":"x"}""", true, null, false)]
    [InlineData("""{"status":"stopped","exitstatus":"OK"}""", false, "OK", false)]
    [InlineData("""{"status":"stopped","exitstatus":"WARNINGS: 2"}""", false, "WARNINGS: 2", false)]
    [InlineData("""{"status":"stopped","exitstatus":"start failed: QEMU exited with code 1"}""", false, "start failed: QEMU exited with code 1", true)]
    [InlineData("""{"status":"stopped","exitstatus":"unexpected status"}""", false, "unexpected status", true)]
    [InlineData("""{"status":"stopped"}""", false, null, true)]
    public void TaskStatusIsRead(string json, bool running, string? exit, bool failed)
    {
        using var doc = JsonDocument.Parse(json);
        var t = PveTask.Parse(QemuUpid, doc.RootElement);
        Assert.Equal(running, t.Running);
        Assert.Equal(exit, t.ExitStatus);
        Assert.Equal(failed, t.Failed);
    }

    [Fact]
    public async Task WaitingFollowsTheTaskUntilItStops()
    {
        await using var pve = new FakePve(TestPki.Shared);
        int asked = 0;
        pve.Handler = r => FakeResponse.Data(Interlocked.Increment(ref asked) < 3
            ? """{"status":"running"}"""
            : """{"status":"stopped","exitstatus":"OK"}""");
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var t = await c.WaitForTaskAsync("node1", QemuUpid, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
        Assert.False(t.Running);
        Assert.False(t.Failed);
        Assert.Equal(3, asked);
        // The UPID is one path segment, escaped.
        Assert.All(pve.Seen(), r => Assert.Equal("/api2/json/nodes/node1/tasks/" + Uri.EscapeDataString(QemuUpid) + "/status", r.Path));
    }

    [Fact]
    public async Task WaitingStopsAtTheLimit()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data("""{"status":"running"}""");
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var t = await c.WaitForTaskAsync("node1", QemuUpid, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));
        Assert.True(t.Running);
        Assert.InRange(pve.Seen().Length, 1, 6);
    }

    [Fact]
    public async Task ABadTaskIdIsNotSent()
    {
        await using var pve = new FakePve(TestPki.Shared);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        await Assert.ThrowsAsync<PveApiException>(() => c.GetTaskStatusAsync("node1", "UPID:../../x"));
        await Assert.ThrowsAsync<PveApiException>(() => c.GetTaskStatusAsync("node1/..", QemuUpid));
        Assert.Empty(pve.Seen());
    }

    [Theory]
    [InlineData("""{"/vms/100":{"VM.PowerMgmt":1,"VM.Console":1}}""", true)]
    [InlineData("""{"/vms/100":{"VM.Console":1,"VM.Audit":0}}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("""[]""", null)]
    public async Task ThePrivilegeIsTheServersAnswerForThatPath(string data, bool? expected)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(data);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        Assert.Equal(expected, await c.HasPrivilegeAsync("/vms/100", "VM.PowerMgmt"));
        var r = Assert.Single(pve.Seen());
        Assert.Equal("GET", r.Method);
        Assert.Equal("/api2/json/access/permissions?path=%2Fvms%2F100", r.Path);
    }

    [Fact]
    public async Task AnUnreadablePrivilegeIsUnknownButAnEndedSessionIsSaid()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => new FakeResponse(500, "internal error", "{}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        Assert.Null(await c.HasPrivilegeAsync("/vms/100", "VM.PowerMgmt"));
        pve.Handler = r => new FakeResponse(401, "invalid token", "{}");
        await Assert.ThrowsAsync<PveAuthException>(() => c.HasPrivilegeAsync("/vms/100", "VM.PowerMgmt"));
    }

    [Fact]
    public async Task TheQuietListLogsOnlyFailures()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data("[]");
        var log = new List<string>();
        using var c = new PveClient(pve.Url, pve.CaPem) { Log = log.Add };
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        await c.ListVmsAsync(log: false);
        Assert.Empty(log);
        pve.Handler = r => new FakeResponse(500, "busy", "{}");
        await Assert.ThrowsAsync<PveApiException>(() => c.ListVmsAsync(log: false));
        Assert.Single(log, l => l.Contains("-> 500"));
    }
}
