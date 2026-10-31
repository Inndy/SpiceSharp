namespace Pve.Term.Tests;

/// <summary>Viewer detection and command lines (docs/decisions/0043), on a fake file system and registry.</summary>
public class VncViewerTests
{
    const string Pf = @"C:\Program Files", Pf86 = @"C:\Program Files (x86)";

    static VncViewerProbe Probe(IEnumerable<string> files, Dictionary<string, string>? keys = null) => new()
    {
        FileExists = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase).Contains,
        GetEnv = n => n switch { "ProgramW6432" or "ProgramFiles" => Pf, "ProgramFiles(x86)" => Pf86, _ => null },
        InstallLocation = k => keys?.GetValueOrDefault(k),
    };

    [Fact]
    public void NothingInstalledFindsNothing()
    {
        Assert.Empty(VncViewers.Scan(Probe([])));
        Assert.Null(VncViewers.Resolve(VncViewerKind.Auto, Probe([])));
        Assert.Null(VncViewers.Resolve(VncViewerKind.TigerVnc, Probe([])));
    }

    [Fact]
    public void AutomaticPrefersTigerVncThenTightThenUltraThenReal()
    {
        string tiger = $@"{Pf}\TigerVNC\vncviewer.exe", tight = $@"{Pf}\TightVNC\tvnviewer.exe",
            ultra = $@"{Pf}\uvnc bvba\UltraVNC\vncviewer.exe", real = $@"{Pf}\RealVNC\VNC Viewer\vncviewer.exe";
        Assert.Equal(VncViewerKind.TigerVnc, VncViewers.Resolve(VncViewerKind.Auto, Probe([tiger, tight, ultra, real]))!.Kind);
        Assert.Equal(VncViewerKind.TightVnc, VncViewers.Resolve(VncViewerKind.Auto, Probe([tight, ultra, real]))!.Kind);
        Assert.Equal(VncViewerKind.UltraVnc, VncViewers.Resolve(VncViewerKind.Auto, Probe([ultra, real]))!.Kind);
        Assert.Equal(real, VncViewers.Resolve(VncViewerKind.Auto, Probe([real]))!.Path);
        Assert.Equal(4, VncViewers.Scan(Probe([tiger, tight, ultra, real])).Count);
        // An explicit choice finds only that viewer.
        Assert.Null(VncViewers.Resolve(VncViewerKind.UltraVnc, Probe([tiger])));
    }

    [Fact]
    public void UninstallKeysWinOverDefaultFolders()
    {
        var keys = new Dictionary<string, string> { ["TigerVNC_is1"] = @"D:\Apps\TigerVNC\", ["Ultravnc2_is1"] = "\"E:\\uvnc\"" };
        var probe = Probe([@"D:\Apps\TigerVNC\vncviewer.exe", $@"{Pf}\TigerVNC\vncviewer.exe", @"E:\uvnc\vncviewer.exe"], keys);
        Assert.Equal(@"D:\Apps\TigerVNC\vncviewer.exe", VncViewers.Resolve(VncViewerKind.TigerVnc, probe)!.Path);
        Assert.Equal(@"E:\uvnc\vncviewer.exe", VncViewers.Resolve(VncViewerKind.UltraVnc, probe)!.Path);
        // A relative or empty InstallLocation is ignored.
        var bad = Probe([$@"{Pf}\TigerVNC\vncviewer.exe"], new() { ["TigerVNC_is1"] = @"TigerVNC" });
        Assert.Equal($@"{Pf}\TigerVNC\vncviewer.exe", VncViewers.Resolve(VncViewerKind.TigerVnc, bad)!.Path);
    }

    [Fact]
    public void UltraVncIsFoundUnderEitherPublisherFolder()
    {
        Assert.NotNull(VncViewers.Resolve(VncViewerKind.UltraVnc, Probe([$@"{Pf}\uvnc\UltraVNC\vncviewer.exe"])));
        Assert.NotNull(VncViewers.Resolve(VncViewerKind.UltraVnc, Probe([$@"{Pf86}\uvnc bvba\UltraVNC\vncviewer.exe"])));
    }

    [Fact]
    public void ArgumentsUseALiteralPortAndSilenceTheNoneWarnings()
    {
        Assert.Equal(["127.0.0.1::5999"], VncViewers.Arguments(VncViewerKind.TigerVnc, 5999));
        Assert.Equal(["127.0.0.1::1234"], VncViewers.Arguments(VncViewerKind.TightVnc, 1234));
        Assert.Equal(["127.0.0.1::1234", "-AllowUntrustedServers"], VncViewers.Arguments(VncViewerKind.UltraVnc, 1234));
        Assert.Equal(["127.0.0.1::1234", "-WarnUnencrypted=FALSE"], VncViewers.Arguments(VncViewerKind.RealVnc, 1234));
    }

    [Fact]
    public void CustomCommandsGetTheirPlaceholders()
    {
        Assert.Equal("viewer 127.0.0.1::4321 127.0.0.1 4321 VM 100", VncViewers.Expand("viewer {hostport} {host} {port} {title}", 4321, "VM 100"));
        Assert.Equal("{passwordfile}", VncViewers.Expand("{passwordfile}", 1, "t"));
    }

    [Fact]
    public void IdsRoundTrip()
    {
        foreach (var k in Enum.GetValues<VncViewerKind>()) Assert.Equal(k, VncViewers.Parse(VncViewers.Id(k)));
        Assert.Null(VncViewers.Parse("vncviewer"));
    }
}
