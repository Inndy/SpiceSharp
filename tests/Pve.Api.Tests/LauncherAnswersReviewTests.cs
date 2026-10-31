using System.Net;
using System.Reflection;
using System.Text;

namespace Pve.Api.Tests;

/// <summary>Offline regressions: no sockets, windows, credential store or real server.</summary>
public sealed class LauncherAnswersReviewTests
{
    const string Secret = "synthetic-review-secret";
    const string Upid = "UPID:node1:00000001:00000001:00000001:qmstart:100:alice@pve:";

    sealed class Replies(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply(request), Encoding.UTF8, "application/json"),
            });
    }

    static PveClient OfflineClient(Func<HttpRequestMessage, string> reply)
    {
        var c = new PveClient(new Uri("https://pve.example:8006/"), null);
        // Replace transport only; the actual request construction, parsing and logging still execute.
        var field = typeof(PveClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(c)!).Dispose();
        field.SetValue(c, new HttpClient(new Replies(reply)));
        c.UseApiToken("alice@pve!review", Secret);
        return c;
    }

    [Fact]
    public async Task StartTaskIdDoesNotLogAnEchoedAuthorizationSecret()
    {
        using var c = OfflineClient(r =>
        {
            Assert.Contains(Secret, r.Headers.GetValues("Authorization").Single());
            return "{\"data\":\"" + Upid.Replace("alice@pve", Secret + "@pve") + "\"}";
        });
        var log = new List<string>();
        c.Log = log.Add;
        var vm = new PveVm(100, "guest", "node1", PveGuestType.Qemu, "stopped", false);
        await c.StartGuestAsync(vm);
        Assert.DoesNotContain(Secret, string.Join("\n", log));
    }

    [Fact]
    public async Task TaskErrorDoesNotExposeAnEchoedAuthorizationSecret()
    {
        using var c = OfflineClient(_ => "{\"data\":{\"status\":\"stopped\",\"exitstatus\":\"error " + Secret + "\"}}");
        var task = await c.GetTaskStatusAsync("node1", Upid);
        Assert.True(task.Failed);
        // MainWindow passes this straight to ShowError, which also writes it to launcher.log.
        Assert.DoesNotContain(Secret, task.Error);
    }

    [Fact]
    public void SlowFollowUpDoesNotStartAnotherRequestAfterThirtySeconds()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var clock = new TimerClock();
        var pending = new TaskCompletionSource();
        int calls = 0;
        using var s = new RefreshScheduler(TimeSpan.Zero, () =>
        {
            calls++;
            return calls == 1 ? pending.Task : Task.CompletedTask;
        }, a => a(), clock);
        s.Armed = true;
        s.Visible = true;
        s.FollowUp(() => false);
        clock.Advance(TimeSpan.FromSeconds(29));
        pending.SetResult();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
        Assert.False(s.IsScheduled);
    }

    [Fact]
    public void CustomWindowsTerminalEscapesSeparatorsInTheBridgePath()
    {
        var target = TerminalTarget.Custom(["wt.exe", "new-tab", "--", "{exe}", "console", "--pipe", "{pipe}", "--nonce", "{nonce}"]);
        var psi = TerminalLauncher.CreateStartInfo(target, @"C:\apps;archive\PveTerm.exe", "pipe", "nonce", "title");
        Assert.Contains(@"C:\apps\;archive\PveTerm.exe", psi.ArgumentList);
    }
}
