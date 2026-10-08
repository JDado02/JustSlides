using System.Security.Cryptography;
using System.Text;

namespace Regia.Core.Settings;

/// <summary>
/// Cartella delle immagini del Tappo PowerPoint (<c>slide-001.png</c>...). Logica pura: la chiave dipende dal file sorgente
/// (percorso, dimensione, data), così un PPT modificato si riesporta e uno uguale no.
/// </summary>
public static class TappoSlidesCache
{
    public const string FolderName = ".tappo";

    public static string SlideFileName(int number) => $"slide-{number:000}.png";

    public static string KeyFor(string path, long length, DateTime lastWriteUtc)
    {
        var text = $"{Path.GetFullPath(path).ToLowerInvariant()}|{length}|{lastWriteUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    public static string DirectoryFor(string cacheRoot, string key) => Path.Combine(cacheRoot, FolderName, key);

    /// <summary>Le slide presenti nella cartella, in ordine; vuoto se la cartella non esiste.</summary>
    public static IReadOnlyList<string> ListSlides(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir, "slide-*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Slide successiva/precedente a quella corrente, ai bordi si ferma (nessun giro).</summary>
    public static int Step(int current, int delta, int count) => count <= 0 ? 1 : Math.Clamp(current + delta, 1, count);
}
