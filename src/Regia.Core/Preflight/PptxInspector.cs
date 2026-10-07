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

        return new PptxInfo(slides, width, height, fonts.ToList(), missing.ToList(), thumbnail);
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
