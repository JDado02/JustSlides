using System.IO;
using Serilog;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Regia.Output.Content;

/// <summary>
/// Pre-rendering in background delle pagine di un PDF alla risoluzione dell'output. Un solo worker
/// renderizza le pagine in ordine; la pagina richiesta dall'operatore passa in testa alla coda.
/// Le pagine sono tenute come PNG compressi (poche centinaia di KB l'una).
/// </summary>
internal sealed class PdfPageCache : IDisposable
{
    private readonly PdfDocument _document;
    private readonly OutputSize _size;
    private readonly TaskCompletionSource<byte[]>[] _results;
    private readonly List<int> _priority = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    public PdfPageCache(PdfDocument document, OutputSize size)
    {
        _document = document;
        _size = size;

        _results = new TaskCompletionSource<byte[]>[(int)document.PageCount];
        for (var i = 0; i < _results.Length; i++)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Una pagina fallita non osservata non deve far scattare l'handler globale dei task.
            tcs.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _results[i] = tcs;
        }

        Task.Run(() => WorkerAsync(_cts.Token));
    }

    public int PageCount => _results.Length;

    /// <summary>Restituisce i byte PNG della pagina (0-based), dandole la precedenza nel rendering.</summary>
    public Task<byte[]> GetPageAsync(int index)
    {
        var task = _results[index].Task;
        if (!task.IsCompleted)
        {
            lock (_gate)
            {
                _priority.Remove(index);
                _priority.Insert(0, index);
            }
        }

        return task;
    }

    private async Task WorkerAsync(CancellationToken token)
    {
        try
        {
            var sequential = 0;
            while (!token.IsCancellationRequested)
            {
                var next = PickNext(ref sequential);
                if (next < 0)
                    break;

                try
                {
                    var bytes = await RenderPageAsync(next);
                    _results[next].TrySetResult(bytes);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Log.Error(ex, "Rendering pagina PDF {Page} fallito", next + 1);
                    _results[next].TrySetException(ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Worker di rendering PDF terminato con errore");
        }
        finally
        {
            foreach (var tcs in _results)
                tcs.TrySetCanceled();
        }
    }

    private int PickNext(ref int sequential)
    {
        lock (_gate)
        {
            while (_priority.Count > 0)
            {
                var candidate = _priority[0];
                _priority.RemoveAt(0);
                if (!_results[candidate].Task.IsCompleted)
                    return candidate;
            }
        }

        while (sequential < _results.Length && _results[sequential].Task.IsCompleted)
            sequential++;

        return sequential < _results.Length ? sequential : -1;
    }

    private async Task<byte[]> RenderPageAsync(int index)
    {
        using var page = _document.GetPage((uint)index);

        var width = page.Size.Width;
        var height = page.Size.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Pagina PDF con dimensioni non valide");

        // Adatta la pagina all'output mantenendo le proporzioni (la finestra fa il letterbox).
        var scale = Math.Min(_size.Width / width, _size.Height / height);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height * scale)),
            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);

        using var managed = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await managed.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}
