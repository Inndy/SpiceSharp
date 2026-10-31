using System.Text;

namespace Pve.Term.Tests;

/// <summary>The bridge's RFB pieces without a network: versions, VNC authentication, ServerInit checks, the message
/// tracker, the vncproxy answer and the descriptor's VNC form (docs/decisions/0043).</summary>
public class VncProtocolTests
{
    [Theory]
    [InlineData("RFB 003.008\n", 8)]
    [InlineData("RFB 003.007\n", 7)]
    [InlineData("RFB 003.003\n", 3)]
    [InlineData("RFB 003.005\n", 3)] // QEMU treats 3.4 and 3.5 as 3.3 too
    [InlineData("RFB 003.889\n", 8)] // Apple's 3.889 and anything above 3.8: 3.8
    [InlineData("RFB 004.001\n", 8)]
    public void VersionsMapToTheThreeRfbVersions(string text, int minor) =>
        Assert.Equal(minor, RfbHandshake.ParseVersion(Encoding.ASCII.GetBytes(text)));

    [Theory]
    [InlineData("RFB 003.008")]
    [InlineData("RFB 003.008\r")]
    [InlineData("RFB 0x3.008\n")]
    [InlineData("HTTP/1.1 200")]
    [InlineData("RFB 002.000\n")]
    public void NonRfbVersionsAreRefused(string text) =>
        Assert.Throws<RfbHandshakeException>(() => RfbHandshake.ParseVersion(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void VncAuthMatchesAnIndependentImplementationAndUsesEightCharacters()
    {
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)(i * 17 + 3)).ToArray();
        var response = new byte[16];
        RfbHandshake.VncAuthResponse(challenge, FakeVncproxy.Password, response);
        Assert.Equal(FakeVncproxy.ExpectedResponse(challenge, FakeVncproxy.Password), response);
        // The ticket starts with the password; QEMU reads only the first 8 bytes.
        var fromTicket = new byte[16];
        RfbHandshake.VncAuthResponse(challenge, FakeVncproxy.Ticket, fromTicket);
        Assert.Equal(response, fromTicket);
        // Short passwords are zero-padded.
        RfbHandshake.VncAuthResponse(challenge, "ab", response);
        Assert.Equal(FakeVncproxy.ExpectedResponse(challenge, "ab"), response);
        Assert.Throws<RfbHandshakeException>(() => RfbHandshake.VncAuthResponse(challenge, "pässwort", response));
    }

    [Fact]
    public void ReasonsLoseQemusNulAndControlCharacters()
    {
        Assert.Equal("Authentication failed", RfbHandshake.ReasonText("Authentication failed\0"u8));
        Assert.Equal("a?b", RfbHandshake.ReasonText("a\nb"u8));
        Assert.Equal(new byte[] { 0, 0, 0, 2, (byte)'h', (byte)'i' }, RfbHandshake.Reason("hi"));
    }

    [Fact]
    public void ServerInitIsChecked()
    {
        var ok = FakeVncproxy.ServerInit(5120, 2160, "QEMU (vm)");
        Assert.Equal(9, RfbHandshake.CheckServerInit(ok.AsSpan(0, 24)));
        Assert.Equal((5120, 2160), RfbHandshake.ServerInitSize(ok));
        foreach (var (offset, value) in new[] { (0, (byte)0x00), (4, (byte)24), (5, (byte)33) })
        {
            var bad = FakeVncproxy.ServerInit(0x100, 0x100, "x");
            bad[offset] = value;
            if (offset == 0) bad[1] = 0;
            Assert.Throws<RfbHandshakeException>(() => RfbHandshake.CheckServerInit(bad.AsSpan(0, 24)));
        }
    }

    static ClientMessageTracker Fed(params byte[][] chunks)
    {
        var t = new ClientMessageTracker();
        foreach (var c in chunks) t.Feed(c);
        return t;
    }

