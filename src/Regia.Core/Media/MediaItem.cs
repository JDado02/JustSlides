namespace Regia.Core.Media;

public enum MediaKind
{
    Image,
    Pdf,
    Video,
    Ppt,
    TestPattern,
    Unknown
}

/// <summary>Cosa fare quando un video arriva in fondo.</summary>
public enum VideoEndAction
{
    /// <summary>Resta fermo sull'ultimo fotogramma, poi dissolvenza al Tappo.</summary>
    ReturnToTappo,

    /// <summary>Resta in onda fermo sull'ultimo fotogramma finché l'operatore non interviene.</summary>
    HoldLastFrame,

    /// <summary>Riparte dall'inizio all'infinito.</summary>
    Loop
}

/// <summary>Un file mandabile in onda.</summary>
public sealed record MediaItem(string Path, MediaKind Kind)
{
    /// <summary>Fine video (vale solo per i video).</summary>
    public VideoEndAction VideoEnd { get; init; } = VideoEndAction.ReturnToTappo;

    public string DisplayName => Kind == MediaKind.TestPattern ? "Schermata di prova" : System.IO.Path.GetFileName(Path);

    public static MediaItem FromPath(string path) => new(path, MediaKindDetector.FromPath(path));

    public static MediaItem TestPattern { get; } = new("", MediaKind.TestPattern);
}

/// <summary>Riconosce il tipo di file dall'estensione.</summary>
public static class MediaKindDetector
{
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v"];
    public static readonly string[] PptExtensions = [".ppt", ".pptx", ".pps", ".ppsx"];

    public static MediaKind FromPath(string? path)
    {
        var ext = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();

        if (ImageExtensions.Contains(ext))
            return MediaKind.Image;
        if (ext == ".pdf")
            return MediaKind.Pdf;
        if (VideoExtensions.Contains(ext))
            return MediaKind.Video;
        if (PptExtensions.Contains(ext))
            return MediaKind.Ppt;

        return MediaKind.Unknown;
    }
}
