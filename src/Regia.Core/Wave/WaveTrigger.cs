namespace Regia.Core.Wave;

/// <summary>Eventi che fanno avanzare la macchina a stati dell'onda.</summary>
public enum WaveTrigger
{
    /// <summary>Messa in onda (o cambio file se qualcosa è già in onda).</summary>
    Go,

    /// <summary>Il contenuto sotto il Tappo è pronto (primo frame / pagina renderizzata).</summary>
    ContentReady,

    /// <summary>Il caricamento del contenuto è fallito.</summary>
    LoadFailed,

    /// <summary>Una dissolvenza del Tappo è arrivata in fondo.</summary>
    FadeCompleted,

    /// <summary>Ritorno al Tappo richiesto dall'operatore.</summary>
    Stop,

    /// <summary>Cambio slide / pagina dentro il contenuto in onda.</summary>
    Navigate,

    /// <summary>PANIC: Tappo immediato.</summary>
    Panic,

    /// <summary>Errore durante la proiezione.</summary>
    Fail
}
