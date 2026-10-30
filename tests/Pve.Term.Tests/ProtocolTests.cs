using System.Text;

namespace Pve.Term.Tests;

public class ProtocolTests
{
    static string Frame(Func<byte[], int> write)
    {
        var buf = new byte[64];
        return Encoding.ASCII.GetString(buf, 0, write(buf));
    }

    [Fact]
    public void DataFrameCountsUtf8Bytes()
    {
        var data = Encoding.UTF8.GetBytes("é中\r"); // 2 + 3 + 1 bytes, as pve-xtermjs counts (unescape(encodeURIComponent))
        var buf = new byte[64];
        int n = TermproxyProtocol.WriteData(buf, data);
        Assert.Equal("0:6:"u8.ToArray().Concat(data), buf.Take(n));
    }

    [Fact]
    public void EmptyAndLargeDataFrames()
    {
        Assert.Equal("0:0:", Frame(b => TermproxyProtocol.WriteData(b, [])));
        var big = new byte[TermproxyChannel.MaxChunk];
        var buf = new byte[big.Length + TermproxyProtocol.MaxDataHeader];
        int n = TermproxyProtocol.WriteData(buf, big);
        Assert.Equal("0:32768:", Encoding.ASCII.GetString(buf, 0, 8));
        Assert.Equal(8 + big.Length, n);
    }

    [Theory]
    [InlineData(80, 24, "1:80:24:")]
    [InlineData(0, -5, "1:1:1:")]
    [InlineData(70000, 65535, "1:65535:65535:")]
    public void ResizeFrameClampsToTermproxyRange(int cols, int rows, string expected)
        => Assert.Equal(expected, Frame(b => TermproxyProtocol.WriteResize(b, cols, rows)));

    [Fact]
    public void PingAndAuthLine()
    {
        Assert.Equal("2", Encoding.ASCII.GetString(TermproxyProtocol.Ping));
        Assert.Equal("root@pam!t:PVEVNC:AB::cd\n", Encoding.UTF8.GetString(TermproxyProtocol.AuthLine("root@pam!t", "PVEVNC:AB::cd")));
    }

    [Fact]
    public void SessionAnswerParsesNumberOrStringPort()
    {
        var s = TermproxyConnector.ParseSession("""{"data":{"upid":"UPID:x","port":5905,"ticket":"T:1","user":"u@pve!t","extra":{"a":[1]}}}"""u8);
        Assert.Equal(new TermproxySession("u@pve!t", "T:1", 5905), s);
        s = TermproxyConnector.ParseSession("""{"success":1,"data":{"user":"u@pam","ticket":"T","port":"5999"}}"""u8);
        Assert.Equal(5999, s.Port);
    }

    [Theory]
    [InlineData("""{"data":{"user":"u","ticket":"T"}}""")]
    [InlineData("""{"data":{"user":"u","ticket":"T","port":22}}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""not json""")]
    [InlineData("""{"user":"u","ticket":"T","port":5900}""")]
    public void SessionAnswerRejectsIncompleteData(string json)
        => Assert.Throws<TermConnectException>(() => TermproxyConnector.ParseSession(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void UrisFollowPveXtermjs()
    {
        var d = new TermDescriptor { ApiUrl = new("https://pve.example:8006"), Node = "n1", Target = TermTarget.Lxc, Vmid = 101, ApiToken = "a@pve!t=s" };
        Assert.Equal("https://pve.example:8006/api2/json/nodes/n1/lxc/101/termproxy", d.TermproxyUri.AbsoluteUri);
        Assert.Equal("wss://pve.example:8006/api2/json/nodes/n1/lxc/101/vncwebsocket?port=5900&vncticket=PVEVNC%3AAB%3A%3Ac%2Bd%2Fe%3D",
            d.WebSocketUri(5900, "PVEVNC:AB::c+d/e=").AbsoluteUri);
        var node = new TermDescriptor { ApiUrl = new("https://h:8006"), Node = "n1", Target = TermTarget.Node, ApiToken = "a@pve!t=s" };
        Assert.Equal("/api2/json/nodes/n1/termproxy", node.TermproxyUri.AbsolutePath);
    }
}
