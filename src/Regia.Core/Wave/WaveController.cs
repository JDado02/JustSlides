using Regia.Core.Media;
using Serilog;

namespace Regia.Core.Wave;

/// <summary>
/// Orchestratore dell'onda: esegue GO / Stop / pagina / PANIC pilotando la macchina a stati,
/// il Tappo e il contenuto. Va usato da un solo thread (quello UI): non lancia mai eccezioni
/// verso l'alto, ogni errore porta al Tappo e allo stato Errore.
/// </summary>
public sealed class WaveController
{
    private readonly WaveStateMachine _machine;
    private readonly ITappoTransitions _tappo;
    private readonly IContentPresenterFactory _factory;

    private IContentPresenter? _current;
    private IPlaybackContent? _playback;
    private ILiveContent? _live;
    private CancellationTokenSource _cts = new();
    private int _generation;
    private bool _endPending;
    private bool _muted;
    private int _liveVolume = 100;

    public WaveController(WaveStateMachine machine, ITappoTransitions tappo, IContentPresenterFactory factory)
    {
        _machine = machine;
        _tappo = tappo;
        _factory = factory;
        _machine.StateChanged += (old, now) => StateChanged?.Invoke(old, now);
    }

    public WaveState State => _machine.State;

    public MediaItem? CurrentItem { get; private set; }

    public PageInfo? Page => _current?.Page;

    /// <summary>Tempo trascorso / durata del video in onda; null per gli altri contenuti.</summary>
    public PlaybackProgress? Progress => _playback?.Progress;

    public bool IsPaused => _playback?.IsPaused ?? false;

    /// <summary>
    /// Volume dal vivo del video in onda (0-100). Parte dal volume salvato nel file (<see cref="MediaItem.Volume"/>)
    /// a ogni messa in onda e si può cambiare senza toccare quello salvato.
    /// </summary>
    public int LiveVolume => _liveVolume;

    public event Action<WaveState, WaveState>? StateChanged;

    public event Action? PageChanged;

    /// <summary>Progresso del video, pausa/ripresa o fine: la UI rilegge <see cref="Progress"/> e <see cref="IsPaused"/>.</summary>
    public event Action? PlaybackChanged;

    /// <summary>Messaggio per l'operatore quando qualcosa è andato storto.</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>Messa in onda; se qualcosa è già in onda, cambio file passando dal Tappo.</summary>
    public async Task GoAsync(MediaItem item)
    {
        if (!_machine.CanFire(WaveTrigger.Go))
        {
            _machine.Fire(WaveTrigger.Go); // logga il comando ignorato
            return;
        }

        var generation = NewOperation(out var token);
        Log.Information("GO: {Item}", item.DisplayName);

        try
        {
            if (_machine.State == WaveState.InOnda)
            {
                // Cambio file: Tappo 0→1, solo dopo si chiude il vecchio contenuto.
                _machine.Fire(WaveTrigger.Go);
                var covered = await CoverWithAudioFadeAsync();
                if (!covered || generation != _generation)
                    return;

                CloseCurrent();
                _machine.Fire(WaveTrigger.FadeCompleted);
            }

            _machine.Fire(WaveTrigger.Go);
            await LoadAndRevealAsync(item, generation, token);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                Fail("GO", ex);
        }
    }

    /// <summary>Ritorno al Tappo con dissolvenza, poi chiusura del contenuto.</summary>
    public async Task StopAsync()
    {
        if (!_machine.Fire(WaveTrigger.Stop))
            return;

        var generation = NewOperation(out _);
        Log.Information("Ritorno al Tappo: {Item}", CurrentItem?.DisplayName);

        try
        {
            var covered = await CoverWithAudioFadeAsync();
            if (!covered || generation != _generation)
                return;

            CloseCurrent();
            _machine.Fire(WaveTrigger.FadeCompleted);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                Fail("Torna al Tappo", ex);
        }
    }

    /// <summary>Play / Pausa del video in onda; ignorato (e loggato) negli altri casi.</summary>
    public bool TogglePause()
    {
        if (_playback is null || !_machine.CanFire(WaveTrigger.Transport))
        {
            Log.Warning("Comando Play/Pausa ignorato: stato {State}", _machine.State);
            return false;
        }

        try
        {
            _machine.Fire(WaveTrigger.Transport);
            _playback.TogglePause();
            Log.Information("Video: {State}", _playback.IsPaused ? "pausa" : "play");
            PlaybackChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Fail("Play/Pausa", ex);
            return false;
        }
    }

