namespace Regia.Core.Ppt;

public enum SlideMove
{
    /// <summary>Si può inviare il comando a PowerPoint.</summary>
    Advance,

    /// <summary>Ultima slide senza animazioni rimaste: "avanti" significa fine della presentazione.</summary>
    AtEnd,

    /// <summary>Ultima slide con "ciclo continuo" attivo nel file: "avanti" ricomincia dalla prima slide.</summary>
    Restart,

    /// <summary>Prima slide: "indietro" non fa nulla.</summary>
    AtStart
}

/// <summary>
/// Decide se "avanti/indietro" va inoltrato a PowerPoint. Serve a non far mai comparire la schermata
/// nera "Fine della presentazione": su <see cref="SlideMove.AtEnd"/> la regia non chiama <c>Next</c>
/// ma passa al Tappo.
/// </summary>
public static class SlideNavigator
{
    /// <param name="position">Slide corrente (1-based).</param>
    /// <param name="total">Numero di slide.</param>
    /// <param name="clicksRemaining">Click di animazione ancora da fare sulla slide corrente.</param>
    /// <param name="loop">La presentazione è impostata a ciclo continuo: oltre l'ultima slide si ricomincia.</param>
    public static SlideMove DecideNext(int position, int total, int clicksRemaining, bool loop = false) =>
        position >= total && clicksRemaining <= 0 ? (loop ? SlideMove.Restart : SlideMove.AtEnd) : SlideMove.Advance;

    public static SlideMove DecidePrevious(int position) =>
        position <= 1 ? SlideMove.AtStart : SlideMove.Advance;
}
