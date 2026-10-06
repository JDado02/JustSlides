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

public interface IContentPresenterFactory
{
    /// <summary>Crea il presenter per il file. Lancia NotSupportedException per i tipi non ancora gestiti.</summary>
    IContentPresenter Create(MediaItem item);
}