    /// <summary>Volume dal vivo 0-100 del video in onda. Non cambia il volume salvato nel file: solo questa riproduzione.</summary>
    public void SetVolume(int volume)
    {
        if (_playback is null)
            return;

        _liveVolume = Math.Clamp(volume, 0, 100);
        TryApply(p => p.SetVolume(_liveVolume), "Volume");
    }

    /// <summary>Porta il video in onda a una posizione assoluta. Ignorato fuori da <c>InOnda</c> (anche durante le dissolvenze).</summary>
    public bool SeekTo(TimeSpan position, bool log = true)
    {
        if (_playback is null || !_machine.CanFire(WaveTrigger.Transport))
        {
            Log.Warning("Scorrimento video ignorato: stato {State}", _machine.State);
            return false;
        }

        try
        {
            _playback.Seek(position);
            if (log)
                Log.Information("Video: posizione {Position:hh\\:mm\\:ss}", position);
            PlaybackChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Fail("Scorrimento video", ex);
            return false;
        }
    }

    /// <summary>Sposta il video in onda avanti o indietro rispetto alla posizione attuale.</summary>
    public bool SeekBy(TimeSpan delta) =>
        SeekTo(_playback is { } playback ? playback.Progress.Elapsed + delta : delta);

    public void SetMuted(bool muted)
    {
        _muted = muted;
        TryApply(p => p.SetMuted(_muted), "Mute");
    }

    public bool Next() => Navigate(p => p.Next(), "Avanti");

    public bool Previous() => Navigate(p => p.Previous(), "Indietro");

    /// <summary>PANIC: Tappo immediato da qualsiasi stato, annulla tutto, chiude il contenuto.</summary>
    public void Panic()
    {
        Log.Warning("PANIC (stato precedente: {State})", _machine.State);
        Abort();
        _machine.Fire(WaveTrigger.Panic);
    }

    /// <summary>Errore durante la proiezione: Tappo immediato, contenuto chiuso, stato Errore.</summary>
    public void Fail(string context, Exception ex)
    {
        Log.Error(ex, "Errore in {Context}: ritorno al Tappo", context);
        Abort();
        _machine.Fire(WaveTrigger.Fail);
        ErrorOccurred?.Invoke($"Errore ({context}): {ex.Message}");
    }

    private bool Navigate(Func<IContentPresenter, bool> move, string name)
    {
        if (!_machine.CanFire(WaveTrigger.Navigate) || _current is null)
        {
            Log.Warning("Comando {Name} ignorato: stato {State}", name, _machine.State);
            return false;
        }

        try
        {
            if (!move(_current))
            {
                Log.Information("Comando {Name} ignorato: nessuna pagina in quella direzione", name);
                return false;
            }

            _machine.Fire(WaveTrigger.Navigate);
            return true;
        }
        catch (Exception ex)
        {
            Fail(name, ex);
            return false;
        }
    }

