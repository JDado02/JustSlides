namespace Regia.Core.Input;

/// <summary>Azioni dell'operatore che si possono associare a un tasto.</summary>
public enum KeyAction
{
    /// <summary>Manda in onda il file selezionato.</summary>
    Go,

    /// <summary>Tappo immediato da qualsiasi stato. Sempre associato a Esc.</summary>
    Panic,

    /// <summary>Slide o pagina successiva.</summary>
    Next,

    /// <summary>Slide o pagina precedente.</summary>
    Previous,

    /// <summary>Sposta la selezione in scaletta verso l'alto.</summary>
    SelectUp,

    /// <summary>Sposta la selezione in scaletta verso il basso.</summary>
    SelectDown,

    /// <summary>Play / Pausa del video in onda.</summary>
    PlayPause,

    /// <summary>Mute globale.</summary>
    Mute,

    /// <summary>Uscita normale dall'onda: dissolvenza al Tappo e chiusura del contenuto (come il pulsante "Torna al Tappo").
    /// Ultima nell'enum: se un tasto è già preso da un'altra azione vince quella.</summary>
    BackToTappo
}

public static class KeyActionInfo
{
    /// <summary>
    /// Se tenere premuto il tasto (autorepeat) deve ripetere l'azione. Solo spostamenti (slide, pagina, selezione):
    /// GO, PANIC, Torna al Tappo, Play/Pausa e Mute sono comandi singoli (un autorepeat li farebbe scattare a raffica
    /// o alternare). Unica regola, usata sia dalla finestra regia sia dall'hook di tastiera.
    /// </summary>
    public static bool IsRepeatable(KeyAction action) => action is
        KeyAction.Next or KeyAction.Previous or KeyAction.SelectUp or KeyAction.SelectDown;

    /// <summary>Nome mostrato all'operatore.</summary>
    public static string DisplayName(KeyAction action) => action switch
    {
        KeyAction.Go => "Manda in onda (GO)",
        KeyAction.Panic => "PANIC (Tappo immediato, senza dissolvenza)",
        KeyAction.Next => "Slide / pagina avanti",
        KeyAction.Previous => "Slide / pagina indietro",
        KeyAction.SelectUp => "Selezione su",
        KeyAction.SelectDown => "Selezione giù",
        KeyAction.PlayPause => "Play / Pausa video",
        KeyAction.Mute => "Mute",
        KeyAction.BackToTappo => "Torna al Tappo (con dissolvenza)",
        _ => action.ToString()
    };
}
