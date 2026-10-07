using System.ComponentModel;
using System.Runtime.CompilerServices;

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

/// <summary>Stato della copia locale di un file della cartella collegata.</summary>
public enum CopyState
{
    /// <summary>In attesa o in corso di copia: non si può ancora mandare in onda.</summary>
    Copying,

    /// <summary>La copia locale è completa e utilizzabile.</summary>
    Ready,

    /// <summary>Copia fallita (disco pieno, file illeggibile...): si riprova al giro successivo.</summary>
    Failed,

    /// <summary>Tipo di file non gestito dalla regia: visibile in lista ma non mandabile in onda.</summary>
    Unsupported
}

/// <summary>Esito del pre-flight di un file.</summary>
public enum PreflightStatus
{
    Pending,
    Ok,
    Warning,
    Error
}

/// <summary>
/// Un file mandabile in onda. È una classe con identità per riferimento (NON un record): le impostazioni per file
/// (fine video, volume) si modificano sul posto, e un'uguaglianza/hash per valore romperebbe la ListBox, che
/// perderebbe la voce selezionata appena il volume cambia. La voce in lista e quella in onda sono lo stesso oggetto.
/// Notifica i cambi di proprietà: la lista si aggiorna dal vivo (copia, pre-flight) e lo show si salva da solo.
/// </summary>
public sealed class MediaItem : INotifyPropertyChanged
{
    private string _session = "";
    private bool _sessionEdited;
    private string _speaker = "";
    private bool _excluded;
    private VideoEndAction _videoEnd = VideoEndAction.ReturnToTappo;
    private int _volume = 100;
    private CopyState _copyState = CopyState.Ready;
    private int _copyProgress;
    private string _statusDetail = "";
    private PreflightStatus _preflight = PreflightStatus.Ok;
    private string _preflightSummary = "";
    private IReadOnlyList<string> _preflightDetails = [];
    private string? _thumbnailPath;
    private bool _isUpdated;
    private bool _pendingUpdate;
    private bool _removedFromSource;