    private async Task LoadAndRevealAsync(MediaItem item, int generation, CancellationToken token)
    {
        IContentPresenter presenter;
        try
        {
            presenter = _factory.Create(item);
        }
        catch (Exception ex)
        {
            FailLoad(item, ex);
            return;
        }

        _current = presenter;
        CurrentItem = item;
        presenter.PageChanged += OnPresenterPageChanged;

        // Video e slideshow possono finire o rompersi da soli.
        if (presenter is ILiveContent live)
        {
            _live = live;
            live.EndRequested += OnPlaybackEnded;
            live.Faulted += OnPlaybackFaulted;
        }

        if (presenter is IPlaybackContent playback)
        {
            _playback = playback;
            playback.ProgressChanged += OnPlaybackProgress;
            // Ogni video parte dal suo volume salvato.
            _liveVolume = Math.Clamp(item.Volume, 0, 100);
            TryApply(p =>
            {
                p.SetVolume(_liveVolume);
                p.SetMuted(_muted);
            }, "Volume iniziale");
        }

        try
        {
            await presenter.LoadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            presenter.Close();
            return;
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                FailLoad(item, ex);
            else
                presenter.Close();
            return;
        }

        // Annullato (PANIC) mentre caricava: il risultato tardivo si scarta.
        if (generation != _generation)
        {
            presenter.Close();
            return;
        }

        _machine.Fire(WaveTrigger.ContentReady);
        PageChanged?.Invoke();

        // Il video parte con la dissolvenza (movimento e audio entrano insieme al Tappo che sfuma).
        if (_playback is { } playing && ReferenceEquals(_current, playing))
        {
            try
            {
                playing.BeginPlayback();
            }
            catch (Exception ex)
            {
                if (generation == _generation)
                    Fail("Avvio video", ex);
                return;
            }
        }

        var revealed = await _tappo.RevealAsync();
        if (!revealed || generation != _generation)
            return;

        _machine.Fire(WaveTrigger.FadeCompleted);
        Log.Information("In onda: {Item}", item.DisplayName);
        PlaybackChanged?.Invoke();

        // Video più corto della dissolvenza: la fine è arrivata mentre eravamo in transizione.
        if (_endPending)
        {
            _endPending = false;
            await StopAsync();
        }
    }

    /// <summary>Tappo 0→1 con, in parallelo, la rampa audio a zero del video in onda.</summary>
    private async Task<bool> CoverWithAudioFadeAsync()
    {
        var audio = Task.CompletedTask;
        if (_playback is { } playback)
        {
            try
            {
                audio = playback.FadeAudioOutAsync(_tappo.FadeDuration);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fade audio non avviato");
            }
        }

        var covered = await _tappo.CoverAsync();

        try
        {
            await audio;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Errore nel fade audio");
        }

        return covered;
    }

    private void TryApply(Action<IPlaybackContent> action, string name)
    {
        if (_playback is null)
            return;

        try
        {
            action(_playback);
        }
        catch (Exception ex)
        {
            // Un volume che non si applica non deve fermare la regia.
            Log.Warning(ex, "{Name} non applicato", name);
        }
    }

    private void OnPlaybackProgress() => PlaybackChanged?.Invoke();

    private void OnPlaybackEnded()
    {
        Log.Information("Contenuto terminato: {Item}", CurrentItem?.DisplayName);
        PlaybackChanged?.Invoke();

        switch (_machine.State)
        {
            case WaveState.InOnda:
                _ = StopAsync();
                break;
            case WaveState.InTransizioneIn:
                _endPending = true; // si esegue appena la dissolvenza in entrata è finita
                break;
        }
    }

    private void OnPlaybackFaulted(Exception ex) => Fail("Video", ex);

    private void FailLoad(MediaItem item, Exception ex)
    {
        Log.Error(ex, "Caricamento fallito: {Item}", item.DisplayName);
        Abort();
        _machine.Fire(WaveTrigger.LoadFailed);
        ErrorOccurred?.Invoke($"Impossibile aprire \"{item.DisplayName}\": {ex.Message}");
    }

    /// <summary>Invalida le operazioni in corso, Tappo pieno subito, contenuto chiuso.</summary>
    private void Abort()
    {
        _generation++;
        _cts.Cancel();

        try
        {
            _tappo.CoverNow();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nel ritorno immediato al Tappo");
        }

        CloseCurrent();
    }

    private int NewOperation(out CancellationToken token)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        token = _cts.Token;
        return ++_generation;
    }

    private void CloseCurrent()
    {
        var presenter = _current;
        _current = null;
        CurrentItem = null;
        _endPending = false;

        if (_playback is { } playback)
        {
            playback.ProgressChanged -= OnPlaybackProgress;
            _playback = null;
        }

        if (_live is { } live)
        {
            live.EndRequested -= OnPlaybackEnded;
            live.Faulted -= OnPlaybackFaulted;
            _live = null;
        }

        if (presenter is null)
            return;

        presenter.PageChanged -= OnPresenterPageChanged;
        try
        {
            presenter.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nella chiusura del contenuto");
        }

        PageChanged?.Invoke();
        PlaybackChanged?.Invoke();
    }

    private void OnPresenterPageChanged() => PageChanged?.Invoke();
}
