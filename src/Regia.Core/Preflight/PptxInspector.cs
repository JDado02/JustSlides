using System.IO.Compression;
using System.Xml.Linq;

namespace Regia.Core.Preflight;

/// <summary>Cosa si legge dentro un .pptx senza aprire PowerPoint.</summary>
public sealed record PptxInfo(
    int Slides,
    double WidthEmu,
    double HeightEmu,
    IReadOnlyList<string> Fonts,
    IReadOnlyList<string> MissingLinks,
    byte[]? Thumbnail)
{
    /// <summary>
    /// Titolo di OGNI slide, nell'ordine del file (indice 0 = slide 1; PowerPoint numera così anche con slide nascoste);
    /// vuoto dove la slide non ha un titolo. Lista vuota se non leggibile: la regia mostra solo il numero.
    /// </summary>
    public IReadOnlyList<string> SlideTitles { get; init; } = [];

    /// <summary>Numeri (1-based) delle slide nascoste: PowerPoint le salta e non vanno in onda.</summary>
    public IReadOnlyList<int> HiddenSlideNumbers { get; init; } = [];

    /// <summary>Quante slide sono nascoste.</summary>
    public int HiddenSlides => HiddenSlideNumbers.Count;

    /// <summary>Rapporto larghezza/altezza (0 se sconosciuto).</summary>
    public double AspectRatio => HeightEmu > 0 ? WidthEmu / HeightEmu : 0;

    /// <summary>"16:9", "4:3" o "1,60:1".</summary>
    public string AspectText => AspectRatio switch
    {
        0 => "?",
        var a when Math.Abs(a - 16.0 / 9) < 0.02 => "16:9",
        var a when Math.Abs(a - 4.0 / 3) < 0.02 => "4:3",
        var a when Math.Abs(a - 16.0 / 10) < 0.02 => "16:10",
        var a => $"{a:0.00}:1"
    };
}

