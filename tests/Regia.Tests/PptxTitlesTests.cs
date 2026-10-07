using System.IO.Compression;
using System.Text;
using Regia.Core.Preflight;

namespace Regia.Tests;

public sealed class PptxTitlesTests
{
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string NsRDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Una slide: titolo (null = nessun segnaposto titolo), nascosta, e file nel pacchetto.</summary>
    private sealed record Slide(string? TitleXml, bool Hidden = false, string File = "slide1.xml", string RId = "rId1");

    private static string Title(string text, string type = "title") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"T\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"{type}\"/></p:nvPr></p:nvSpPr>" +
        $"<p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";

    private static MemoryStream Build(IReadOnlyList<Slide> slides, bool withRels = true)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // L'ordine dello slideshow è quello di sldIdLst, NON quello dei nomi dei file.
            var ids = string.Concat(slides.Select((s, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"{s.RId}\"/>"));
            Add(zip, "ppt/presentation.xml",
                $"<p:presentation xmlns:p=\"{NsP}\" xmlns:r=\"{NsRDoc}\"><p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/></p:presentation>");

            if (withRels)
            {
                var rels = string.Concat(slides.Select(s => $"<Relationship Id=\"{s.RId}\" Type=\"x/slide\" Target=\"slides/{s.File}\"/>"));
                Add(zip, "ppt/_rels/presentation.xml.rels", $"<Relationships xmlns=\"{NsR}\">{rels}</Relationships>");
            }

            foreach (var s in slides)
            {
                var show = s.Hidden ? " show=\"0\"" : "";
                Add(zip, "ppt/slides/" + s.File,
                    $"<p:sld xmlns:p=\"{NsP}\" xmlns:a=\"{NsA}\"{show}><p:cSld><p:spTree>{s.TitleXml}</p:spTree></p:cSld></p:sld>");
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

    private static PptxInfo Inspect(MemoryStream ms) => PptxInspector.Inspect(ms, @"C:\show", _ => false);

    [Fact]
    public void Titoli_NellOrdineDelloSlideshow()
    {
        var info = Inspect(Build(
        [
            new Slide(Title("Benvenuti"), File: "slide1.xml", RId: "rId1"),
            new Slide(Title("Risultati 2025"), File: "slide2.xml", RId: "rId2"),
            new Slide(Title("Conclusioni"), File: "slide3.xml", RId: "rId3")
        ]));

        Assert.Equal(["Benvenuti", "Risultati 2025", "Conclusioni"], info.SlideTitles);
        Assert.Equal(0, info.HiddenSlides);
    }

    [Fact]
    public void Titoli_SeguonoSldIdLst_NonIlNomeDelFile()
    {
        // Slide riordinate in PowerPoint: slide3.xml è la prima dello show.
        var info = Inspect(Build(
        [
            new Slide(Title("Terza nel file, prima in onda"), File: "slide3.xml", RId: "rId7"),
            new Slide(Title("Prima nel file, seconda in onda"), File: "slide1.xml", RId: "rId8")
        ]));

        Assert.Equal(["Terza nel file, prima in onda", "Prima nel file, seconda in onda"], info.SlideTitles);
    }

    [Fact]
    public void SlideNascoste_RestanoNellaNumerazione_ConIlLoroNumero()
    {
        var info = Inspect(Build(
        [
            new Slide(Title("Uno"), File: "slide1.xml", RId: "rId1"),
            new Slide(Title("Nascosta"), Hidden: true, File: "slide2.xml", RId: "rId2"),
            new Slide(Title("Tre"), File: "slide3.xml", RId: "rId3")
        ]));

        // PowerPoint numera per posizione nel file anche con nascoste: i titoli seguono lo stesso indice.
        Assert.Equal(["Uno", "Nascosta", "Tre"], info.SlideTitles);
        Assert.Equal([2], info.HiddenSlideNumbers);
        Assert.Equal(1, info.HiddenSlides);
        Assert.Equal(3, info.Slides);
    }

    [Fact]
    public void SlideSenzaTitolo_StringaVuota_SenzaSpostareLeAltre()
    {
        var info = Inspect(Build(
        [
            new Slide(Title("Uno"), File: "slide1.xml", RId: "rId1"),
            new Slide(null, File: "slide2.xml", RId: "rId2"),
            new Slide(Title("Tre"), File: "slide3.xml", RId: "rId3")
        ]));

        Assert.Equal(["Uno", "", "Tre"], info.SlideTitles);
    }

    [Fact]
    public void TitoloCentrato_ctrTitle_EAcapoSuUnaRiga()
    {
        var xml = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"T\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"ctrTitle\"/></p:nvPr></p:nvSpPr>" +
                  "<p:txBody><a:p><a:r><a:t>Congresso</a:t></a:r><a:br/><a:r><a:t>Nazionale</a:t></a:r></a:p>" +
                  "<a:p><a:r><a:t>  2026 </a:t></a:r></a:p></p:txBody></p:sp>";

        var info = Inspect(Build([new Slide(xml)]));

        Assert.Equal(["Congresso Nazionale 2026"], info.SlideTitles);
    }

    [Fact]
    public void TestoDiUnaCasellaNonTitolo_Ignorato()
    {
        var info = Inspect(Build([new Slide(Title("Sottotitolo", type: "body"))]));

        Assert.Equal([""], info.SlideTitles);
    }

    [Fact]
    public void TitoloTroppoLungo_Troncato()
    {
        var info = Inspect(Build([new Slide(Title(new string('x', 400)))]));

        var title = Assert.Single(info.SlideTitles);
        Assert.True(title.Length <= 120);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void SenzaRelazioni_NessunTitolo_MaIlPreflightNonFallisce()
    {
        var info = Inspect(Build([new Slide(Title("Uno"))], withRels: false));

        Assert.Empty(info.SlideTitles);
        Assert.Equal(1, info.Slides);
    }

    [Fact]
    public void RelazioneMancante_NessunTitoloPiuttostoCheTitoliSbagliati()
    {
        var info = Inspect(Build(
        [
            new Slide(Title("Uno"), File: "slide1.xml", RId: "rId1"),
            new Slide(Title("Due"), File: "slide2.xml", RId: "rId1") // stesso rId due volte: relazioni ambigue
        ]));

        Assert.Empty(info.SlideTitles);
        Assert.Equal(2, info.Slides);
    }
}
