using Regia.Core.Input;
using Regia.Core.Media;

namespace Regia.Core.Wave;

/// <summary>Testi del pannello Program (logica pura, testata): pagina successiva e etichette dei tasti.</summary>
public static class ProgramText
{
    /// <summary>
    /// "Prossima: 8/24 – Risultati 2025" per le slide, "Prossima: pagina 3/10" per i PDF. All'ultima slide il prossimo
    /// "avanti" porta al Tappo (lo si dice, è ciò che l'operatore deve sapere); all'ultima pagina di un PDF non succede nulla.
    /// Vuoto per i contenuti senza pagine.
    /// </summary>
    /// <param name="titles">Titoli di tutte le slide (indice 0 = slide 1); usati solo se sono tanti quante le slide.</param>
    /// <param name="hidden">Numeri (1-based) delle slide nascoste: PowerPoint le salta, quindi la "prossima" è la prima non nascosta.
    /// PowerPoint numera le slide per posizione nel file anche con nascoste (verificato: 1, 3, 4, 6...).</param>
    public static string NextPage(MediaKind kind, PageInfo? page, IReadOnlyList<string>? titles, IReadOnlyList<int>? hidden = null)
    {
        if (page is not { } p || p.Total <= 0)
            return "";

        var isSlide = kind == MediaKind.Ppt;

        var next = p.Current + 1;
        if (isSlide && hidden is { Count: > 0 })
        {
            while (next <= p.Total && hidden.Contains(next))
                next++;
        }

        if (next > p.Total)
            return isSlide ? "Ultima slide: avanti = Tappo" : "Ultima pagina";

        var text = $"Prossima: {(isSlide ? "" : "pagina ")}{next}/{p.Total}";

        if (isSlide && titles is { } list && list.Count == p.Total && !string.IsNullOrWhiteSpace(list[next - 1]))
            text += " – " + list[next - 1];

        return text;
    }

    /// <summary>"GO  (L / Spazio / Invio)": l'etichetta del pulsante segue i tasti configurati.</summary>
    public static string ButtonLabel(string name, KeyMap map, KeyAction action)
    {
        var chords = map.ChordsFor(action);
        return chords.Count == 0 ? name : $"{name}  ({string.Join(" / ", chords.Select(c => c.ToString()))})";
    }
}
