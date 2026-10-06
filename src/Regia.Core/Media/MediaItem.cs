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

/// <summary>
/// Un file mandabile in onda. È una classe con identità per riferimento (NON un record): le impostazioni per file
/// (fine video, volume) si modificano sul posto, e un'uguaglianza/hash per valore romperebbe la ListBox, che
/// perderebbe la voce selezionata appena il volume cambia. La voce in lista e quella in onda sono lo stesso oggetto.
/// </summary>
public sealed class MediaItem
{
    public MediaItem(string path, MediaKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    public MediaKind Kind { get; }

    /// <summary>Fine video (vale solo per i video), ricordata per ogni file.</summary>
    public VideoEndAction VideoEnd { get; set; } = VideoEndAction.ReturnToTappo;

    /// <summary>Volume del video, 0-100, ricordato per ogni file.</summary>
    public int Volume { get; set; } = 100;

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