    public MediaItem(string path, MediaKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Percorso del file da riprodurre: la COPIA LOCALE per i file della cartella collegata.</summary>
    public string Path { get; }

    public MediaKind Kind { get; }

    /// <summary>Percorso relativo alla cartella collegata (chiave dell'identità del file); vuoto per la schermata di prova.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>Dimensione e data del file nella cartella collegata all'ultima copia riuscita.</summary>
    public long SourceSize { get; set; }

    public DateTime SourceMtimeUtc { get; set; }

    /// <summary>Voce fissa (schermata di prova): non si sposta, non si salva, non si rimuove.</summary>
    public bool IsFixed => Kind == MediaKind.TestPattern;

    /// <summary>Sessione della voce: di default il nome della sottocartella, modificabile a mano.</summary>
    public string Session
    {
        get => _session;
        set => Set(ref _session, value ?? "");
    }

    /// <summary>La sessione è stata scritta a mano: la scansione non la riscrive più.</summary>
    public bool SessionEdited
    {
        get => _sessionEdited;
        set => Set(ref _sessionEdited, value);
    }

    public string Speaker
    {
        get => _speaker;
        set => Set(ref _speaker, value ?? "");
    }

    /// <summary>Nascosta dalla scaletta (la cartella non viene toccata).</summary>
    public bool Excluded
    {
        get => _excluded;
        set => Set(ref _excluded, value);
    }

    /// <summary>Fine video (vale solo per i video), ricordata per ogni file.</summary>
    public VideoEndAction VideoEnd
    {
        get => _videoEnd;
        set => Set(ref _videoEnd, value);
    }

    /// <summary>Volume del video o del PowerPoint, 0-100, ricordato per ogni file.</summary>
    public int Volume
    {
        get => _volume;
        set => Set(ref _volume, value);
    }

    public CopyState CopyState
    {
        get => _copyState;
        set
        {
            if (Set(ref _copyState, value))
            {
                OnChanged(nameof(CanGoOnAir));
                OnChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Avanzamento della copia, 0-100.</summary>
    public int CopyProgress
    {
        get => _copyProgress;
        set
        {
            if (Set(ref _copyProgress, value))
                OnChanged(nameof(StatusText));
        }
    }

    /// <summary>Dettaglio dello stato (errore di copia, "in attesa che termini l'onda"...).</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        set => Set(ref _statusDetail, value ?? "");
    }

    public PreflightStatus Preflight
    {
        get => _preflight;
        set
        {
            if (Set(ref _preflight, value))
                OnChanged(nameof(CanGoOnAir));
        }
    }

    /// <summary>Riga di sintesi del pre-flight (tooltip della lista).</summary>
    public string PreflightSummary
    {
        get => _preflightSummary;
        set => Set(ref _preflightSummary, value ?? "");
    }

    /// <summary>Righe di dettaglio del pre-flight (pannello Preview).</summary>
    public IReadOnlyList<string> PreflightDetails
    {
        get => _preflightDetails;
        set => Set(ref _preflightDetails, value ?? []);
    }

    /// <summary>
    /// Titoli di tutte le slide di un PowerPoint, dal pre-flight (indice 0 = slide 1). Vuoto se non ancora letti o non
    /// leggibili: il Program mostra allora solo il numero. Non si salva nello show, si rilegge.
    /// </summary>
    public IReadOnlyList<string> SlideTitles { get; set; } = [];

    /// <summary>Numeri (1-based) delle slide nascoste di un PowerPoint: PowerPoint le salta. Dal pre-flight, non si salva.</summary>
    public IReadOnlyList<int> HiddenSlides { get; set; } = [];

    /// <summary>File della miniatura nella cache dello show; null = nessuna.</summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => Set(ref _thumbnailPath, value);
    }

    /// <summary>La copia è stata aggiornata dalla cartella: marcatore "AGGIORNATO" finché non si seleziona la voce.</summary>
    public bool IsUpdated
    {
        get => _isUpdated;
        set
        {
            if (Set(ref _isUpdated, value))
                OnChanged(nameof(StatusText));
        }
    }

    /// <summary>Il file è cambiato nella cartella ma è in onda: la copia si aggiorna appena si torna al Tappo.</summary>
    public bool PendingUpdate
    {
        get => _pendingUpdate;
        set
        {
            if (Set(ref _pendingUpdate, value))
                OnChanged(nameof(StatusText));
        }
    }

    /// <summary>Il file è sparito dalla cartella ma è in onda: la voce sparisce appena si torna al Tappo.</summary>
    public bool RemovedFromSource
    {
        get => _removedFromSource;
        set
        {
            if (Set(ref _removedFromSource, value))
                OnChanged(nameof(StatusText));
        }
    }

    /// <summary>Stato breve per la lista: copia, aggiornamento, rimozione in attesa.</summary>
    public string StatusText
    {
        get
        {
            if (Kind == MediaKind.TestPattern)
                return "";
            if (_removedFromSource)
                return "TOLTO DALLA CARTELLA";
            return _copyState switch
            {
                CopyState.Copying => $"Copia {_copyProgress}%",
                CopyState.Failed => "COPIA NON RIUSCITA",
                CopyState.Unsupported => "NON SUPPORTATO",
                _ when _pendingUpdate => "AGGIORNAMENTO IN ATTESA",
                _ when _isUpdated => "AGGIORNATO",
                _ => ""
            };
        }
    }

    /// <summary>Etichetta breve del tipo per la lista.</summary>
    public string KindText => Kind switch
    {
        MediaKind.Image => "IMG",
        MediaKind.Pdf => "PDF",
        MediaKind.Video => "VIDEO",
        MediaKind.Ppt => "PPT",
        MediaKind.TestPattern => "TEST",
        _ => "?"
    };

    /// <summary>Si può mandare in onda: copia pronta, tipo supportato, pre-flight senza errori.</summary>
    public bool CanGoOnAir =>
        Kind == MediaKind.TestPattern ||
        (_copyState == CopyState.Ready && Kind != MediaKind.Unknown && _preflight != PreflightStatus.Error);

    public string DisplayName => Kind == MediaKind.TestPattern
        ? "Schermata di prova"
        : System.IO.Path.GetFileName(string.IsNullOrEmpty(RelativePath) ? Path : RelativePath);

    /// <summary>Nome leggibile (log, accessibilità, automazione dei test dell'interfaccia).</summary>
    public override string ToString() => DisplayName;

    public static MediaItem FromPath(string path) => new(path, MediaKindDetector.FromPath(path));

    public static MediaItem TestPattern { get; } = new("", MediaKind.TestPattern);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
