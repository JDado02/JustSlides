using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Regia.Core.Media;
using Regia.Core.Wave;
using Regia.Output.Tappo;
using Regia.Output.Windows;
using Serilog;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Regia.Output.Content;

/// <summary>
/// Video LibVLC in una finestra figlia (<see cref="VideoHost"/>) della finestra contenuto, sotto il Tappo.
/// Caricamento: si fa partire il player a volume 0 e, al primo fotogramma, lo si mette in pausa (pronto, muto);
/// la riproduzione vera parte con <see cref="BeginPlayback"/>, all'inizio della dissolvenza in entrata.
/// Gli eventi di LibVLC arrivano su thread suoi: si passa sempre da <c>BeginInvoke</c> sul thread UI e non si
/// chiama mai il player dentro un evento (deadlock noto). Stop/Dispose del player vanno fuori dal thread UI.
/// </summary>
public sealed class VideoPresenter : IPlaybackContent
{
    private const int LoadTimeoutMs = 10_000;

    /// <summary>Dopo "Playing" senza traccia video oltre questo tempo il file si considera solo audio.</summary>
    private const int NoVideoGraceMs = 3_000;

    /// <summary>Si ferma il video quando mancano questi ms alla fine, per restare sull'ultimo fotogramma (a EndReached la finestra diventa nera).</summary>
    private const int EndMarginMs = 120;

    private readonly ContentWindow _window;
    private readonly VlcService _vlc;
    private readonly string _path;
    private readonly VideoEndAction _endAction;
    private readonly string? _audioDeviceId;
    private readonly Dispatcher _dispatcher;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private VideoHost? _host;
    private Media? _media;
    private MediaPlayer? _player;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _fadeTimer;
    private TaskCompletionSource? _fadeDone;
    private volatile bool _closed;
    private volatile bool _playingSeen;
    private bool _started;
    private bool _ended;
    private bool _userPaused;
    private bool _audioOpen;
    private int _volume = 100;
    private bool _muted;
    private double _fadeScale = 1.0;
    private int _tick;
    private long _lengthMs;

    public VideoPresenter(ContentWindow window, VlcService vlc, string path, VideoEndAction endAction, string? audioDeviceId)
    {
        _window = window;
        _vlc = vlc;
        _path = path;
        _endAction = endAction;
        _audioDeviceId = string.IsNullOrEmpty(audioDeviceId) ? null : audioDeviceId;
        _dispatcher = window.Dispatcher;
    }

    public PageInfo? Page => null;

    public event Action? PageChanged
    {
        add { }
        remove { }
    }

    public event Action? ProgressChanged;

    public event Action? EndRequested;

    public event Action<Exception>? Faulted;

    public bool IsPaused => _userPaused;

