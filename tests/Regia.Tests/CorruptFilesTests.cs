using System.IO.Compression;
using System.Text;
using Regia.Core.Media;
using Regia.Core.Preflight;

namespace Regia.Tests;

/// <summary>File rotti, troncati o travestiti: il pre-flight deve rispondere con un errore chiaro, mai con un'eccezione inattesa.</summary>
public class CorruptFilesTests
{
    private const string PresentationXml =
        "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"5143500\"/></p:presentation>";

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    private static PptxInfo Inspect(byte[] data) =>
        PptxInspector.Inspect(new MemoryStream(data), @"C:\nessuna", _ => true);

    /// <summary>L'unica eccezione ammessa dal contratto di <see cref="PptxInspector"/> è <see cref="InvalidDataException"/>.</summary>
    private static void AssertInvalidOrReadable(byte[] data)
    {
        try
        {
            Inspect(data);
        }
        catch (InvalidDataException)
        {
            // Atteso: file non valido, il pre-flight lo segna in rosso.
        }
    }

    [Fact]
    public void EmptyFileIsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => Inspect([]));

    [Fact]
    public void RandomBytesAreInvalidData()
    {
        var data = new byte[4096];
        new Random(1).NextBytes(data);

        Assert.Throws<InvalidDataException>(() => Inspect(data));
    }

    [Fact]
    public void ExecutableRenamedAsPptxIsInvalidData()
    {
        // Intestazione di un eseguibile Windows ("MZ") seguita da rumore.
        var data = new byte[2048];
        new Random(2).NextBytes(data);
        data[0] = (byte)'M';
        data[1] = (byte)'Z';

        Assert.Throws<InvalidDataException>(() => Inspect(data));
    }

    [Fact]
    public void ZipWithoutPresentationIsInvalidData()
    {
        var data = BuildZip(("readme.txt", "ciao"));

        Assert.Throws<InvalidDataException>(() => Inspect(data));
    }

    [Fact]
    public void BrokenPresentationXmlIsInvalidData()
    {
        var data = BuildZip(("ppt/presentation.xml", "<p:presentation><non chiuso"));

        Assert.Throws<InvalidDataException>(() => Inspect(data));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(90)]
    [InlineData(99)]
    public void TruncatedPackageNeverThrowsUnexpectedExceptions(int percent)
    {
        var full = BuildZip(
            ("ppt/presentation.xml", PresentationXml),
            ("ppt/slides/slide1.xml", "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>"));

        AssertInvalidOrReadable(full[..(full.Length * percent / 100)]);
    }

    [Fact]
    public void BrokenSlideXmlStillGivesSlideCountWithoutTitles()
    {
        var data = BuildZip(
            ("ppt/presentation.xml", PresentationXml),
            ("ppt/_rels/presentation.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"x/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", "<p:sld><rotto"));

        var info = Inspect(data);

        Assert.Equal(1, info.Slides);
        Assert.Empty(info.SlideTitles);
    }

    [Fact]
    public void PresentationWithoutSizeHasUnknownAspect()
    {
        var data = BuildZip(("ppt/presentation.xml",
            "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>"));

        var info = Inspect(data);

        Assert.Equal(0, info.Slides);
        Assert.Equal("?", info.AspectText);
    }

    [Fact]
    public void ExternalLinkWithAbsurdTargetDoesNotThrow()
    {
        var data = BuildZip(
            ("ppt/presentation.xml", PresentationXml),
            ("ppt/slides/_rels/slide1.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"x/video\" Target=\"file:///\" TargetMode=\"External\"/>" +
                "<Relationship Id=\"rId2\" Type=\"x/video\" Target=\"&lt;&gt;|?*:.mp4\" TargetMode=\"External\"/>" +
                "</Relationships>"));

        // Nessuna eccezione: i percorsi illeggibili si ignorano.
        Assert.NotNull(Inspect(data));
    }

    [Theory]
    [InlineData(null, MediaKind.Unknown)]
    [InlineData("", MediaKind.Unknown)]
    [InlineData("senza_estensione", MediaKind.Unknown)]
    [InlineData("file.", MediaKind.Unknown)]
    [InlineData(".mp4", MediaKind.Video)]
    [InlineData("FOTO.JPG", MediaKind.Image)]
    [InlineData("Relazione.PDF", MediaKind.Pdf)]
    [InlineData("a.b.c.MP4", MediaKind.Video)]
    [InlineData("slide.PPTX", MediaKind.Ppt)]
    [InlineData("virus.exe", MediaKind.Unknown)]
    [InlineData("doppia.pdf.exe", MediaKind.Unknown)]
    [InlineData("nota.txt", MediaKind.Unknown)]
    public void KindDetectionIsRobustToOddNames(string? path, MediaKind expected) =>
        Assert.Equal(expected, MediaKindDetector.FromPath(path));
}
