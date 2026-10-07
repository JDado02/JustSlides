using Regia.Core.Monitors;

namespace Regia.Core.Settings;

public enum TappoKind
{
    Image,
    Video
}

public sealed record TappoSettings
{
    public TappoKind Kind { get; init; } = TappoKind.Image;

    /// <summary>Percorso del file immagine o video. Vuoto = Tappo nero.</summary>
    public string Path { get; init; } = "";
}

/// <summary>Impostazioni dell'evento (monitor, Tappo, dissolvenza).</summary>
public sealed record AppSettings
{
    public const int MinFadeMs = 300;
    public const int MaxFadeMs = 1000;

    /// <summary>Monitor di output scelto; null = nessuno scelto.</summary>
    public MonitorId? OutputMonitor { get; init; }

    public bool SimulationMode { get; init; }

    public TappoSettings Tappo { get; init; } = new();

    public int FadeDurationMs { get; init; } = 500;

    /// <summary>ID endpoint CoreAudio del dispositivo di uscita audio dell'evento; vuoto = predefinito di Windows.</summary>
    public string AudioDeviceId { get; init; } = "";

    /// <summary>Nome del dispositivo scelto, per avvisare l'operatore se non è più collegato.</summary>
    public string AudioDeviceName { get; init; } = "";

    /// <summary>Taglio secco invece della dissolvenza.</summary>
    public bool HardCut { get; init; }

    /// <summary>Timeout heartbeat verso PptHost (usato dalla Milestone 4).</summary>
    public int PptHostTimeoutMs { get; init; } = 3000;

    /// <summary>Tempo massimo per aprire una presentazione (file grandi o su disco lento).</summary>
    public int PptOpenTimeoutMs { get; init; } = 30000;

    /// <summary>Riporta i valori entro i limiti ammessi.</summary>
    public AppSettings Normalize() => this with
    {
        FadeDurationMs = Math.Clamp(FadeDurationMs, MinFadeMs, MaxFadeMs),
        PptHostTimeoutMs = Math.Max(PptHostTimeoutMs, 500),
        PptOpenTimeoutMs = Math.Max(PptOpenTimeoutMs, 5000),
        Tappo = Tappo ?? new TappoSettings()
    };
}
