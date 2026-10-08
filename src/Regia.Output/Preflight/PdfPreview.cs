using System.IO;
using System.Windows.Media.Imaging;
using Serilog;
using Windows.Data.Pdf;
using Windows.Storage;

namespace Regia.Output.Preflight;

/// <summary>Miniature delle pagine di un PDF per la Preview (solo anteprima, mai per l'onda).</summary>
public static class PdfPreview
{
    /// <summary>Pagine mostrate al massimo nella striscia: un PDF da 500 pagine non deve riempire la memoria.</summary>
    public const int MaxPages = 120;

    /// <summary>Una sola pagina (indice 1-based) alla larghezza richiesta, per la Preview grande. Null se non si rende.</summary>
    public static async Task<BitmapSource?> RenderPageAsync(string path, int pageNumber, int width, CancellationToken token)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
            var document = await PdfDocument.LoadFromFileAsync(file).AsTask(token);
            if (document.IsPasswordProtected || pageNumber < 1 || pageNumber > document.PageCount)
                return null;

            var jpeg = await FileChecks.RenderPdfPageAsync(document, pageNumber - 1, width, token);
            using var stream = new MemoryStream(jpeg);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pagina {Page} del PDF non renderizzabile in anteprima: {Path}", pageNumber, path);
            return null;
        }
    }

    /// <summary>
    /// Renderizza le pagine una alla volta (dalla prima) e le consegna a <paramref name="onPage"/> (da thread di
    /// background). Si ferma al token o alla pagina <see cref="MaxPages"/>. Non solleva eccezioni oltre
    /// all'annullamento: una pagina che non si rende viene saltata.
    /// </summary>
    public static async Task RenderAsync(string path, int width, Action<int, BitmapSource> onPage, CancellationToken token)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
            var document = await PdfDocument.LoadFromFileAsync(file).AsTask(token);
            if (document.IsPasswordProtected)
                return;

            var count = (int)Math.Min(document.PageCount, MaxPages);
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var jpeg = await FileChecks.RenderPdfPageAsync(document, i, width, token);
                    using var stream = new MemoryStream(jpeg);
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    onPage(i + 1, frame);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug(ex, "Miniatura della pagina {Page} non disponibile", i + 1);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Anteprima delle pagine del PDF non riuscita: {Path}", path);
        }
    }
}
