using Spice.Canvas;
using Spice.Wire;

namespace Spice.Replay.Tests;

public sealed class CanvasDeterministicTests
{
    static byte[] Bytes(Action<BinaryWriter> action){using var m=new MemoryStream();using var w=new BinaryWriter(m);action(w);return m.ToArray();}
    static void Rect(BinaryWriter w,SpiceRect r){w.Write(r.Top);w.Write(r.Left);w.Write(r.Bottom);w.Write(r.Right);}
    static void Create(Spice.Canvas.Canvas c,int w,int h)=>c.Apply(DisplayMsg.SurfaceCreate,Bytes(b=>{b.Write(0u);b.Write((uint)w);b.Write((uint)h);b.Write(32u);b.Write(1u);}));
    static bool Contains(SpiceRect r,int x,int y)=>x>=r.Left&&x<r.Right&&y>=r.Top&&y<r.Bottom;

    [Theory] [InlineData(12345)] [InlineData(349)] [InlineData(7711)]
    public void OverlappingClipUnionTouchesEveryCoveredPixelExactlyOnce(int seed)
    {
        const int width=31,height=23;var rng=new Random(seed);
        using var canvas=new Spice.Canvas.Canvas();Create(canvas,width,height);
        for(int iteration=0;iteration<100;iteration++)
        {
            canvas.Primary!.Pixels.AsSpan().Clear();
            var box=new SpiceRect(rng.Next(-8,8),rng.Next(-8,8),rng.Next(24,40),rng.Next(17,31));
            var clips=Enumerable.Range(0,rng.Next(1,60)).Select(_=>
            {int x=rng.Next(-10,width+10),y=rng.Next(-10,height+10);return new SpiceRect(x,y,x+rng.Next(0,25),y+rng.Next(0,20));}).ToArray();
            canvas.Apply(DisplayMsg.DrawFill,Bytes(b=>
            {
                b.Write(0u);Rect(b,box);b.Write(SpiceClip.TypeRects);b.Write((uint)clips.Length);foreach(var r in clips)Rect(b,r);
                b.Write((byte)BrushType.Solid);b.Write(0xFFFFFFu);b.Write(Ropd.OpXor);
                b.Write((byte)0);b.Write(0);b.Write(0);b.Write(0u);
            }));
            Assert.Equal(0,canvas.Errors);
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                bool covered=Contains(box,x,y)&&clips.Any(r=>Contains(r,x,y));
                Assert.True(canvas.Primary.Row(y)[x]==(covered?0xFFFFFFFFu:0u),$"seed={seed}, iteration={iteration}, pixel={x},{y}");
            }
        }
    }
    [Theory] [InlineData(101)] [InlineData(555)]
    public void ClippedStrokeMatchesNaiveBresenhamAcrossAllOctants(int seed)
    {
        const int width=31,height=23;var rng=new Random(seed);var surface=new Surface(0,width,height,32,true);var white=PixelSource.Solid(0xFFFFFF);
        for(int iteration=0;iteration<500;iteration++)
        {
            int x0=rng.Next(-70,71),y0=rng.Next(-70,71),x1=rng.Next(-70,71),y1=rng.Next(-70,71);
            var clip=new SpiceRect(rng.Next(0,10),rng.Next(0,8),rng.Next(15,32),rng.Next(12,24));
            surface.Pixels.AsSpan().Clear();bool[] expected=new bool[width*height];
            int x=x0,y=y0,dx=Math.Abs(x1-x0),dy=-Math.Abs(y1-y0),sx=x0<x1?1:-1,sy=y0<y1?1:-1,error=dx+dy;
            while(true)
            {
                if(Contains(clip,x,y))expected[y*width+x]=true;
                if(x==x1&&y==y1)break;
                int twice=2*error;if(twice>=dy){error+=dy;x+=sx;}if(twice<=dx){error+=dx;y+=sy;}
            }
            long work=Raster.LineInRect(surface,x0,y0,x1,y1,clip,white,Ropd.OpPut);
            Assert.InRange(work,1,300);
            for(y=0;y<height;y++)for(x=0;x<width;x++)Assert.True(surface.Row(y)[x]==(expected[y*width+x]?0xFFFFFFFFu:0u),$"seed={seed}, line=({x0},{y0})-({x1},{y1}), pixel={x},{y}");
        }
    }
    [Fact]
    public void ExtremeOffscreenStrokeHasBoundedVisibleWork()
    {
        var s=new Surface(0,31,23,32,true);var white=PixelSource.Solid(0xFFFFFF);
        long work=Raster.LineInRect(s,-134217727,-134217727,134217727,134217727,s.Bounds,white,Ropd.OpPut);
        Assert.InRange(work,1,200);
        for(int y=0;y<23;y++)for(int x=0;x<31;x++)Assert.Equal(x==y?0xFFFFFFFFu:0xFF000000u,s.Row(y)[x]);
    }
    [Fact]
    public void A4GlyphUsesAllSixteenUpstreamCoverageLevels()
    {
        using var c=new Spice.Canvas.Canvas();Create(c,16,1);
        c.Apply(DisplayMsg.DrawText,Bytes(b=>
        {
            b.Write(0u);Rect(b,new(0,0,16,1));b.Write(SpiceClip.TypeNone);b.Write(51u);Rect(b,default);
            b.Write((byte)BrushType.Solid);b.Write(0xFFFFFFu);b.Write((byte)BrushType.None);b.Write(Ropd.OpPut);b.Write((ushort)0);
            b.Write((ushort)1);b.Write((byte)2);b.Write(0);b.Write(0);b.Write(0);b.Write(0);b.Write((ushort)16);b.Write((ushort)1);
            for(int x=0;x<16;x+=2)b.Write((byte)((x<<4)|(x+1)));
        }));
        Assert.Equal(0,c.Errors);for(int x=0;x<16;x++){uint value=(uint)(x*16);Assert.Equal(0xFF000000u|value*0x10101,c.Primary!.Row(0)[x]);}
    }
    [Theory] [InlineData(-1,1)] [InlineData(16384,16384)] [InlineData(int.MaxValue,int.MaxValue)]
    public void DecodedImageBudgetRejectsBeforeAllocation(int w,int h)=>Assert.Throws<CanvasException>(()=>DecodedImage.ByteCount(w,h));
    [Fact]
    public void OversizedSurfaceIsRejectedWithoutAllocatingItsPixels()
    {
        using var c=new Spice.Canvas.Canvas();Create(c,16384,16384);
        Assert.Equal(1,c.Errors);Assert.Null(c.Primary);
    }
}