    public PlaybackProgress Progress
    {
        get
        {
            var player = _player;
            if (_closed || player is null)
                return default;

            var length = Math.Max(0, GetLengthMs(player));
            var time = _ended ? length : Math.Clamp(player.Time, 0, length > 0 ? length : long.MaxValue);
            return new PlaybackProgress(TimeSpan.FromMilliseconds(time), TimeSpan.FromMilliseconds(length));
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException("File non trovato", _path);

        _host = new VideoHost();
        _window.SetContent(_host);
        _host.UpdateLayout();
        if (_host.Handle == IntPtr.Zero)
            await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (_host.Handle == IntPtr.Zero)
            throw new InvalidOperationException("La finestra video non è stata creata");

        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
            return;

        _player = new MediaPlayer(_vlc.Instance) { Hwnd = _host.Handle };
        if (_audioDeviceId is not null)
            _player.SetAudioOutput("mmdevice");

        _media = new Media(_vlc.Instance, new Uri(Path.GetFullPath(_path)));
        if (_endAction == VideoEndAction.Loop)
            _media.AddOption(":input-repeat=65535");

        _player.Playing += OnVlcPlaying;
        _player.Vout += OnVlcVout;
        _player.EndReached += OnVlcEndReached;
        _player.EncounteredError += OnVlcError;
        _player.LengthChanged += OnVlcLengthChanged;

        // Muto finché non va in onda: nessun suono sotto il Tappo.
        _player.Volume = 0;
        _player.Play(_media);

        var first = await Task.WhenAny(_ready.Task, Task.Delay(LoadTimeoutMs, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
            return;
        if (first != _ready.Task)
            throw new TimeoutException($"Il video non si è aperto entro {LoadTimeoutMs / 1000} secondi");
        await _ready.Task; // rilancia l'eventuale errore del player

        // Primo fotogramma visibile e fermo: pronto per la messa in onda.
        _player.SetPause(true);
        await RenderWait.NextFramesAsync(cancellationToken);

        Log.Information("Video pronto: {Path} ({Length} ms)", _path, _lengthMs);
    }

    public void BeginPlayback()
    {
        var player = _player;
        if (_closed || player is null || _started)
            return;

        _started = true;
        _audioOpen = true;
        ApplyVolume();
        player.SetPause(false);

        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
        Log.Information("Video in riproduzione: {Path}", _path);
    }

    public void TogglePause()
    {
        var player = _player;
        if (_closed || player is null || !_started || _ended)
            return;

        _userPaused = !_userPaused;
        player.SetPause(_userPaused);
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        ApplyVolume();
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        ApplyVolume();
    }

    public Task FadeAudioOutAsync(TimeSpan duration)
    {
        if (_closed || _player is null)
            return Task.CompletedTask;

        StopFade();

        if (duration <= TimeSpan.Zero)
        {
            _fadeScale = 0;
            ApplyVolume();
            return Task.CompletedTask;
        }

        var start = _fadeScale;
        var watch = Stopwatch.StartNew();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fadeDone = done;

        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var k = Math.Min(1.0, watch.Elapsed / duration);
            _fadeScale = start * (1.0 - k);
            ApplyVolume();

            if (k >= 1.0)
            {
                timer.Stop();
                done.TrySetResult();
            }
        };
        _fadeTimer = timer;
        timer.Start();

        return done.Task;
    }

    public bool Next() => false;

    public bool Previous() => false;

    public void Close()
    {
        if (_closed)
            return;

        _closed = true;
        _timer?.Stop();
        _timer = null;
        StopFade();

        var player = _player;
        var media = _media;
        var host = _host;
        _player = null;
        _media = null;
        _host = null;

        if (player is not null)
        {
            // Audio tagliato subito (PANIC): lo Stop vero richiede tempo ed è fuori dal thread UI.
            try
            {
                player.Volume = 0;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Volume non azzerato in chiusura");
            }

            player.Playing -= OnVlcPlaying;
            player.Vout -= OnVlcVout;
            player.EndReached -= OnVlcEndReached;
            player.EncounteredError -= OnVlcError;
            player.LengthChanged -= OnVlcLengthChanged;
        }

        var stopped = Task.Run(() =>
        {
            try
            {
                player?.Stop();
                player?.Dispose();
                media?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Errore nella chiusura del video");
            }
        });

        // La finestra video si distrugge solo a Stop finito: VLC non deve mai disegnare su un HWND distrutto.
        if (host is not null)
            _window.Retire(host, stopped);
    }

    private void ApplyVolume()
    {
        var player = _player;
        if (_closed || player is null)
            return;

        var volume = _audioOpen && !_muted ? (int)Math.Round(_volume * _fadeScale) : 0;
        if (player.Volume != volume)
            player.Volume = volume;
    }

    private void StopFade()
    {
        _fadeTimer?.Stop();
        _fadeTimer = null;
        _fadeDone?.TrySetResult();
        _fadeDone = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var player = _player;
        if (_closed || player is null)
            return;

        _tick++;

        if (!_userPaused && !_ended && _endAction != VideoEndAction.Loop)
        {
            var length = GetLengthMs(player);
            if (length > 0 && player.Time >= length - EndMarginMs)
                FinishVideo(player);
        }

        if (_tick % 2 == 0 || _ended)
            ProgressChanged?.Invoke();
    }

    /// <summary>Il video è arrivato in fondo: si ferma sull'ultimo fotogramma e, se richiesto, si chiede il ritorno al Tappo.</summary>
    private void FinishVideo(MediaPlayer player)
    {
        if (_ended)
            return;

        _ended = true;
        try
        {
            player.SetPause(true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Pausa di fine video non applicata");
        }

        Log.Information("Fine video ({Action}): {Path}", _endAction, _path);
        ProgressChanged?.Invoke();

        if (_endAction == VideoEndAction.ReturnToTappo)
            EndRequested?.Invoke();
    }

    private long GetLengthMs(MediaPlayer player)
    {
        var length = player.Length;
        if (length <= 0)
            length = _lengthMs;
        if (length <= 0 && _media is not null)
            length = _media.Duration;
        return length;
    }

    // ---- Eventi di LibVLC (thread di VLC): solo marshaling verso la UI ----

    private void OnVlcPlaying(object? sender, EventArgs e) => Post(OnPlayingUi);

    private void OnVlcVout(object? sender, MediaPlayerVoutEventArgs e)
    {
        if (e.Count > 0)
            _ready.TrySetResult();
    }

    private void OnVlcEndReached(object? sender, EventArgs e) => Post(() =>
    {
        // Rete di sicurezza: il polling di fine non ha fatto in tempo (durata non affidabile). La finestra può essere nera.
        if (_player is { } player && !_ended && _endAction != VideoEndAction.Loop)
        {
            Log.Warning("EndReached senza arresto anticipato: possibile schermo nero a fine video ({Path})", _path);
            FinishVideo(player);
        }
    });

    private void OnVlcError(object? sender, EventArgs e) => Post(() =>
    {
        var error = new InvalidOperationException("Il player video ha segnalato un errore (file corrotto o codec non supportato)");
        Log.Error("Errore del player video: {Path}", _path);

        if (!_ready.Task.IsCompleted)
            _ready.TrySetException(error);
        else
            Faulted?.Invoke(error);
    });

    private void OnVlcLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) => _lengthMs = e.Length;

    private void Post(Action action)
    {
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_closed)
                    action();
            });
        }
        catch (Exception ex)
        {
            // Dispatcher in chiusura: nessuna conseguenza.
            Log.Debug(ex, "Evento del player scartato");
        }
    }

    private void OnPlayingUi()
    {
        if (_playingSeen)
            return;

        _playingSeen = true;

        // Il dispositivo si può impostare solo a riproduzione avviata (con volume 0 nessun suono sul predefinito).
        if (_audioDeviceId is not null && _player is { } player)
        {
            player.SetOutputDevice(_audioDeviceId);
            Log.Information("Dispositivo audio del video impostato: {Id}", _audioDeviceId);
        }

        // File senza traccia video (solo audio): niente Vout, lo si dà per pronto dopo una breve attesa.
        _ = Task.Delay(NoVideoGraceMs).ContinueWith(
            _ =>
            {
                if (!_closed && _ready.TrySetResult())
                    Log.Warning("Nessuna traccia video rilevata, si prosegue: {Path}", _path);
            },
            TaskScheduler.Default);
    }
}
