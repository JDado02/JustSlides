using Serilog;

namespace Regia.Core.Input;

/// <summary>
/// I tasti in uso adesso: tiene la <see cref="KeyMap"/> corrente, la salva su disco a ogni modifica e avvisa chi ascolta
/// (finestra della regia, hook di tastiera), così i cambi dalle impostazioni valgono subito senza riavviare.
/// </summary>
public sealed class KeyBindings
{
    private readonly KeyMapStore _store;

    public KeyBindings(KeyMapStore store)
    {
        _store = store;
        Current = store.Load();
    }

    public KeyMap Current { get; private set; }

    /// <summary>La mappa è cambiata (sul thread che ha chiamato <see cref="Update"/>).</summary>
    public event Action? Changed;

    /// <summary>Applica la nuova mappa e la salva. Un errore di salvataggio si logga ma la mappa resta applicata.</summary>
    public void Update(KeyMap map)
    {
        Current = map;

        try
        {
            _store.Save(map);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile salvare keys.json: i tasti restano validi solo fino alla chiusura");
        }

        Changed?.Invoke();
    }
}
