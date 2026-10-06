using Regia.Core.Media;

namespace Regia.Core.Wave;

/// <summary>Pagina corrente / totale (1-based).</summary>
public readonly record struct PageInfo(int Current, int Total);

/// <summary>Dissolvenze del Tappo, astratte per poter testare l'orchestratore con dei finti.</summary>
public interface ITappoTransitions
{
    /// <summary>Messa in onda: Tappo 1 → 0. False se interrotta.</summary>
    Task<bool> RevealAsync();

    /// <summary>Ritorno al Tappo: Tappo 0 → 1. False se interrotta.</summary>
    Task<bool> CoverAsync();

    /// <summary>PANIC: Tappo pieno subito.</summary>
    void CoverNow();

    /// <summary>Durata della dissolvenza in corso o prossima (zero con il taglio secco): serve al fade audio.</summary>
    TimeSpan FadeDuration { get; }
}

/// <summary>Un contenuto (immagine, PDF...) mostrato nella finestra sotto il Tappo. Una istanza per file.</summary>
public interface IContentPresenter
{
    /// <summary>Completa quando il contenuto è renderizzato sotto il Tappo (pronto per la messa in onda).</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Pagina/slide successiva. False se non c'è (ultima pagina o contenuto senza pagine).</summary>
    bool Next();

    bool Previous();

    /// <summary>Pagina corrente; null per i contenuti senza pagine.</summary>
    PageInfo? Page { get; }

    event Action? PageChanged;

    /// <summary>Libera il contenuto. Idempotente.</summary>
    void Close();
}

/// <summary>Tempo trascorso e durata di un contenuto a riproduzione continua.</summary>
public readonly record struct PlaybackProgress(TimeSpan Elapsed, TimeSpan Duration)
{
    public TimeSpan Remaining => Duration > Elapsed ? Duration - Elapsed : TimeSpan.Zero;
}

/// <summary>
/// Contenuto a riproduzione continua (video). Il controller lo riconosce con <c>is</c>: immagini e PDF
/// non lo implementano. Gli eventi vanno sollevati sul thread UI e mai dopo <see cref="IContentPresenter.Close"/>.
/// </summary>
public interface IPlaybackContent : IContentPresenter
{
    /// <summary>Fa partire la riproduzione (dopo <see cref="IContentPresenter.LoadAsync"/>, all'inizio della dissolvenza in entrata).</summary>
    void BeginPlayback();

    /// <summary>Play / Pausa.</summary>
    void TogglePause();

    bool IsPaused { get; }

    PlaybackProgress Progress { get; }

    /// <summary>Volume 0-100.</summary>
    void SetVolume(int volume);

    void SetMuted(bool muted);

    /// <summary>Porta la riproduzione a <paramref name="position"/> (anche in pausa); fuori dai limiti si riporta dentro.</summary>
    void Seek(TimeSpan position);

    /// <summary>Abbassa l'audio a zero in <paramref name="duration"/>; completa a fine rampa o alla chiusura.</summary>
    Task FadeAudioOutAsync(TimeSpan duration);

    /// <summary>Aggiornamento di tempo trascorso / rimanente (pochi al secondo).</summary>
    event Action? ProgressChanged;

    /// <summary>Il video è finito e l'azione di fine chiede di tornare al Tappo.</summary>
    event Action? EndRequested;

    /// <summary>Il player è andato in errore durante la riproduzione.</summary>
    event Action<Exception>? Faulted;
}

public interface IContentPresenterFactory
{
    /// <summary>Crea il presenter per il file. Lancia NotSupportedException per i tipi non ancora gestiti.</summary>
    IContentPresenter Create(MediaItem item);
}
