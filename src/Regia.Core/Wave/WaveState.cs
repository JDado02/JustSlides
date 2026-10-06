namespace Regia.Core.Wave;

/// <summary>Stati espliciti dell'onda. La macchina a stati completa con i test arriva nella Milestone 2.</summary>
public enum WaveState
{
    Tappo,
    Caricamento,
    InTransizioneIn,
    InOnda,
    InTransizioneOut,
    Errore
}