    [Fact]
    public void TrackerFollowsEveryMessageQemuAccepts()
    {
        byte[] setPixelFormat = new byte[20];
        byte[] setEncodings = [2, 0, 0, 3, 0, 0, 0, 7, 0, 0, 0, 16, 0xFF, 0xFF, 0xFE, 0xFE];
        byte[] fbur = [3, 1, 0, 0, 0, 0, 1, 0, 1, 0];
        byte[] key = [4, 1, 0, 0, 0, 0, 0, 0x61];
        byte[] pointer = [5, 1, 0, 1, 0, 2];
        byte[] cut = [6, 0, 0, 0, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c'];
        byte[] cutExtended = [6, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFC, 1, 2, 3, 4]; // length -4
        byte[] xvp = [250, 0, 1, 2];
        byte[] desktopSize = [251, 0, 4, 0, 3, 0, 1, 0, .. new byte[16]];
        byte[] extKey = [255, 0, 0, 1, 0, 0, 0, 0x61, 0, 0, 0, 0x1E];
        byte[] audioEnable = [255, 1, 0, 0];
        byte[] audioFormat = [255, 1, 0, 2, 3, 2, 0, 0, 0xBB, 0x80];
        byte[] all = [.. setPixelFormat, .. setEncodings, .. fbur, .. key, .. pointer, .. cut, .. cutExtended, .. xvp,
            .. desktopSize, .. extKey, .. audioEnable, .. audioFormat];
        Assert.True(Fed(all).AtBoundary);
        // Byte by byte: only the message ends are boundaries.
        var t = new ClientMessageTracker();
        var ends = new HashSet<int>();
        int at = 0;
        foreach (var m in new[] { setPixelFormat, setEncodings, fbur, key, pointer, cut, cutExtended, xvp, desktopSize, extKey, audioEnable, audioFormat })
            ends.Add(at += m.Length);
        for (int i = 0; i < all.Length; i++)
        {
            t.Feed(all.AsSpan(i, 1));
            Assert.Equal(ends.Contains(i + 1), t.AtBoundary);
        }
    }

    [Fact]
    public void TrackerGivesUpOnUnknownMessages()
    {
        var t = Fed([4, 1, 0, 0, 0, 0, 0, 0x61], [150, 1, 0, 0]);
        Assert.True(t.Lost);
        Assert.False(t.AtBoundary);
        Assert.Equal(150, t.LostType);
        Assert.True(Fed([255, 7]).Lost);
        Assert.True(Fed([255, 1, 0, 9]).Lost);
        Assert.False(Fed([4, 1, 0]).AtBoundary);
    }

    [Fact]
    public void VncproxyAnswersAreParsedAndChecked()
    {
        var s = VncproxyConnector.ParseSession("""{"data":{"cert":"x","password":"Ab1!c`D@","port":"5901","ticket":"Ab1!c`D@:PVEVNC:1","upid":"u","user":"root@pam"}}"""u8);
        Assert.Equal(5901, s.Port);
        Assert.Equal("Ab1!c`D@", s.VncPassword);
        Assert.DoesNotContain("PVEVNC", s.ToString());
        Assert.DoesNotContain("Ab1", s.ToString());
        Assert.Equal("t", VncproxyConnector.ParseSession("""{"data":{"port":5900,"ticket":"t","user":"u"}}"""u8).VncPassword);
        Assert.Throws<TermConnectException>(() => VncproxyConnector.ParseSession("""{"data":{"port":6000,"ticket":"t","user":"u"}}"""u8));
        Assert.Throws<TermConnectException>(() => VncproxyConnector.ParseSession("""{"data":{"port":5900,"ticket":"t","user":"u","password":"short"}}"""u8));
        Assert.Throws<TermConnectException>(() => VncproxyConnector.ParseSession("""{"data":{"port":5900,"user":"u"}}"""u8));
        Assert.Throws<TermConnectException>(() => VncproxyConnector.ParseSession("not json"u8));
    }

    [Fact]
    public void VncDescriptorsRoundTripAndAreValidated()
    {
        var d = new TermDescriptor
        {
            ApiUrl = new Uri("https://pve.example:8006"), Node = "pve1", Target = TermTarget.Qemu, Console = ConsoleKind.Vnc,
            Vmid = 101, ApiToken = "u@pve!t=s",
        };
        d.Validate();
        var back = TermDescriptor.Parse(d.ToJson());
        Assert.Equal(ConsoleKind.Vnc, back.Console);
        Assert.Equal("VM 101 on pve1", back.DisplayName);
        Assert.Equal("https://pve.example:8006/api2/json/nodes/pve1/qemu/101/vncproxy", back.VncproxyUri.ToString());
        Assert.Equal(ConsoleKind.Terminal, TermDescriptor.Parse("""{"v":1,"apiUrl":"https://h:8006","node":"n","target":"qemu","vmid":100,"apiToken":"a@b!c=d"}"""u8).Console);
        Assert.Throws<TermDescriptorException>(() => TermDescriptor.Parse("""{"v":1,"apiUrl":"https://h:8006","node":"n","target":"lxc","console":"vnc","vmid":100,"apiToken":"a@b!c=d"}"""u8));
        Assert.Throws<TermDescriptorException>(() => TermDescriptor.Parse("""{"v":1,"apiUrl":"https://h:8006","node":"n","target":"qemu","console":"vnc","vmid":100,"serial":"serial0","apiToken":"a@b!c=d"}"""u8));
        Assert.Throws<TermDescriptorException>(() => TermDescriptor.Parse("""{"v":1,"apiUrl":"https://h:8006","node":"n","target":"qemu","console":"rdp","vmid":100,"apiToken":"a@b!c=d"}"""u8));
        Assert.Throws<TermDescriptorException>(() => TermDescriptor.Parse("""{"v":1,"apiUrl":"https://h:8006","node":"n","target":"qemu","console":"vnc","vmid":100,"apiToken":"a@b!c=d","termUser":"u","termTicket":"t","termPort":5900}"""u8));
    }
}