/// <summary>
/// Legge il pacchetto OOXML (.pptx/.ppsx) come ZIP: numero di slide, dimensione, font usati (esclusi quelli
/// incorporati), collegamenti esterni mancanti e miniatura incorporata. Logica pura: l'esistenza dei file
/// collegati passa da <c>fileExists</c>, così si prova senza disco.
/// </summary>
public static class PptxInspector
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace RDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <exception cref="InvalidDataException">Il file non è un pacchetto PowerPoint valido.</exception>
    public static PptxInfo Inspect(string path, Func<string, bool>? fileExists = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Inspect(stream, Path.GetDirectoryName(path) ?? "", fileExists ?? File.Exists);
    }

    public static PptxInfo Inspect(Stream stream, string baseDirectory, Func<string, bool> fileExists)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var presentation = Load(zip, "ppt/presentation.xml")
            ?? throw new InvalidDataException("Manca ppt/presentation.xml: non è una presentazione PowerPoint.");

        var slides = presentation.Descendants(P + "sldId").Count();
        var size = presentation.Descendants(P + "sldSz").FirstOrDefault();
        var width = (double?)size?.Attribute("cx") ?? 0;
        var height = (double?)size?.Attribute("cy") ?? 0;

        var embedded = presentation.Descendants(P + "embeddedFont")
            .SelectMany(e => e.Elements(P + "font"))
            .Select(f => (string?)f.Attribute("typeface"))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(e => IsFontSource(e.FullName)))
        {
            var doc = Load(entry);
            if (doc is null)
                continue;

            foreach (var element in doc.Descendants().Where(e => e.Name.Namespace == A && e.Name.LocalName is "latin" or "ea" or "cs"))
            {
                var name = (string?)element.Attribute("typeface");

                // "+mj-lt" e simili rimandano al tema (già contato); vuoto = ereditato.
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('+') && !embedded.Contains(name))
                    fonts.Add(name);
            }
        }

        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(e =>
                     e.FullName.StartsWith("ppt/slides/_rels/", StringComparison.OrdinalIgnoreCase) &&
                     e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var rels = Load(entry);
            if (rels is null)
                continue;

            foreach (var rel in rels.Descendants(R + "Relationship"))
            {
                if (!string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                    continue;

                var type = (string?)rel.Attribute("Type") ?? "";
                var target = (string?)rel.Attribute("Target") ?? "";
                if (type.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase) || target.Length == 0 || IsWebOrMail(target))
                    continue;

                var resolved = Resolve(target, baseDirectory);
                if (resolved is not null && !fileExists(resolved))
                    missing.Add(Path.GetFileName(resolved));
            }
        }

        byte[]? thumbnail = null;
        var thumbEntry = zip.GetEntry("docProps/thumbnail.jpeg") ?? zip.GetEntry("docProps/thumbnail.jpg") ?? zip.GetEntry("docProps/thumbnail.png");
        if (thumbEntry is { Length: > 0 and < 8_000_000 })
        {
            using var s = thumbEntry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            thumbnail = ms.ToArray();
        }

        var (titles, hidden) = ReadTitles(zip, presentation);

        return new PptxInfo(slides, width, height, fonts.ToList(), missing.ToList(), thumbnail)
        {
            SlideTitles = titles,
            HiddenSlideNumbers = hidden
        };
    }

    private const int MaxTitleLength = 120;

    /// <summary>
    /// Titoli di tutte le slide e numeri delle nascoste, nell'ordine di <c>sldIdLst</c>. Solo un di più per l'operatore:
    /// qualunque cosa non torni (relazioni mancanti, XML anomalo) dà liste vuote, mai un errore del pre-flight.
    /// </summary>
    private static (IReadOnlyList<string> Titles, IReadOnlyList<int> Hidden) ReadTitles(ZipArchive zip, XDocument presentation)
    {
        try
        {
            var rels = Load(zip, "ppt/_rels/presentation.xml.rels");
            if (rels is null)
                return ([], []);

            var targets = new Dictionary<string, string>();
            foreach (var rel in rels.Descendants(R + "Relationship"))
            {
                var relId = (string?)rel.Attribute("Id");
                var target = (string?)rel.Attribute("Target");
                if (relId is null || target is null)
                    continue;

                if (!targets.TryAdd(relId, target))
                    return ([], []); // relazioni ambigue: meglio nessun titolo che titoli sulle slide sbagliate
            }

            var titles = new List<string>();
            var hidden = new List<int>();
            foreach (var id in presentation.Descendants(P + "sldId").Select(s => (string?)s.Attribute(RDoc + "id")))
            {
                if (id is null || !targets.TryGetValue(id, out var slideTarget))
                    return ([], []); // ordine non ricostruibile: meglio nessun titolo che titoli sulle slide sbagliate

                var name = slideTarget.StartsWith('/') ? slideTarget.TrimStart('/') : "ppt/" + slideTarget;
                var slide = Load(zip, name);
                if (slide?.Root is null)
                    return ([], []);

                titles.Add(TitleOf(slide));
                if ((string?)slide.Root.Attribute("show") == "0")
                    hidden.Add(titles.Count);
            }

            return (titles, hidden);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
        {
            return ([], []);
        }
    }

    /// <summary>Testo del segnaposto titolo (title / ctrTitle) di una slide, su una riga; vuoto se non c'è.</summary>
    private static string TitleOf(XDocument slide)
    {
        foreach (var shape in slide.Descendants(P + "sp"))
        {
            var type = (string?)shape.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type");
            if (type is not ("title" or "ctrTitle"))
                continue;

            var text = new System.Text.StringBuilder();
            foreach (var part in shape.Descendants().Where(e => e.Name == A + "t" || e.Name == A + "br" || e.Name == A + "p"))
            {
                // Paragrafi e a-capo diventano uno spazio: il titolo sta su una riga.
                if (part.Name == A + "t")
                    text.Append(part.Value);
                else
                    text.Append(' ');
            }

            var line = string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return line.Length > MaxTitleLength ? line[..(MaxTitleLength - 1)] + "…" : line;
        }

        return "";
    }

    private static bool IsFontSource(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
        (name.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("ppt/slideLayouts/", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("ppt/slideMasters/", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("ppt/theme/", StringComparison.OrdinalIgnoreCase)) &&
        !name.Contains("/_rels/", StringComparison.OrdinalIgnoreCase);

    private static bool IsWebOrMail(string target) =>
        target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Percorso assoluto di un collegamento esterno (file:///, percorso assoluto o relativo alla cartella del file).</summary>
    private static string? Resolve(string target, string baseDirectory)
    {
        try
        {
            if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return new Uri(target).LocalPath;

            var decoded = Uri.UnescapeDataString(target).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(decoded) ? decoded : Path.Combine(baseDirectory, decoded));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static XDocument? Load(ZipArchive zip, string name) =>
        zip.GetEntry(name) is { } entry ? Load(entry) : null;

    private static XDocument? Load(ZipArchiveEntry entry)
    {
        try
        {
            using var s = entry.Open();
            return XDocument.Load(s);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
