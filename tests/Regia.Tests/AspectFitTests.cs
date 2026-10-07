using Regia.Core.Layout;

namespace Regia.Tests;

public sealed class AspectFitTests
{
    [Fact]
    public void StessaProporzione_RiempieIlRiquadro() =>
        Assert.Equal((0, 0, 480, 270), AspectFit.Fit(480, 270, 1920, 1080));

    [Fact]
    public void SorgenteLargaInRiquadroAlto_BandeSopraESotto()
    {
        // 16:9 in un riquadro 300x300: 300x169, centrato in verticale.
        var (x, y, w, h) = AspectFit.Fit(300, 300, 1920, 1080);

        Assert.Equal(0, x);
        Assert.Equal(300, w);
        Assert.Equal(169, h);
        Assert.Equal((300 - 169) / 2, y);
    }

    [Fact]
    public void Sorgente43InRiquadro169_BandeLaterali()
    {
        var (x, y, w, h) = AspectFit.Fit(1600, 900, 1024, 768);

        Assert.Equal(900, h);
        Assert.Equal(1200, w);
        Assert.Equal(200, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void SorgenteQuadrata_BandeLaterali()
    {
        var (x, _, w, h) = AspectFit.Fit(500, 200, 1100, 1100);

        Assert.Equal(200, w);
        Assert.Equal(200, h);
        Assert.Equal(150, x);
    }

    [Theory]
    [InlineData(0, 100, 1920, 1080)]
    [InlineData(100, 0, 1920, 1080)]
    [InlineData(100, 100, 0, 1080)]
    [InlineData(100, 100, 1920, 0)]
    public void DimensioniNonValide_RiquadroIntero(int bw, int bh, int sw, int sh)
    {
        var (x, y, w, h) = AspectFit.Fit(bw, bh, sw, sh);

        Assert.Equal((0, 0), (x, y));
        Assert.Equal((Math.Max(bw, 0), Math.Max(bh, 0)), (w, h));
    }

    [Fact]
    public void MaiOltreIlRiquadro()
    {
        var (x, y, w, h) = AspectFit.Fit(333, 187, 1921, 1079);

        Assert.True(x >= 0 && y >= 0 && x + w <= 333 && y + h <= 187);
    }
}
