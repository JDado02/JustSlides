using Serilog;

namespace Regia.Core.Wave;

/// <summary>
/// Macchina a stati dell'onda: sincrona, senza dipendenze da UI. Ogni trigger è valido solo in
/// certi stati; quelli non validi vengono ignorati (stato invariato) e loggati.
/// </summary>
public sealed class WaveStateMachine
{
    private static readonly IReadOnlyDictionary<(WaveState, WaveTrigger), WaveState> Transitions = BuildTable();

    public WaveState State { get; private set; } = WaveState.Tappo;

    /// <summary>Sollevato a ogni cambio di stato effettivo (vecchio, nuovo).</summary>
    public event Action<WaveState, WaveState>? StateChanged;

    /// <summary>Indica se il trigger è valido nello stato corrente, senza applicarlo.</summary>
    public bool CanFire(WaveTrigger trigger) => Transitions.ContainsKey((State, trigger));

    /// <summary>Applica il trigger. Restituisce false (e logga) se non è valido nello stato corrente.</summary>
    public bool Fire(WaveTrigger trigger)
    {
        if (!Transitions.TryGetValue((State, trigger), out var next))
        {
            Log.Warning("Comando {Trigger} ignorato: stato {State}", trigger, State);
            return false;
        }

        var old = State;
        State = next;
        if (old != next)
        {
            Log.Information("Stato onda: {Old} -> {New} ({Trigger})", old, next, trigger);
            StateChanged?.Invoke(old, next);
        }

        return true;
    }

    /// <summary>Tabella pubblica delle transizioni valide: usata anche dai test.</summary>
    public static IReadOnlyDictionary<(WaveState, WaveTrigger), WaveState> Table => Transitions;

    private static Dictionary<(WaveState, WaveTrigger), WaveState> BuildTable()
    {
        var t = new Dictionary<(WaveState, WaveTrigger), WaveState>();

        // PANIC e Fail sono validi da qualsiasi stato: il Tappo deve sempre poter tornare.
        foreach (var state in Enum.GetValues<WaveState>())
        {
            t[(state, WaveTrigger.Panic)] = WaveState.Tappo;
            t[(state, WaveTrigger.Fail)] = WaveState.Errore;
        }

        t[(WaveState.Tappo, WaveTrigger.Go)] = WaveState.Caricamento;
        t[(WaveState.Errore, WaveTrigger.Go)] = WaveState.Caricamento;

        t[(WaveState.Caricamento, WaveTrigger.ContentReady)] = WaveState.InTransizioneIn;
        t[(WaveState.Caricamento, WaveTrigger.LoadFailed)] = WaveState.Errore;

        t[(WaveState.InTransizioneIn, WaveTrigger.FadeCompleted)] = WaveState.InOnda;

        t[(WaveState.InOnda, WaveTrigger.Stop)] = WaveState.InTransizioneOut;
        t[(WaveState.InOnda, WaveTrigger.Go)] = WaveState.InTransizioneOut; // cambio file via Tappo
        t[(WaveState.InOnda, WaveTrigger.Navigate)] = WaveState.InOnda;
        t[(WaveState.InOnda, WaveTrigger.Transport)] = WaveState.InOnda;

        t[(WaveState.InTransizioneOut, WaveTrigger.FadeCompleted)] = WaveState.Tappo;

        return t;
    }
}
