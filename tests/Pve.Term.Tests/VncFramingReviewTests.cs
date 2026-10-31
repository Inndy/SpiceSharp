using System.Buffers.Binary;

namespace Pve.Term.Tests;

public class VncFramingReviewTests
{
    [Fact]
    public void CustomViewerTitleStaysOneArgumentWithoutAShell()
    {
        const string title = "VM \"quoted\" & echo injected | other; $(command) \\";
        var psi = VncViewers.StartInfo(@"C:\viewer folder\viewer.exe",
            new[] { VncViewers.Expand("{hostport}", 4321, title), VncViewers.Expand("{title}", 4321, title) });
        Assert.False(psi.UseShellExecute);
        Assert.Equal(@"C:\viewer folder\viewer.exe", psi.FileName);
        Assert.Equal(new[] { "127.0.0.1::4321", title }, psi.ArgumentList);
        Assert.Empty(psi.Arguments);
    }

    public static IEnumerable<object[]> Messages()
    {
        yield return ["SetPixelFormat", new byte[20]];
        yield return ["SetEncodings empty", new byte[] { 2, 0, 0, 0 }];
        yield return ["SetEncodings maximum", Variable([2, 0, 255, 255], 4L * ushort.MaxValue)];
        yield return ["FramebufferUpdateRequest", Variable([3], 9)];
        yield return ["KeyEvent", Variable([4], 7)];
        yield return ["PointerEvent", Variable([5], 5)];
        yield return ["ClientCutText empty", new byte[] { 6, 0, 0, 0, 0, 0, 0, 0 }];
        yield return ["ClientCutText", Variable([6, 0, 0, 0, 0, 0, 1, 0], 256)];
        yield return ["ExtendedClipboard", Variable([6, 0, 0, 0, 255, 255, 255, 0], 256)];
        yield return ["Xvp", Variable([250], 3)];
        yield return ["SetDesktopSize empty", new byte[] { 251, 0, 1, 0, 1, 0, 0, 0 }];
        yield return ["SetDesktopSize maximum", Variable([251, 0, 1, 0, 1, 0, 255, 0], 16L * 255)];
        yield return ["QEMU extended key", Variable([255, 0], 10)];
        yield return ["QEMU audio enable", new byte[] { 255, 1, 0, 0 }];
        yield return ["QEMU audio disable", new byte[] { 255, 1, 0, 1 }];
        yield return ["QEMU audio format", Variable([255, 1, 0, 2], 6)];
    }

    static byte[] Variable(byte[] head, long payload)
    {
        var message = new byte[checked(head.Length + (int)payload)];
        message.AsSpan().Fill(3); // Payload bytes resemble a different message type; they must never be parsed.
        head.CopyTo(message, 0);
        return message;
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void NoSupportedMessageHasAnInteriorInjectionBoundary(string name, byte[] message)
    {
        var tracker = new ClientMessageTracker();
        for (int i = 0; i < message.Length; i++)
        {
            tracker.Feed(message.AsSpan(i, 1));
            Assert.False(tracker.Lost, name);
            Assert.Equal(i == message.Length - 1, tracker.AtBoundary);
        }
        // Also exercise every header split with multiple messages coalesced after it.
        for (int split = 0; split <= Math.Min(8, message.Length); split++)
        {
            tracker = new ClientMessageTracker();
            tracker.Feed(message.AsSpan(0, split));
            tracker.Feed(message.AsSpan(split));
            tracker.Feed([4, 1, 0, 0, 0, 0, 0, 65, 5, 0, 0, 1, 0, 2]);
            Assert.True(tracker.AtBoundary, name);
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ExtremeClipboardLengthsNeverWrapIntoABoundary(int length)
    {
        var tracker = new ClientMessageTracker();
        var header = new byte[8];
        header[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), length);
        tracker.Feed(header);
        tracker.Feed(new byte[65536]);
        Assert.False(tracker.AtBoundary);
        Assert.False(tracker.Lost);
    }
}
