using System.IO;
using Regia.Core.Settings;
using Regia.Core.Show;
using Serilog;

namespace Regia.Output.Ppt;

/// <summary>Esito dell'esportazione del Tappo PowerPoint: cartella delle immagini e numero di slide.</summary>
public sealed record TappoSlidesInfo(string Dir, int Count);

/// <summary>
/// Trasforma un PPT scelto come Tappo in immagini PNG. Si fa UNA VOLTA, quando l'operatore sceglie il file e preme Applica
/// (con la regia a Tappo): dopo, il Tappo usa solo le immagini e PowerPoint non è coinvolto in nessun momento dell'onda.
/// </summary>
public static class TappoSlidesExporter
{
    /// <summary>Le slide già esportate per questo file (stessa dimensione e data), o null se serve esportare.</summary>
    public static TappoSlidesInfo? FindCached(string pptPath, string cacheRoot)
    {
        var file = new FileInfo(pptPath);
        if (!file.Exists)
            return null;

        var dir = TappoSlidesCache.DirectoryFor(cacheRoot, TappoSlidesCache.KeyFor(pptPath, file.Length, file.LastWriteTimeUtc));
        var existing = TappoSlidesCache.ListSlides(dir);
        return existing.Count > 0 ? new TappoSlidesInfo(dir, existing.Count) : null;
    }

    /// <summary>
    /// Se la cartella per questo file esiste già (stessa dimensione e data) la riusa, altrimenti copia il file in locale,
    /// lo fa esportare da PptHost in una cartella provvisoria e la rende definitiva. Toglie le esportazioni vecchie.
    /// </summary>
    public static async Task<TappoSlidesInfo> ExportAsync(
        PptHostClient client, string pptPath, string cacheRoot, int maxWidth, int maxHeight, CancellationToken cancellationToken)
    {
        var file = new FileInfo(pptPath);
        if (!file.Exists)
            throw new FileNotFoundException("File non trovato: " + pptPath);

        var key = TappoSlidesCache.KeyFor(pptPath, file.Length, file.LastWriteTimeUtc);
        var finalDir = TappoSlidesCache.DirectoryFor(cacheRoot, key);

        if (FindCached(pptPath, cacheRoot) is { } cached)
        {
            Log.Information("Tappo PowerPoint: slide già esportate ({Count}) in {Dir}", cached.Count, cached.Dir);
            return cached;
        }

        var root = Path.Combine(cacheRoot, TappoSlidesCache.FolderName);
        var work = Path.Combine(root, "work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // Copia locale a flusso (niente Mark-of-the-Web, mai leggere a lungo da chiavetta o rete), poi PowerPoint lavora sulla copia.
            var local = Path.Combine(work, "tappo" + Path.GetExtension(pptPath));
            var part = await FileCopier.CopyToPartAsync(pptPath, local, null, cancellationToken);
            FileCopier.Commit(part, local);

            var images = Path.Combine(work, "slides");
            var result = await client.ExportSlidesAsync(local, images, maxWidth, maxHeight, cancellationToken);
            var written = TappoSlidesCache.ListSlides(images);
            if (written.Count == 0 || written.Count != result.Exported)
                throw new InvalidOperationException("PowerPoint non ha prodotto le immagini attese.");

            Directory.CreateDirectory(root);
            if (Directory.Exists(finalDir))
                Directory.Delete(finalDir, recursive: true);
            Directory.Move(images, finalDir);

            Prune(root, keep: key);
            return new TappoSlidesInfo(finalDir, written.Count);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Cartella di lavoro del Tappo non rimossa: {Dir}", work);
            }
        }
    }

    /// <summary>Toglie le esportazioni di altri file (un solo Tappo per evento).</summary>
    private static void Prune(string root, string keep)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!string.Equals(name, keep, StringComparison.OrdinalIgnoreCase) && !name.StartsWith("work-", StringComparison.Ordinal))
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Esportazioni vecchie del Tappo non rimosse");
        }
    }
}
