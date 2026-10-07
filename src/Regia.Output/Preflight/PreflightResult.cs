using Regia.Core.Media;

namespace Regia.Output.Preflight;

/// <summary>Esito del pre-flight di un file: stato, riga di sintesi, dettagli per la Preview, miniatura.</summary>
public sealed record PreflightResult(
    PreflightStatus Status,
    string Summary,
    IReadOnlyList<string> Details,
    byte[]? ThumbnailJpeg = null)
{
    /// <summary>Titoli di tutte le slide (solo PowerPoint); vuoto per il resto.</summary>
    public IReadOnlyList<string> SlideTitles { get; init; } = [];

    /// <summary>Numeri (1-based) delle slide nascoste (solo PowerPoint).</summary>
    public IReadOnlyList<int> HiddenSlides { get; init; } = [];

    public static PreflightResult Error(string summary, params string[] details) =>
        new(PreflightStatus.Error, summary, details);

    public static PreflightResult Warning(string summary, IReadOnlyList<string> details, byte[]? thumbnail = null) =>
        new(PreflightStatus.Warning, summary, details, thumbnail);

    public static PreflightResult Ok(string summary, IReadOnlyList<string> details, byte[]? thumbnail = null) =>
        new(PreflightStatus.Ok, summary, details, thumbnail);
}
