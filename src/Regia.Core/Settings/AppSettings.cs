using Regia.Core.Monitors;

namespace Regia.Core.Settings;

public enum TappoKind
{
    Image,
    Video,

    /// <summary>Presentazione PowerPoint esportata una volta in immagini (<see cref="TappoSettings.SlidesDir"/>): a runtime PowerPoint non c'entra.</summary>
    Slides
}

public enum TappoSlidesMode
{
    /// <summary>Le slide scorrono da sole e ricominciano.</summary>
    Loop,

    /// <summary>Resta ferma sulla slide scelta (<see cref="TappoSettings.SlideIndex"/>), che si cambia solo dalle frecce a schermo.</summary>
    Hold
}

public sealed record TappoSettings
{
    public TappoKind Kind { get; init; } = TappoKind.Image;

    /// <summary>Percorso del file immagine o video. Vuoto = Tappo nero.</summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// Tappo immagine con più immagini in loop (con <see cref="SlideSeconds"/> per immagine). Vuota = si usa solo <see cref="Path"/>.
    /// La prima è sempre uguale a <see cref="Path"/>.
    /// </summary>
    public IReadOnlyList<string> ImagePaths { get; init; } = [];

    /// <summary>
    /// Tappo immagine: true = le immagini scorrono in loop, false = resta fissa la prima. Null (show della 1.2.0 o precedenti) =
    /// loop se la lista ha due o più immagini.
    /// </summary>
    public bool? ImageLoop { get; init; }

    /// <summary>Loop effettivo del Tappo immagine: serve almeno una seconda immagine.</summary>
    public bool ImageLoopEffective => (ImageLoop ?? ImagePaths.Count >= 2) && ImagePaths.Count >= 2;

    /// <summary>Immagini del Tappo: la lista se c'è, altrimenti il solo <see cref="Path"/>.</summary>
    public IReadOnlyList<string> EffectiveImages =>
        ImagePaths is { Count: > 0 } ? ImagePaths : string.IsNullOrWhiteSpace(Path) ? [] : [Path];

    public const int MinSlideSeconds = 2;
    public const int MaxSlideSeconds = 120;

    /// <summary>Tappo PowerPoint: loop o fermo su una slide.</summary>
    public TappoSlidesMode SlidesMode { get; init; } = TappoSlidesMode.Loop;

    /// <summary>Tappo PowerPoint in loop: secondi per slide.</summary>
    public int SlideSeconds { get; init; } = 6;

    /// <summary>Tappo PowerPoint fermo: slide mostrata (1-based). Si ricorda anche dopo un'onda e al riavvio.</summary>
    public int SlideIndex { get; init; } = 1;

    /// <summary>Cartella con le immagini esportate (<c>slide-001.png</c>...). Vuota = non ancora esportate.</summary>
    public string SlidesDir { get; init; } = "";

    /// <summary>Numero di slide esportate.</summary>
    public int SlideCount { get; init; }

    public TappoSettings Normalized()
    {
        var images = (ImagePaths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Con una lista, Path è la prima immagine; senza lista resta com'è (vecchi file show).
        return (this with
        {
            ImagePaths = images.Count == 0 ? [] : images.ToArray(),
            Path = images.Count > 0 ? images[0] : (Path ?? "")
        }).NormalizedRest();
    }

    private TappoSettings NormalizedRest() => this with
    {
        SlidesDir = SlidesDir ?? "",
        SlideSeconds = Math.Clamp(SlideSeconds, MinSlideSeconds, MaxSlideSeconds),
        SlideCount = Math.Max(SlideCount, 0),
        SlideIndex = Math.Clamp(SlideIndex, 1, Math.Max(SlideCount, 1))
    };
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

    /// <summary>Cartella collegata: i file che ci si mettono compaiono da soli in scaletta. Vuoto = non configurata.</summary>
    public string SourceFolder { get; init; } = "";

    /// <summary>Riporta i valori entro i limiti ammessi.</summary>
    public AppSettings Normalize() => this with
    {
        FadeDurationMs = Math.Clamp(FadeDurationMs, MinFadeMs, MaxFadeMs),
        PptHostTimeoutMs = Math.Max(PptHostTimeoutMs, 500),
        PptOpenTimeoutMs = Math.Max(PptOpenTimeoutMs, 5000),
        Tappo = (Tappo ?? new TappoSettings()).Normalized(),
        SourceFolder = SourceFolder?.Trim() ?? ""
    };
}
