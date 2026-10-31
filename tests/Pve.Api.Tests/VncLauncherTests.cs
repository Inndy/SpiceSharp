using Pve.Term;
using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>The launcher's side of VNC consoles (docs/decisions/0043): the display rule, the descriptor, the bridge's
/// command line, the viewer check and the setting.</summary>
public class VncLauncherTests
{
    [Theory]
    [InlineData(null, GuestDisplay.Vnc)]           // no vga: std on x86
    [InlineData("std", GuestDisplay.Vnc)]
    [InlineData("cirrus", GuestDisplay.Vnc)]
    [InlineData("vmware,memory=32", GuestDisplay.Vnc)]
    [InlineData("qxl", GuestDisplay.Spice)]
    [InlineData("qxl2,memory=64", GuestDisplay.Spice)]
    [InlineData("type=qxl4", GuestDisplay.Spice)]
    [InlineData("memory=32,qxl", GuestDisplay.Spice)]
    [InlineData("virtio", GuestDisplay.Spice)]
    [InlineData("virtio-gl,memory=256", GuestDisplay.Spice)]
    [InlineData("std,clipboard=vnc", GuestDisplay.Vnc)]
    [InlineData("serial0", GuestDisplay.Serial)]
    [InlineData("none", GuestDisplay.None)]
    [InlineData("qxl5", GuestDisplay.Vnc)]
    public void TheDisplayFollowsQemuServersRule(string? vga, GuestDisplay expected) => Assert.Equal(expected, GuestDisplays.Of(vga));

    [Fact]
    public async Task DisplayIsReadFromTheConfigAndVncDescriptorsCarryTheToken()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path.EndsWith("/qemu/100/config", StringComparison.Ordinal)
            ? FakeResponse.Data("""{"vga":"std","name":"web","digest":"x"}""")
            : FakeResponse.Data("""{"name":"web"}""");
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        Assert.Equal(GuestDisplay.Vnc, await c.GetDisplayAsync("node1", 100));
        Assert.Equal(GuestDisplay.Vnc, await c.GetDisplayAsync("node1", 101)); // no vga key
        Assert.Equal("GET /api2/json/nodes/node1/qemu/100/config", pve.Seen()[0].Method + " " + pve.Seen()[0].Path);

        var vm = new PveVm(100, "web", "node1", PveGuestType.Qemu, "running", false);
        var d = await c.CreateVncDescriptorAsync(vm);
        Assert.Equal(ConsoleKind.Vnc, d.Console);
        Assert.Equal("VM 100 web", d.Title);
        Assert.Equal($"{Fake.TokenId}={Fake.TokenSecret}", d.ApiToken);
        Assert.Equal(ConsoleKind.Vnc, TermDescriptor.Parse(d.ToJson()).Console);
        await Assert.ThrowsAsync<PveApiException>(() => c.CreateVncDescriptorAsync(vm with { Type = PveGuestType.Lxc }));
    }

    [Fact]
    public void TheBridgeRunsWithoutAWindowAndOnlyThePipeIsOnItsCommandLine()
    {
        var psi = VncLauncher.CreateStartInfo(@"C:\app\PveTerm.exe", "pveterm-abc", "0123", VncViewerKind.TigerVnc, null);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(["vnc", "--pipe", "pveterm-abc", "--nonce", "0123", "--viewer", "tigervnc"], psi.ArgumentList);
        var custom = VncLauncher.CreateStartInfo(@"C:\app\PveTerm.exe", "p", "n", VncViewerKind.Custom, [@"C:\v\viewer.exe", "{hostport}", "-x"]);
        Assert.Equal(["vnc", "--pipe", "p", "--nonce", "n", "--", @"C:\v\viewer.exe", "{hostport}", "-x"], custom.ArgumentList);
    }

    [Fact]
    public void AMissingViewerIsReportedBeforeAnythingIsRequested()
    {
        var none = new VncViewerProbe { FileExists = _ => false, GetEnv = _ => @"C:\Program Files", InstallLocation = _ => null };
        Assert.Contains("No VNC viewer was found", Assert.Throws<PveApiException>(() => VncLauncher.CheckViewer(VncViewerKind.Auto, null, none)).Message);
        Assert.Contains("UltraVNC Viewer was not found", Assert.Throws<PveApiException>(() => VncLauncher.CheckViewer(VncViewerKind.UltraVnc, null, none)).Message);
        Assert.Contains("custom VNC viewer command is empty", Assert.Throws<PveApiException>(() => VncLauncher.CheckViewer(VncViewerKind.Custom, [], none)).Message);
        VncLauncher.CheckViewer(VncViewerKind.None, null, none);
        VncLauncher.CheckViewer(VncViewerKind.Custom, ["x"], none);
    }

    [Fact]
    public void TheViewerSettingRoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vnc-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new LauncherSettings { VncViewer = VncViewerKind.Custom };
            s.VncCommand.AddRange([@"C:\v\viewer.exe", "{hostport}"]);
            s.Save(dir);
            var back = LauncherSettings.Load(dir);
            Assert.Equal(VncViewerKind.Custom, back.VncViewer);
            Assert.Equal([@"C:\v\viewer.exe", "{hostport}"], back.VncCommand);
            File.WriteAllText(Path.Combine(dir, LauncherSettings.FileName), """{"version":1,"vncViewer":"bogus"}""");
            Assert.Equal(VncViewerKind.Auto, LauncherSettings.Load(dir).VncViewer);
        }
        finally { Directory.Delete(dir, true); }
    }
}
