using System.Text;

namespace Pve.Term.Tests;

public class DescriptorTests
{
    const string Valid = """{"v":1,"apiUrl":"https://pve.example:8006","node":"pve-1","target":"qemu","vmid":100,"serial":"serial0","apiToken":"me@pve!term=hunter2-VALUE","caPem":null,"future":{"x":1}}""";

    static TermDescriptor Parse(string json) => TermDescriptor.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParsesAndIgnoresUnknownFields()
    {
        var d = Parse(Valid.Replace("\"caPem\":null,", ""));
        Assert.Equal(new Uri("https://pve.example:8006"), d.ApiUrl);
        Assert.Equal(("pve-1", TermTarget.Qemu, 100, "serial0"), (d.Node, d.Target, d.Vmid.GetValueOrDefault(), d.Serial));
        Assert.False(d.HasSession);
        Assert.Equal("VM 100 serial0 on pve-1", d.DisplayName);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var d = new TermDescriptor
        {
            ApiUrl = new("https://10.0.0.1:8006"), Node = "n", Target = TermTarget.Qemu, Vmid = 123, Serial = "serial3",
            AuthCookie = "PVE:u@pam:ABC::sig/+=", CsrfToken = "ABC:def", TermUser = "u@pam", TermTicket = "PVEVNC:1::x",
            TermPort = 5999, CaPem = "-----BEGIN CERTIFICATE-----\nAA==\n-----END CERTIFICATE-----\n", Title = "Mein Server",
        };
        var r = TermDescriptor.Parse(d.ToJson());
        Assert.Equal(d.ApiUrl, r.ApiUrl);
        Assert.Equal((d.Node, d.Target, d.Vmid, d.Serial), (r.Node, r.Target, r.Vmid, r.Serial));
        Assert.Equal((d.AuthCookie, d.CsrfToken, d.ApiToken), (r.AuthCookie, r.CsrfToken, r.ApiToken));
        Assert.Equal((d.TermUser, d.TermTicket, d.TermPort), (r.TermUser, r.TermTicket, r.TermPort));
        Assert.Equal((d.CaPem, d.Title), (r.CaPem, r.Title));
        Assert.True(r.HasSession);
    }

    [Fact]
    public void ToStringAndErrorsCarryNoSecret()
    {
        var d = Parse(Valid.Replace("\"caPem\":null,", ""));
        Assert.DoesNotContain("hunter2", d.ToString());
        var e = Assert.Throws<TermDescriptorException>(() => Parse(Valid.Replace("me@pve!term=hunter2-VALUE", "hunter2-VALUE")));
        Assert.DoesNotContain("hunter2", e.Message);
    }

    [Theory]
    [InlineData("\"v\":1,", "\"v\":2,")]                                              // version
    [InlineData("https://pve.example:8006", "http://pve.example:8006")]               // https only
    [InlineData("https://pve.example:8006", "https://pve.example:8006/x")]            // host and port only
    [InlineData("https://pve.example:8006", "https://u:p@pve.example:8006")]          // no user info
    [InlineData("\"node\":\"pve-1\"", "\"node\":\"../pve\"")]                           // safe id
    [InlineData("\"node\":\"pve-1\"", "\"node\":\"\"")]
    [InlineData("\"target\":\"qemu\"", "\"target\":\"vnc\"")]
    [InlineData("\"vmid\":100,", "\"vmid\":99,")]
    [InlineData("\"vmid\":100,", "\"vmid\":\"100\",")]                                  // wrong type
    [InlineData("\"vmid\":100,", "")]                                                 // missing vmid for qemu
    [InlineData("\"serial\":\"serial0\"", "\"serial\":\"serial4\"")]
    [InlineData("\"apiToken\":\"me@pve!term=hunter2-VALUE\"", "\"apiToken\":\"me@pve!term=SE\\r\\nX: y\"")] // header injection
    [InlineData("\"apiToken\":\"me@pve!term=hunter2-VALUE\"", "\"apiToken\":\"me@pve!term=A\",\"authCookie\":\"PVE:x\"")] // both
    [InlineData("\"apiToken\":\"me@pve!term=hunter2-VALUE\"", "\"other\":1")]          // neither
    [InlineData("\"caPem\":null", "\"termTicket\":\"PVEVNC:x\"")]                      // partial session
    [InlineData("\"caPem\":null", "\"termTicket\":\"T\",\"termUser\":\"u@pve\",\"termPort\":6000")]
    [InlineData("\"caPem\":null", "\"termTicket\":\"T\",\"termUser\":\"u:x\",\"termPort\":5900")] // ':' ends the user
    [InlineData("\"caPem\":null", "\"title\":\"a\\u001bb\"")]                          // control chars in the title
    public void RejectsInvalidDescriptors(string from, string to)
    {
        Assert.Contains(from, Valid);
        Assert.Throws<TermDescriptorException>(() => Parse(Valid.Replace(from, to)));
    }

    [Fact]
    public void RejectsNodeTargetWithVmidAndOversizeInput()
    {
        Assert.Throws<TermDescriptorException>(() => Parse(Valid.Replace("\"target\":\"qemu\"", "\"target\":\"node\"")));
        Assert.Throws<TermDescriptorException>(() => Parse("[1]"));
        Assert.Throws<TermDescriptorException>(() => Parse(Valid + "{}"));
        Assert.Throws<TermDescriptorException>(() => TermDescriptor.Parse(new byte[TermDescriptor.MaxBytes + 1]));
    }
}
