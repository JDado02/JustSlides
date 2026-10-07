namespace Regia.Core.Input;

/// <summary>Spostamento della selezione in una lista (tasti "selezione su / giù"): si ferma ai bordi, mai circolare.</summary>
public static class SelectionStep
{
    /// <summary>
    /// Indice della voce da selezionare partendo da <paramref name="current"/> (-1 = nessuna) e spostandosi di
    /// <paramref name="delta"/>. Senza selezione: giù prende la prima voce, su l'ultima. Lista vuota → -1.
    /// </summary>
    public static int Move(int count, int current, int delta)
    {
        if (count <= 0)
            return -1;

        if (current < 0)
            return delta > 0 ? 0 : count - 1;

        return Math.Clamp(current + delta, 0, count - 1);
    }
}
