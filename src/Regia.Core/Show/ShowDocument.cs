using Regia.Core.Media;
using Regia.Core.Settings;

namespace Regia.Core.Show;

/// <summary>Una voce della scaletta come salvata nel file show.</summary>
public sealed record ShowItemDto
{
    /// <summary>Percorso relativo alla cartella collegata: è anche il percorso della copia nella cache.</summary>
    public string RelativePath { get; init; } = "";

    public string Session { get; init; } = "";

    public bool SessionEdited { get; init; }

    public string Speaker { get; init; } = "";

    public int Volume { get; init; } = 100;

    public VideoEndAction VideoEnd { get; init; } = VideoEndAction.ReturnToTappo;

    public bool Excluded { get; init; }

    /// <summary>Dimensione e data del file sorgente all'ultima copia riuscita (per riconoscere le modifiche al riavvio).</summary>
    public long SourceSize { get; init; }

    public DateTime SourceMtimeUtc { get; init; }
}

/// <summary>Il file show (JSON): impostazioni dell'evento, scaletta, selezione.</summary>
public sealed record ShowDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Identifica la cache locale dell'evento (<c>cache\&lt;EventId&gt;</c>).</summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    public AppSettings Settings { get; init; } = new();

    public List<ShowItemDto> Items { get; init; } = [];

    /// <summary>Percorso relativo della voce selezionata; null = nessuna (o schermata di prova).</summary>
    public string? SelectedPath { get; init; }

    public ShowDocument Normalize() => this with
    {
        EventId = string.IsNullOrWhiteSpace(EventId) ? Guid.NewGuid().ToString("N") : EventId,
        Settings = (Settings ?? new AppSettings()).Normalize(),
        Items = Items ?? []
    };
}
