using System.IO.Compression;
using System.Text;
using Regia.Core.Preflight;

namespace Regia.Tests;

public sealed class PptxInspectorTests
{
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static MemoryStream BuildPptx(
        int slides = 3,
        long cx = 12192000,
        long cy = 6858000,
        string[]? slideFonts = null,
        string[]? embeddedFonts = null,
        (string Type, string Target, bool External)[]? slideLinks = null,
        byte[]? thumbnail = null)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var sldIds = string.Concat(Enumerable.Range(1, slides).Select(i => $"<p:sldId id=\"{255 + i}\" r:id=\"rId{i}\"/>"));
            var embedded = embeddedFonts is null
                ? ""
                : "<p:embeddedFontLst>" + string.Concat(embeddedFonts.Select(f => $"<p:embeddedFont><p:font typeface=\"{f}\"/></p:embeddedFont>")) + "</p:embeddedFontLst>";

            Add(zip, "ppt/presentation.xml",
                $"<p:presentation xmlns:p=\"{NsP}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                $"<p:sldMasterIdLst/><p:sldIdLst>{sldIds}</p:sldIdLst><p:sldSz cx=\"{cx}\" cy=\"{cy}\"/>{embedded}</p:presentation>");

            Add(zip, "ppt/theme/theme1.xml",
                $"<a:theme xmlns:a=\"{NsA}\"><a:themeElements><a:fontScheme name=\"x\">" +
                "<a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/></a:majorFont>" +
                "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/></a:minorFont>" +
                "</a:fontScheme></a:themeElements></a:theme>");

            var runs = string.Concat((slideFonts ?? []).Select(f => $"<a:r><a:rPr><a:latin typeface=\"{f}\"/></a:rPr><a:t>x</a:t></a:r>"));
            Add(zip, "ppt/slides/slide1.xml",
                $"<p:sld xmlns:p=\"{NsP}\" xmlns:a=\"{NsA}\"><p:cSld><p:spTree><a:p>{runs}<a:r><a:rPr><a:latin typeface=\"+mn-lt\"/></a:rPr></a:r></a:p></p:spTree></p:cSld></p:sld>");

            if (slideLinks is not null)
            {
                var rels = string.Concat(slideLinks.Select((l, i) =>
                    $"<Relationship Id=\"rId{i + 10}\" Type=\"{l.Type}\" Target=\"{l.Target}\"" + (l.External ? " TargetMode=\"External\"" : "") + "/>"));
                Add(zip, "ppt/slides/_rels/slide1.xml.rels", $"<Relationships xmlns=\"{NsR}\">{rels}</Relationships>");
            }

            if (thumbnail is not null)
            {
                var entry = zip.CreateEntry("docProps/thumbnail.jpeg");
                using var s = entry.Open();
                s.Write(thumbnail);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }

    private static PptxInfo Inspect(MemoryStream ms, Func<string, bool>? exists = null) =>
        PptxInspector.Inspect(ms, @"C:\show", exists ?? (_ => false));

    [Fact]
    public void ContaSlideEDimensione_16_9()
    {
        var info = Inspect(BuildPptx(slides: 5));

        Assert.Equal(5, info.Slides);
        Assert.Equal("16:9", info.AspectText);
    }

    [Fact]
    public void Aspect_4_3()
    {
        var info = Inspect(BuildPptx(cx: 9144000, cy: 6858000));

        Assert.Equal("4:3", info.AspectText);
    }

    [Fact]
    public void Font_TemaESlide_SenzaRiferimentiAlTemaNeIncorporati()
    {
        var info = Inspect(BuildPptx(slideFonts: ["Gotham", "Arial", "FontIncorporato"], embeddedFonts: ["FontIncorporato"]));

        Assert.Equal(["Arial", "Calibri", "Calibri Light", "Gotham"], info.Fonts);
    }

    [Fact]
    public void Collegamenti_SoloFileMancanti_NonIPagineWeb()
    {
        var info = Inspect(BuildPptx(slideLinks:
        [
            ("http://schemas.openxmlformats.org/officeDocument/2006/relationships/video", "file:///C:/media/filmato.mp4", true),
            ("http://schemas.openxmlformats.org/officeDocument/2006/relationships/audio", "audio%20relativo.wav", true),
            ("http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", "https://example.com", true),
            ("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", "../media/image1.png", false),
            ("http://schemas.openxmlformats.org/officeDocument/2006/relationships/video", "C:\\esiste\\ok.mp4", true)
        ]), exists: path => path.EndsWith("ok.mp4", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["audio relativo.wav", "filmato.mp4"], info.MissingLinks);
    }

    [Fact]
    public void Collegamento_RelativoSiRisolveRispettoAllaCartellaDelFile()
    {
        string? asked = null;
        Inspect(BuildPptx(slideLinks: [("http://x/video", "sub/clip.mp4", true)]), p => { asked = p; return true; });

        Assert.Equal(@"C:\show\sub\clip.mp4", asked);
    }

    [Fact]
    public void Miniatura_PresenteOAssente()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };

        Assert.Equal(bytes, Inspect(BuildPptx(thumbnail: bytes)).Thumbnail);
        Assert.Null(Inspect(BuildPptx()).Thumbnail);
    }

    [Fact]
    public void FileNonPptx_SollevaInvalidDataException()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            Add(zip, "altro.txt", "ciao");
        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => Inspect(ms));
    }

    [Fact]
    public void FileCorrotto_SollevaInvalidDataException()
    {
        var ms = new MemoryStream(Encoding.UTF8.GetBytes("questo non è uno zip"));

        Assert.Throws<InvalidDataException>(() => Inspect(ms));
    }
}
