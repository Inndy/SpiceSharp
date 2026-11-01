using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Pve.Term.Tests;

public partial class HandoffTests
{
    static readonly byte[] Payload = Encoding.UTF8.GetBytes("{\"v\":1}");

    [Fact]
    public async Task ServesTheDescriptorOnceToTheClientWithTheNonce()
    {
        using var server = HandoffServer.Create();
        Assert.Matches("^pveterm-[0-9a-f]{32}$", server.PipeName);
        Assert.Matches("^[0-9a-f]{32}$", server.Nonce);
        var serve = server.ServeAsync(Payload, TimeSpan.FromSeconds(10));
        var got = await Task.Run(() => HandoffClient.Receive(server.PipeName, server.Nonce, TimeSpan.FromSeconds(5)));
        await serve;
        Assert.Equal(Payload, got);
        // One use: the pipe is gone.
        Assert.Throws<HandoffException>(() => HandoffClient.Receive(server.PipeName, server.Nonce, TimeSpan.FromMilliseconds(200)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.ServeAsync(Payload, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task WrongNonceGetsNothingAndEndsTheHandoff()
    {
        using var server = HandoffServer.Create();
        var serve = server.ServeAsync(Payload, TimeSpan.FromSeconds(10));
        string wrong = new('0', 32);
        Assert.NotEqual(wrong, server.Nonce);
        var e = Assert.Throws<HandoffException>(() => HandoffClient.Receive(server.PipeName, wrong, TimeSpan.FromSeconds(5)));
        Assert.Contains("no connection data", e.Message);
        await Assert.ThrowsAsync<HandoffException>(() => serve);
        Assert.Throws<HandoffException>(() => HandoffClient.Receive(server.PipeName, server.Nonce, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void ASecondPipeWithTheSameNameIsRefused()
    {
        using var first = HandoffServer.Create();
        var e = Assert.Throws<HandoffException>(() => HandoffServer.Create(first.PipeName));
        Assert.Contains("in use", e.Message);
    }

    [Fact]
    public async Task TimeoutAndAbandonEndTheHandoff()
    {
        using (var server = HandoffServer.Create())
        {
            var e = await Assert.ThrowsAsync<HandoffException>(() => server.ServeAsync(Payload, TimeSpan.FromMilliseconds(100)));
            Assert.Contains("in time", e.Message);
        }
        using (var server = HandoffServer.Create())
        {
            var e = await Assert.ThrowsAsync<HandoffException>(() => server.ServeAsync(Payload, TimeSpan.FromSeconds(30), Task.CompletedTask));
            Assert.Contains("exited", e.Message);
        }
    }

    [Theory]
    [InlineData("pveterm-x", "0123456789abcdef0123456789abcdef", true)]
    [InlineData(@"..\x", "0123456789abcdef0123456789abcdef", false)]
    [InlineData(@"\\host\pipe\x", "0123456789abcdef0123456789abcdef", false)]
    [InlineData("pveterm-x", "0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("pveterm-x", "0123", false)]
    public void ArgumentsAreValidated(string pipe, string nonce, bool valid)
        => Assert.Equal(valid, HandoffClient.IsValidPipeName(pipe) && HandoffClient.IsValidNonce(nonce));

    [Fact]
    public void ThePipeIsLockedToTheCurrentUserAndIntegrityLevel()
    {
        using var server = HandoffServer.Create();
        string user = WindowsIdentity.GetCurrent().User!.Value;
        string sddl = ReadSddl(server.Handle);
        // Protected DACL with exactly one allow ACE, for this user, full access.
        var dacl = Regex.Match(sddl, @"D:([A-Z]*)((\([^)]*\))*)");
        Assert.Contains("P", dacl.Groups[1].Value);
        var aces = Regex.Matches(dacl.Groups[2].Value, @"\(([^)]*)\)").Select(m => m.Groups[1].Value).ToArray();
        Assert.Single(aces);
        Assert.StartsWith("A;;FA;;;", aces[0]);
        // Windows abbreviates the local administrator SID as LA in SDDL.
        // Compare the decoded SID, keeping the exact-one-ACE/access checks above.
        var descriptor = new RawSecurityDescriptor(sddl);
        var ace = Assert.IsType<CommonAce>(descriptor.DiscretionaryAcl![0]);
        Assert.Equal(new SecurityIdentifier(user), ace.SecurityIdentifier);
        // Mandatory label: no read up and no write up from lower integrity levels.
        Assert.Matches(@"S:.*\(ML;;NWNR;;;[A-Z0-9-]+\)", sddl);
    }

    static unsafe string ReadSddl(SafeHandle handle)
    {
        const int SeKernelObject = 6;
        const uint Dacl = 0x4, Label = 0x10;
        uint err = GetSecurityInfo(handle, SeKernelObject, Dacl | Label, 0, 0, 0, 0, out nint sd);
        Assert.Equal(0u, err);
        try
        {
            Assert.True(ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, Dacl | Label, out nint text, 0));
            try { return Marshal.PtrToStringUni(text)!; }
            finally { LocalFree(text); }
        }
        finally { LocalFree(sd); }
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeHandle handle, int type, uint info, nint owner, nint group, nint dacl, nint sacl, out nint sd);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint sd, uint revision, uint info, out nint text, nint length);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);
}
