using Regia.Core.Media;

namespace Regia.Tests;

public class MediaKindDetectorTests
{
    [Theory]
    [InlineData("a.jpg", MediaKind.Image)]
    [InlineData("A.JPEG", MediaKind.Image)]
    [InlineData("b.png", MediaKind.Image)]
    [InlineData("c.pdf", MediaKind.Pdf)]
    [InlineData("d.MP4", MediaKind.Video)]
    [InlineData("e.pptx", MediaKind.Ppt)]
    [InlineData("f.txt", MediaKind.Unknown)]
    [InlineData("senza_estensione", MediaKind.Unknown)]
    [InlineData("", MediaKind.Unknown)]
    [InlineData(null, MediaKind.Unknown)]
    public void FromPath_DetectsKind(string? path, MediaKind expected)
    {
        Assert.Equal(expected, MediaKindDetector.FromPath(path));
    }
}
