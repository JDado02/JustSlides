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
