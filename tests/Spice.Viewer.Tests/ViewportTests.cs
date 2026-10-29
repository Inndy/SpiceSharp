namespace SpiceViewer;

public sealed class ViewportTests
{
    static Viewport DirectOf(int w, int h) => new(true, w, h, 0, 0, w, h, w, h);

    [Theory]
    // Fit, aspect matches (scaled up, exact, or rounding within a pixel): direct, guest-sized swap chain.
    [InlineData(1600, 1200, 800, 600)]
    [InlineData(800, 600, 800, 600)]
    [InlineData(400, 300, 800, 600)]
    [InlineData(800, 601, 800, 600)]
    [InlineData(801, 600, 800, 600)]
    [InlineData(1281, 721, 1280, 720)]
    public void FitWithGuestAspectIsDirect(int cw, int ch, int gw, int gh)
        => Assert.Equal(DirectOf(gw, gh), Viewport.Compute(cw, ch, gw, gh, false));

    [Theory]
    // Fit, other aspect: client-sized swap chain, uniformly scaled and centered destination.
    [InlineData(1600, 900, 800, 600, 200, 0, 1200, 900)]
    [InlineData(800, 1000, 800, 600, 0, 200, 800, 600)]
    [InlineData(1920, 1017, 1920, 1080, 56, 0, 1808, 1017)] // maximized above the taskbar
    [InlineData(3840, 300, 800, 600, 1720, 0, 400, 300)]
    public void FitLetterbox(int cw, int ch, int gw, int gh, int x, int y, int w, int h)
        => Assert.Equal(new Viewport(false, cw, ch, x, y, w, h, gw, gh), Viewport.Compute(cw, ch, gw, gh, false));

    [Theory]
    [InlineData(800, 600, 800, 600, true, 0, 0, 800, 600)]       // exactly the guest size: direct
    [InlineData(1000, 800, 800, 600, false, 100, 100, 800, 600)] // larger: centered
    [InlineData(400, 300, 800, 600, false, 0, 0, 800, 600)]      // smaller: cropped right/bottom
    [InlineData(801, 601, 800, 600, false, 0, 0, 800, 600)]      // one pixel larger is not the guest size
    [InlineData(1000, 500, 800, 600, false, 100, 0, 800, 600)]   // mixed: centered across, cropped down
    public void OneToOne(int cw, int ch, int gw, int gh, bool direct, int x, int y, int w, int h)
    {
        var v = Viewport.Compute(cw, ch, gw, gh, true);
        var expected = direct ? DirectOf(gw, gh) : new Viewport(false, cw, ch, x, y, w, h, gw, gh);
        Assert.Equal(expected, v);
        Assert.True(v.Unscaled);
    }

    [Fact]
    public void DegenerateSizesStayValid()
        => Assert.Equal(DirectOf(1, 1), Viewport.Compute(0, 0, 0, 0, false));

    [Theory]
    [InlineData(1600, 1200, false, 0, 0, 1600, 1200)] // direct: the whole client
    [InlineData(801, 600, false, 0, 0, 801, 600)]
    [InlineData(1600, 900, false, 200, 0, 1400, 900)]
    [InlineData(1000, 800, true, 100, 100, 900, 700)]
    [InlineData(400, 300, true, 0, 0, 800, 600)]
    public void GuestRectMatchesWhatIsDrawn(int cw, int ch, bool one, int left, int top, int right, int bottom)
    {
        var r = Viewport.Compute(cw, ch, 800, 600, one).GuestRectInClient(cw, ch, 800, 600);
        Assert.Equal((left, top, right, bottom), (r.left, r.top, r.right, r.bottom));
    }

    [Theory]
    [InlineData(100000, 100, 1920, 1080, false)]
    [InlineData(100, 100000, 1920, 1080, false)]
    [InlineData(100000, 100, 1920, 1080, true)]
    [InlineData(20000, 1000, 800, 600, true)]
    public void DefaultTextureLimitIsRespected(int cw, int ch, int gw, int gh, bool one)
    {
        var v = Viewport.Compute(cw, ch, gw, gh, one);
        Assert.InRange(v.BufferWidth, 1, Viewport.DefaultMaxDimension);
        Assert.InRange(v.BufferHeight, 1, Viewport.DefaultMaxDimension);
    }

    [Fact]
    public void LowerFeatureLevelLimitMustAlsoBoundGuestDimensions()
    {
        // The guest is cropped to the texture limit (documented); every buffer stays valid.
        foreach (bool one in new[] { false, true })
        {
            var v = Viewport.Compute(1920, 1080, 8192, 4096, one, 4096);
            Assert.InRange(v.BufferWidth, 1, 4096); Assert.InRange(v.BufferHeight, 1, 4096);
            Assert.InRange(v.SourceWidth, 1, 4096); Assert.InRange(v.SourceHeight, 1, 4096);
        }
        var d = Viewport.Compute(4096, 2048, 8192, 4096, false, 4096);
        Assert.InRange(d.BufferWidth, 1, 4096); Assert.InRange(d.BufferHeight, 1, 4096);
    }

    [Theory]
    [InlineData(3840, 300, 800, 600, 4096)]
    [InlineData(1600, 900, 800, 600, 16384)]
    [InlineData(5000, 1200, 800, 600, 4096)] // client beyond the limit: buffer shrunk, then stretched back
    [InlineData(1000, 3000, 1920, 1080, 4096)]
    [InlineData(700, 701, 1920, 1080, 16384)]
    public void FitMustPreserveUniformScale(int cw, int ch, int gw, int gh, int maxDim)
    {
        var v = Viewport.Compute(cw, ch, gw, gh, false, maxDim);
        Assert.InRange(v.BufferWidth, 1, maxDim); Assert.InRange(v.BufferHeight, 1, maxDim);
        var r = v.GuestRectInClient(cw, ch, gw, gh);
        double sx = r.Width / (double)gw, sy = r.Height / (double)gh;
        Assert.InRange(sx / sy, 0.99, 1.01);
        // Fit shows the whole guest inside the client, touching two opposite edges.
        Assert.True(r.left >= 0 && r.top >= 0 && r.right <= cw && r.bottom <= ch);
        Assert.True(r.Width >= cw - 1 || r.Height >= ch - 1);
    }

    [Fact]
    public void OneToOneBeyondTextureLimitScalesUniformly()
    {
        // Documented limitation: a letterboxed client larger than a texture can't be exactly 1:1 (the buffer is
        // shrunk and DXGI stretches it back); the scale stays uniform and close to 1.
        var v = Viewport.Compute(20000, 1000, 800, 600, true);
        Assert.False(v.Direct);
        Assert.InRange(v.BufferWidth, 1, Viewport.DefaultMaxDimension);
        var r = v.GuestRectInClient(20000, 1000, 800, 600);
        double sx = r.Width / 800.0, sy = r.Height / 600.0;
        Assert.InRange(sx / sy, 0.99, 1.01);
        Assert.InRange(sx, 0.98, 1.02);
    }
}
