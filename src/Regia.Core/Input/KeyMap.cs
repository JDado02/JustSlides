using System.Text.Json;
using Serilog;

namespace Regia.Core.Input;

/// <summary>Due azioni diverse con lo stesso tasto.</summary>
public sealed record KeyConflict(KeyChord Chord, KeyAction First, KeyAction Second);

/// <summary>
/// Associazione tasti → azioni, globale per l'app (non per evento). Immutabile: ogni modifica restituisce una mappa nuova.
/// Esc è sempre PANIC e non si può togliere né dare a un'altra azione.
/// </summary>
public sealed class KeyMap
{
    public static readonly KeyChord PanicChord = new(KeyChord.VkEscape);

    private readonly Dictionary<KeyAction, IReadOnlyList<KeyChord>> _bindings;

    private KeyMap(Dictionary<KeyAction, IReadOnlyList<KeyChord>> bindings)
    {
        _bindings = bindings;
    }

    /// <summary>L / Spazio / Invio = GO, frecce e PageUp/PageDown = pagina, Q / A = selezione su / giù, Esc = PANIC.</summary>
    public static KeyMap Default { get; } = Create(new Dictionary<KeyAction, IReadOnlyList<KeyChord>>
    {
        [KeyAction.Go] = [new KeyChord('L'), new KeyChord(KeyChord.VkSpace), new KeyChord(KeyChord.VkEnter)],
        [KeyAction.Panic] = [PanicChord],
        [KeyAction.Next] = [new KeyChord(KeyChord.VkRight), new KeyChord(KeyChord.VkDown), new KeyChord(KeyChord.VkPageDown)],
        [KeyAction.Previous] = [new KeyChord(KeyChord.VkLeft), new KeyChord(KeyChord.VkUp), new KeyChord(KeyChord.VkPageUp)],
        [KeyAction.SelectUp] = [new KeyChord('Q')],
        [KeyAction.SelectDown] = [new KeyChord('A')],
        [KeyAction.PlayPause] = [],
        [KeyAction.Mute] = []
    });

    /// <summary>Tasti dell'azione (mai null).</summary>
    public IReadOnlyList<KeyChord> ChordsFor(KeyAction action) =>
        _bindings.TryGetValue(action, out var chords) ? chords : [];

    /// <summary>Azione associata al tasto; null se nessuna. Con conflitti vince la prima in ordine di enum.</summary>
    public KeyAction? Find(KeyChord chord)
    {
        foreach (var action in Enum.GetValues<KeyAction>())
        {
            if (ChordsFor(action).Contains(chord))
                return action;
        }

        return null;
    }

    /// <summary>Tasti usati da due azioni diverse (vuoto = mappa valida).</summary>
    public IReadOnlyList<KeyConflict> Conflicts()
    {
        var seen = new Dictionary<KeyChord, KeyAction>();
        var conflicts = new List<KeyConflict>();

        foreach (var action in Enum.GetValues<KeyAction>())
        {
            foreach (var chord in ChordsFor(action))
            {
                if (seen.TryGetValue(chord, out var first) && first != action)
                    conflicts.Add(new KeyConflict(chord, first, action));
                else
                    seen[chord] = action;
            }
        }

        return conflicts;
    }

    /// <summary>Aggiunge un tasto all'azione. Esc e i soli modificatori sono rifiutati (restano com'erano).</summary>
    public KeyMap With(KeyAction action, KeyChord chord)
    {
        if (chord == PanicChord || KeyChord.IsModifierKey(chord.VirtualKey) || ChordsFor(action).Contains(chord))
            return this;

        return Replace(action, [.. ChordsFor(action), chord]);
    }

    /// <summary>Toglie un tasto dall'azione. Esc non si toglie da PANIC.</summary>
    public KeyMap Without(KeyAction action, KeyChord chord)
    {
        if (action == KeyAction.Panic && chord == PanicChord)
            return this;

        return Replace(action, ChordsFor(action).Where(c => c != chord).ToList());
    }

    private KeyMap Replace(KeyAction action, IReadOnlyList<KeyChord> chords)
    {
        var copy = new Dictionary<KeyAction, IReadOnlyList<KeyChord>>(_bindings) { [action] = chords };
        return Create(copy);
    }

    private static KeyMap Create(Dictionary<KeyAction, IReadOnlyList<KeyChord>> bindings)
    {
        var normalized = new Dictionary<KeyAction, IReadOnlyList<KeyChord>>();
        foreach (var action in Enum.GetValues<KeyAction>())
        {
            var chords = bindings.TryGetValue(action, out var list) ? list : [];

            // Esc è solo di PANIC; nessun doppione; niente tasti-modificatore da soli.
            chords = chords
                .Where(c => action == KeyAction.Panic || c != PanicChord)
                .Where(c => !KeyChord.IsModifierKey(c.VirtualKey))
                .Distinct()
                .ToList();

            if (action == KeyAction.Panic && !chords.Contains(PanicChord))
                chords = [PanicChord, .. chords];

            normalized[action] = chords;
        }

        return new KeyMap(normalized);
    }

    // ---------------------------------------------------------------- JSON

    /// <summary>Forma su disco: nome azione → elenco di testi ("Ctrl+L"). Leggibile e modificabile a mano.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(
            Enum.GetValues<KeyAction>().ToDictionary(a => a.ToString(), a => ChordsFor(a).Select(c => c.ToString()).ToArray()),
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Mai un'eccezione: JSON illeggibile → default; azioni o tasti sconosciuti vengono ignorati; azioni mancanti → default.</summary>
    public static KeyMap FromJson(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            if (raw is null)
                return Default;

            var bindings = new Dictionary<KeyAction, IReadOnlyList<KeyChord>>();
            foreach (var action in Enum.GetValues<KeyAction>())
            {
                if (!raw.TryGetValue(action.ToString(), out var texts) || texts is null)
                {
                    bindings[action] = Default.ChordsFor(action);
                    continue;
                }

                var chords = new List<KeyChord>();
                foreach (var text in texts)
                {
                    if (KeyChord.TryParse(text, out var chord))
                        chords.Add(chord);
                    else
                        Log.Warning("Tasto non riconosciuto in keys.json ignorato: {Text}", text);
                }

                bindings[action] = chords;
            }

            return Create(bindings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "keys.json non leggibile: uso i tasti predefiniti");
            return Default;
        }
    }
}
