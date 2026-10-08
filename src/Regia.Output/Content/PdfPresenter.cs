using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Regia.Core.Wave;
using Regia.Output.Windows;
using Serilog;
using Windows.Data.Pdf;
using Windows.Storage;

namespace Regia.Output.Content;

/// <summary>
/// Pagine di un PDF a tutto schermo. Le pagine sono pre-renderizzate in background alla risoluzione
/// dell'output; il cambio pagina è a taglio secco. Se la pagina richiesta non è ancora pronta si
/// aspetta il suo rendering (resta visibile la precedente) senza passare dal Tappo.
/// </summary>
public sealed class PdfPresenter : ILiveContent
{
    // Pagine decodificate tenute in memoria: corrente ±2.
    private const int DecodedWindow = 2;

    private readonly ContentWindow _window;
    private readonly string _path;
    private readonly OutputSize _size;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, Task<BitmapSource>> _decoded = [];

    private PdfDocument? _document;
    private PdfPageCache? _cache;
    private Image? _view;
    private int _index;
    private int _showVersion;
    private bool _closed;
    private bool _endRaised;

    public PdfPresenter(ContentWindow window, string path, OutputSize size)
    {
        _window = window;
        _path = path;
        _size = size;
    }

    public PageInfo? Page => _cache is null ? null : new PageInfo(_index + 1, _cache.PageCount);

    public event Action? PageChanged;

    /// <summary>"Avanti" oltre l'ultima pagina: il controller riporta il Tappo (come la fine di un PPT o di un video).</summary>
    public event Action? EndRequested;

    // Un PDF non si rompe da solo: l'evento esiste solo per ILiveContent.
#pragma warning disable CS0067
    public event Action<Exception>? Faulted;
#pragma warning restore CS0067

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException("File non trovato", _path);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linked.Token;

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(_path)).AsTask(token);
        var document = await PdfDocument.LoadFromFileAsync(file).AsTask(token);

        if (_closed)
            return;

        if (document.IsPasswordProtected)
            throw new InvalidOperationException("PDF protetto da password");
        if (document.PageCount == 0)
            throw new InvalidOperationException("PDF senza pagine");

        _document = document;
        _cache = new PdfPageCache(document, _size);
        _index = 0;

        var first = await GetBitmapAsync(0);
        token.ThrowIfCancellationRequested();
        if (_closed)
            return;

        _view = new Image { Stretch = Stretch.Uniform, Source = first };
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);
        _window.SetContent(_view);
        Prefetch(0);

        await RenderWait.NextFramesAsync(token);
        Log.Information("PDF pronto: {Path} ({Pages} pagine)", _path, document.PageCount);
    }

    public bool Next() => GoTo(_index + 1);

    public bool Previous() => GoTo(_index - 1);

    private bool GoTo(int index)
    {
        if (_closed || _cache is null || _view is null)
            return false;

        if (index >= _cache.PageCount)
        {
            Log.Information("PDF: oltre l'ultima pagina ({Total}), ritorno al Tappo", _cache.PageCount);
            RaiseEndDeferred();
            return false;
        }

        if (index < 0)
        {
            Log.Information("PDF: nessuna pagina {Page} (totale {Total})", index + 1, _cache.PageCount);
            return false;
        }

        _index = index;
        PageChanged?.Invoke();
        _ = ShowAsync(index, ++_showVersion);
        return true;
    }

    /// <summary>In differita: il controller sta ancora gestendo il comando "avanti" quando arriva la fine.</summary>
    private void RaiseEndDeferred()
    {
        if (_endRaised)
            return;

        _endRaised = true;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
                EndRequested?.Invoke();
        });
    }

    /// <summary>Mostra la pagina quando è pronta; vince sempre l'ultima richiesta.</summary>
    private async Task ShowAsync(int index, int version)
    {
        try
        {
            var bitmap = await GetBitmapAsync(index);
            if (_closed || version != _showVersion || _view is null)
                return;

            _view.Source = bitmap;
            Prefetch(index);
        }
        catch (Exception ex) when (_closed || ex is OperationCanceledException)
        {
            // Contenuto chiuso mentre la pagina si preparava: nulla da fare.
        }
        catch (Exception ex)
        {
            // Resta visibile la pagina precedente; l'errore non deve fermare la regia.
            Log.Error(ex, "PDF: impossibile mostrare la pagina {Page}", index + 1);
        }
    }

    private Task<BitmapSource> GetBitmapAsync(int index)
    {
        if (_decoded.TryGetValue(index, out var existing))
            return existing;

        var cache = _cache ?? throw new InvalidOperationException("PDF non caricato");
        var task = DecodeAsync(cache, index);
        _decoded[index] = task;

        // Se la decodifica fallisce non si tiene in cache: si potrà ritentare.
        task.ContinueWith(
            t =>
            {
                _ = t.Exception;
                _decoded.Remove(index);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.FromCurrentSynchronizationContext());

        return task;
    }

    private static async Task<BitmapSource> DecodeAsync(PdfPageCache cache, int index)
    {
        var bytes = await cache.GetPageAsync(index);
        return await Task.Run<BitmapSource>(() =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        });
    }

    /// <summary>Decodifica in anticipo le pagine vicine e scarta quelle lontane.</summary>
    private void Prefetch(int center)
    {
        foreach (var key in _decoded.Keys.Where(k => Math.Abs(k - center) > DecodedWindow).ToList())
            _decoded.Remove(key);

        if (_cache is null)
            return;

        for (var i = Math.Max(0, center - DecodedWindow); i <= Math.Min(_cache.PageCount - 1, center + DecodedWindow); i++)
        {
            // Si usa solo per avviare la decodifica; gli errori vengono osservati in GetBitmapAsync.
            _ = GetBitmapAsync(i);
        }
    }

    public void Close()
    {
        if (_closed)
            return;

        _closed = true;
        _cts.Cancel();
        _cache?.Dispose();
        _cache = null;
        _document = null;
        _decoded.Clear();

        if (_view is not null)
        {
            _window.ClearContent(_view);
            _view.Source = null;
        }
    }
}
