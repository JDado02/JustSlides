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
    Mute
}

public static class KeyActionInfo
{
    /// <summary>Nome mostrato all'operatore.</summary>
    public static string DisplayName(KeyAction action) => action switch
    {
        KeyAction.Go => "Manda in onda (GO)",
        KeyAction.Panic => "PANIC (Tappo)",
        KeyAction.Next => "Slide / pagina avanti",
        KeyAction.Previous => "Slide / pagina indietro",
        KeyAction.SelectUp => "Selezione su",
        KeyAction.SelectDown => "Selezione giù",
        KeyAction.PlayPause => "Play / Pausa video",
        KeyAction.Mute => "Mute",
        _ => action.ToString()
    };
}
