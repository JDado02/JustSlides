using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Regia.Core.Media;
using Regia.Output.Tappo;
using Serilog;

namespace Regia.Output.Preflight;

/// <summary>Quanto lavoro può fare il pre-flight in questo momento.</summary>
public enum PreflightLoad
{
    /// <summary>Niente (caricamento o dissolvenza in corso).</summary>
    None,

    /// <summary>Solo controlli leggeri: immagini, PDF, PPT (un video o un PowerPoint sono in onda).</summary>
    Light,

    /// <summary>Tutto, video compresi (lettura con VLC e cattura del fotogramma).</summary>
    Full
}

/// <summary>
/// Pre-flight e miniature in background, un file alla volta. Si accoda a ogni copia locale completata e applica
/// l'esito alla voce sul thread UI (da cui viene creato il servizio). Per non disturbare l'onda: nulla durante
/// caricamento e dissolvenze, e niente video mentre un video o un PowerPoint sono in onda.
/// </summary>
public sealed class PreflightService : IDisposable
{
    private readonly VlcService _vlc;
    private readonly Func<PreflightLoad> _load;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly HashSet<MediaItem> _queued = [];
    private readonly object _gate = new();

    private Channel<MediaItem>? _queue;
    private CancellationTokenSource? _cts;
    private string _thumbsDirectory = "";

    public PreflightService(VlcService vlc, Func<PreflightLoad> load)
    {
        _vlc = vlc;
        _load = load;
    }

    /// <summary>Cartella delle miniature dell'evento; ricrea la coda (cambio evento).</summary>
    public void Configure(string thumbsDirectory)
    {
        Stop();

        _thumbsDirectory = thumbsDirectory;
        Directory.CreateDirectory(thumbsDirectory);

        _queue = Channel.CreateUnbounded<MediaItem>();
        _cts = new CancellationTokenSource();
        var queue = _queue;
        var token = _cts.Token;
        _ = Task.Run(() => WorkerAsync(queue, token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _queue?.Writer.TryComplete();
        _queue = null;
        lock (_gate)
            _queued.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>Mette in coda il pre-flight di una voce con la copia locale pronta.</summary>
    public void Enqueue(MediaItem item)
    {
        if (item.Kind is MediaKind.TestPattern or MediaKind.Unknown || _queue is null)
            return;

        lock (_gate)
        {
            if (!_queued.Add(item))
                return;
        }

        _queue.Writer.TryWrite(item);
    }

    private async Task WorkerAsync(Channel<MediaItem> queue, CancellationToken token)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await ProcessAsync(item, queue, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Errore imprevisto nel pre-flight di {Path}", item.RelativePath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cambio evento o chiusura.
        }
    }

    private async Task ProcessAsync(MediaItem item, Channel<MediaItem> queue, CancellationToken token)
    {
        // Aspetta un momento tranquillo; i video aspettano un momento ancora più tranquillo.
        PreflightLoad load;
        while ((load = _load()) == PreflightLoad.None || (item.Kind == MediaKind.Video && load != PreflightLoad.Full))
            await Task.Delay(500, token);

        lock (_gate)
            _queued.Remove(item);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await FileChecks.CheckAsync(item, _vlc, includeVideo: true, token);
        token.ThrowIfCancellationRequested();

        var thumbPath = result.ThumbnailJpeg is { Length: > 0 } ? WriteThumbnail(item, result.ThumbnailJpeg) : null;

        Post(() =>
        {
            var old = item.ThumbnailPath;
            item.Preflight = result.Status;
            item.PreflightSummary = result.Summary;
            item.PreflightDetails = result.Details;
            item.SlideTitles = result.SlideTitles;
            item.HiddenSlides = result.HiddenSlides;
            if (thumbPath is not null)
                item.ThumbnailPath = thumbPath;

            if (thumbPath is not null && old is not null && old != thumbPath)
                TryDelete(old);
        });

        Log.Information("Pre-flight {Path}: {Status} - {Summary} ({Ms} ms)", item.RelativePath, result.Status, result.Summary, clock.ElapsedMilliseconds);
    }

    private string WriteThumbnail(MediaItem item, byte[] jpeg)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(item.RelativePath.ToLowerInvariant())))[..16];

        // Nome nuovo a ogni versione: l'interfaccia ricarica solo se il percorso cambia.
        var path = Path.Combine(_thumbsDirectory, $"{hash}-{Environment.TickCount64:x}.jpg");
        File.WriteAllBytes(path, jpeg);
        return path;
    }

    private void Post(Action action)
    {
        if (_ui is null)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Una miniatura vecchia rimasta: sparisce con la cache.
        }
    }
}
