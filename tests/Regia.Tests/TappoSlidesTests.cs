using System.IO;
using Regia.Core.Ppt;
using Regia.Core.Settings;

namespace Regia.Tests;

public class TappoSlidesTests
{
    [Theory]
    [InlineData(1, 1, 5, 2)]
    [InlineData(5, 1, 5, 5)]    // ultima slide: si ferma, nessun giro
    [InlineData(1, -1, 5, 1)]   // prima slide: si ferma
    [InlineData(3, -1, 5, 2)]
    [InlineData(1, 1, 0, 1)]    // nessuna slide
    public void Step_StopsAtEdges(int current, int delta, int count, int expected)
    {
        Assert.Equal(expected, TappoSlidesCache.Step(current, delta, count));
    }

    [Fact]
    public void KeyFor_ChangesWithFileAndIsStable()
    {
        var when = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var a = TappoSlidesCache.KeyFor(@"C:\x\tappo.pptx", 1000, when);

        Assert.Equal(a, TappoSlidesCache.KeyFor(@"C:\X\TAPPO.pptx", 1000, when));   // maiuscole ininfluenti
        Assert.NotEqual(a, TappoSlidesCache.KeyFor(@"C:\x\tappo.pptx", 1001, when));
        Assert.NotEqual(a, TappoSlidesCache.KeyFor(@"C:\x\tappo.pptx", 1000, when.AddSeconds(1)));
    }

    [Fact]
    public void ListSlides_ReturnsOrderedPngs_AndEmptyWhenMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tappo-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(TappoSlidesCache.ListSlides(dir));

            Directory.CreateDirectory(dir);
            foreach (var n in new[] { 10, 2, 1 })
                File.WriteAllText(Path.Combine(dir, TappoSlidesCache.SlideFileName(n)), "x");
            File.WriteAllText(Path.Combine(dir, "altro.txt"), "x");

            var names = TappoSlidesCache.ListSlides(dir).Select(Path.GetFileName).ToArray();
            Assert.Equal(["slide-001.png", "slide-002.png", "slide-010.png"], names);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Normalized_ClampsIndexAndSeconds()
    {
        var tappo = new TappoSettings { Kind = TappoKind.Slides, SlideCount = 8, SlideIndex = 99, SlideSeconds = 0 }.Normalized();

        Assert.Equal(8, tappo.SlideIndex);
        Assert.Equal(TappoSettings.MinSlideSeconds, tappo.SlideSeconds);
        Assert.Equal(1, new TappoSettings { SlideIndex = -5 }.Normalized().SlideIndex);
    }

    [Fact]
    public void ImagePaths_Normalized_PulisceVuoteEDoppioniEAllineaPath()
    {
        var tappo = new TappoSettings
        {
            Kind = TappoKind.Image,
            Path = @"C:\vecchia.png",
            ImagePaths = [@" C:\img1.png ", "", "  ", @"C:\IMG1.PNG", @"C:\img2.png"]
        }.Normalized();

        Assert.Equal([@"C:\img1.png", @"C:\img2.png"], tappo.ImagePaths);
        Assert.Equal(@"C:\img1.png", tappo.Path);
        Assert.Equal(tappo.ImagePaths, tappo.EffectiveImages);
    }

    [Fact]
    public void ImagePaths_VecchioShow_UsaSoloPath()
    {
        var tappo = new TappoSettings { Kind = TappoKind.Image, Path = @"C:\tappo.png" }.Normalized();

        Assert.Empty(tappo.ImagePaths);
        Assert.Equal([@"C:\tappo.png"], tappo.EffectiveImages);
        Assert.Empty(new TappoSettings().Normalized().EffectiveImages);
    }

    [Fact]
    public void ImagePaths_SalvataggioERilettura_ShowStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tappo-img-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Regia.Core.Show.ShowStore(Path.Combine(dir, "show.json"));
            var show = new Regia.Core.Show.ShowDocument
            {
                Settings = new AppSettings
                {
                    Tappo = new TappoSettings { Kind = TappoKind.Image, ImagePaths = [@"C:\img1.png", @"C:\img2.png"], SlideSeconds = 4 }
                }
            };
            store.Save(show);

            var loaded = store.Load().Settings.Tappo;
            Assert.Equal([@"C:\img1.png", @"C:\img2.png"], loaded.ImagePaths);
            Assert.Equal(4, loaded.SlideSeconds);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImageLoopEffective_FissaOLoop()
    {
        var due = new[] { @"C:\img1.png", @"C:\img2.png" };

        // Show della 1.2.0 (nessun campo): due o più immagini = loop.
        Assert.True(new TappoSettings { ImagePaths = due }.ImageLoopEffective);
        // Scelta esplicita "Fissa": resta una sola, anche se la lista ne avesse di più.
        Assert.False(new TappoSettings { ImagePaths = due, ImageLoop = false }.ImageLoopEffective);
        // Loop con una sola immagine = fissa.
        Assert.False(new TappoSettings { ImagePaths = [@"C:\img1.png"], ImageLoop = true }.ImageLoopEffective);
        Assert.False(new TappoSettings().ImageLoopEffective);
    }

    [Fact]
    public void Defaults_AreLoopSixSeconds()
    {
        var tappo = new TappoSettings();

        Assert.Equal(TappoSlidesMode.Loop, tappo.SlidesMode);
        Assert.Equal(6, tappo.SlideSeconds);
        Assert.Equal(1, tappo.SlideIndex);
    }

    [Fact]
    public void ExportSlides_ProtocolRoundTrips()
    {
        var line = PptProtocol.Serialize(PptProtocol.Success(3, new ExportSlidesResult(7, 8, 1920, 1080)));
        Assert.True(PptProtocol.TryParse(line, out var message));

        Assert.Equal(new ExportSlidesResult(7, 8, 1920, 1080), PptProtocol.ReadData<ExportSlidesResult>(message));
    }
}
