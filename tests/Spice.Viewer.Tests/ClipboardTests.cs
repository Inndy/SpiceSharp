using System.Buffers.Binary;
using Spice.Canvas;
using Windows.Win32;

namespace SpiceViewer;

public sealed class ClipboardTests
{
    static readonly byte[] expected = [1,2,3,255, 4,5,6,255, 7,8,9,255, 10,11,12,255];
    static void U32(byte[] a,int offset,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(offset),v);
    static byte[] Dib(int header,int bpp,bool bottom,uint compression=0)
    {
        int masks=header==40 && compression!=0 ? compression==6?16:12 :0;
        int stride=(2*(bpp/8)+3)&~3; byte[] a=new byte[header+masks+stride*2];
        U32(a,0,(uint)header); U32(a,4,2); U32(a,8,unchecked((uint)(bottom?2:-2)));
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12),1); BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(14),(ushort)bpp); U32(a,16,compression);
        if(compression!=0) { U32(a,40,0xFF0000); U32(a,44,0xFF00); U32(a,48,0xFF); if(compression==6) U32(a,52,0xFF000000); }
        for(int y=0;y<2;y++) for(int x=0;x<2;x++)
        { int dst=header+masks+(bottom?1-y:y)*stride+x*bpp/8; expected.AsSpan((y*2+x)*4,3).CopyTo(a.AsSpan(dst)); if(bpp==32)a[dst+3]=42; }
        return a;
    }
    static byte[] Bmp(byte[] dib,int offset=-1)
    {
        byte[] a=new byte[14+dib.Length]; a[0]=(byte)'B';a[1]=(byte)'M'; U32(a,2,(uint)a.Length);
        int header=(int)BinaryPrimitives.ReadUInt32LittleEndian(dib), comp=(int)BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        U32(a,10,(uint)(offset>=0?offset:14+header+(header==40&&comp!=0?comp==6?16:12:0))); dib.CopyTo(a,14);return a;
    }
    static void Canonical(byte[]? dib)
    { Assert.NotNull(dib); Assert.Equal(56,dib.Length); Assert.Equal(-2,BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8))); Assert.Equal(expected,dib[40..]); }

    [Theory]
    [InlineData(40,24,true,0u)] [InlineData(40,24,false,0u)]
    [InlineData(40,32,true,0u)] [InlineData(40,32,false,0u)]
    [InlineData(40,32,true,3u)] [InlineData(40,32,false,6u)]
    [InlineData(52,32,true,3u)] [InlineData(56,32,false,6u)]
    [InlineData(108,32,true,3u)] [InlineData(124,32,false,3u)]
    public void ValidBmpAndPackedDib(int header,int bpp,bool bottom,uint compression)
    {
        byte[] dib=Dib(header,bpp,bottom,compression); Canonical(ClipboardFormats.BmpToDib(Bmp(dib)));
        var global=ClipboardFormats.ToGlobal(dib); Assert.False(global.IsNull);
        try { byte[]? png=ClipboardFormats.DibToPng(global); Assert.NotNull(png); var decoded=Png.Decode(png); Assert.Equal((2,2),(decoded.Width,decoded.Height)); Assert.Equal(expected,decoded.Bgra); }
        finally { PInvoke.GlobalFree(global); }
    }
    [Fact] public void PngRoundTrip() => Canonical(ClipboardFormats.PngToDib(Png.Encode(expected,2,2,8)));

    [Theory] [InlineData(0)] [InlineData(13)] [InlineData(39)] [InlineData(53)]
    public void TruncatedBmpIsRejected(int length)=>Assert.Null(ClipboardFormats.BmpToDib(Bmp(Dib(40,24,true))[..length]));

    [Theory] [InlineData("offset")] [InlineData("header")] [InlineData("height")] [InlineData("palette")] [InlineData("masks")] [InlineData("planes")]
    public void MalformedBmpIsRejected(string kind)
    {
        byte[] a=Bmp(Dib(40,32,false,3));
        switch(kind)
        {
            case "offset":U32(a,10,uint.MaxValue);break;
            case "header":U32(a,14,uint.MaxValue);break;
            case "height":U32(a,22,0x80000000);break;
            case "palette":U32(a,46,256);break;
            case "masks":U32(a,54,0x123456);break;
            case "planes":a[26]=2;break;
        }
        Assert.Null(ClipboardFormats.BmpToDib(a));
    }
    [Fact]
    public void EveryTruncatedPackedDibStaysInsideItsAllocation()
    {
        byte[] source=Dib(40,32,false,3);
        for(int n=1;n<source.Length;n++)
        { var h=ClipboardFormats.ToGlobal(source.AsSpan(0,n)); try { Assert.Null(ClipboardFormats.DibToPng(h)); } finally{PInvoke.GlobalFree(h);} }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnicodeTextIsBoundedWithOrWithoutTerminator(bool terminate)
    {
        byte[] bytes=System.Text.Encoding.Unicode.GetBytes(terminate?"hello\0ignored":"hello");
        var h=ClipboardFormats.ToGlobal(bytes);try{Assert.Equal("hello",ClipboardFormats.ReadUnicodeText(h));}finally{PInvoke.GlobalFree(h);}
    }
    [Fact] public void MalformedPngIsRejected() => Assert.Null(ClipboardFormats.PngToDib([1,2,3]));
}
