using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Regia.Core.Media;
using Regia.Core.Preflight;
using Regia.Output.Tappo;
using Serilog;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Regia.Output.Preflight;

/// <summary>
/// Controlli del pre-flight, uno per tipo di file. Girano su thread di background, sempre sulla COPIA LOCALE, e non
/// sollevano mai eccezioni: un file illeggibile è un esito <see cref="PreflightStatus.Error"/>. PowerPoint non viene
/// mai usato qui (R4): per i PPT si legge il pacchetto come ZIP.
/// </summary>
public static class FileChecks
{
    private static readonly Lazy<HashSet<string>> InstalledFonts = new(LoadInstalledFonts);

    public static async Task<PreflightResult> CheckAsync(MediaItem item, VlcService vlc, bool includeVideo, CancellationToken token)
    {
        try
        {
            if (!File.Exists(item.Path))
                return PreflightResult.Error("File non trovato nella copia locale");

            return item.Kind switch
            {
                MediaKind.Image => CheckImage(item.Path),
                MediaKind.Pdf => await CheckPdfAsync(item.Path, token),
                MediaKind.Ppt => CheckPpt(item.Path),
                MediaKind.Video => await CheckVideoAsync(item.Path, vlc, includeVideo, token),
                _ => PreflightResult.Error("Tipo di file non supportato")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-flight non riuscito: {Path}", item.RelativePath);
            return PreflightResult.Error("Controllo non riuscito: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- immagini

    private static PreflightResult CheckImage(string path)
    {
        try
        {
            // Si legge in memoria: il file non resta aperto e la copia locale si può sostituire.
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;

            var thumb = Thumbnails.EncodeJpeg(Thumbnails.Fit(frame));
            var details = new List<string> { $"Risoluzione: {width}×{height}", $"Dimensione: {FormatSize(bytes.Length)}" };

            if ((long)width * height > 120_000_000L)
                return PreflightResult.Warning($"{width}×{height}: immagine enorme", [.. details, "Immagine molto grande: la decodifica può essere lenta."], thumb);

            return PreflightResult.Ok($"{width}×{height}", details, thumb);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException or IOException)
        {
            return PreflightResult.Error("Immagine non leggibile o corrotta", ex.Message);
        }
    }

    // ---------------------------------------------------------------- PDF

    private static async Task<PreflightResult> CheckPdfAsync(string path, CancellationToken token)
    {
        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
            document = await PdfDocument.LoadFromFileAsync(file).AsTask(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PreflightResult.Error("PDF non leggibile o corrotto", ex.Message);
        }

        if (document.IsPasswordProtected)
            return PreflightResult.Error("PDF protetto da password");
        if (document.PageCount == 0)
            return PreflightResult.Error("PDF senza pagine");

        var pages = (int)document.PageCount;
        var details = new List<string> { $"Pagine: {pages}", $"Dimensione: {FormatSize(new FileInfo(path).Length)}" };

        byte[]? thumb = null;
        try
        {
            thumb = await RenderPdfPageAsync(document, 0, Thumbnails.Width, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PreflightResult.Warning($"{pages} pagine (prima pagina non renderizzabile)", [.. details, ex.Message]);
        }

        return PreflightResult.Ok(pages == 1 ? "1 pagina" : $"{pages} pagine", details, thumb);
    }

    /// <summary>Pagina di un PDF come JPEG della larghezza richiesta (sfondo bianco).</summary>
    public static async Task<byte[]> RenderPdfPageAsync(PdfDocument document, int index, int width, CancellationToken token)
    {
        using var page = document.GetPage((uint)index);
        if (page.Size.Width <= 0 || page.Size.Height <= 0)
            throw new InvalidOperationException("Pagina PDF con dimensioni non valide");

        var height = Math.Max(1, (int)Math.Round(width * page.Size.Height / page.Size.Width));
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)width,
            DestinationHeight = (uint)height,
            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options).AsTask(token);
        stream.Seek(0);

        using var managed = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await managed.CopyToAsync(buffer, token);
        buffer.Position = 0;

        var decoded = BitmapFrame.Create(buffer, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return Thumbnails.EncodeJpeg(decoded);
    }

    // ---------------------------------------------------------------- PowerPoint

    private static PreflightResult CheckPpt(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var isPackage = IsZip(path);

        if (!isPackage)
        {
            // .ppt/.pps = formato binario 97-2003; un .pptx che non è uno ZIP è quasi sempre protetto da password.
            if (ext is ".ppt" or ".pps")
            {
                return PreflightResult.Warning("Formato PowerPoint 97-2003: controllo limitato",
                [
                    "Numero di slide, font e collegamenti non verificabili senza aprire PowerPoint.",
                    "Consiglio: salvarlo come .pptx."
                ]);
            }

            return PreflightResult.Warning("Non leggibile: protetto da password o corrotto?",
            [
                "Il file non è un pacchetto PowerPoint valido.",
                "Se è protetto da password PowerPoint non riuscirà ad aprirlo: la messa in onda finirebbe in errore dopo il timeout."
            ]);
        }

        PptxInfo info;
        try
        {
            info = PptxInspector.Inspect(path);
        }
        catch (InvalidDataException ex)
        {
            return PreflightResult.Error("Presentazione non valida o corrotta", ex.Message);
        }

        var details = new List<string>
        {
            $"Slide: {info.Slides}",
            $"Formato: {info.AspectText}",
            $"Dimensione: {FormatSize(new FileInfo(path).Length)}"
        };
        var warnings = new List<string>();

        if (info.Slides == 0)
            return PreflightResult.Error("Presentazione senza slide", details.ToArray());

        var missingFonts = MissingFonts(info.Fonts);
        if (missingFonts.Count > 0)
            warnings.Add("Font non installati: " + string.Join(", ", missingFonts));

        if (info.MissingLinks.Count > 0)
            warnings.Add("Media collegati mancanti: " + string.Join(", ", info.MissingLinks) +
                         " (incorporali nella presentazione o mettili nella cartella)");

        if (ext is ".pps" or ".ppt")
            warnings.Add("Estensione del vecchio formato.");

        details.AddRange(warnings);
        if (info.Fonts.Count > 0)
            details.Add("Font usati: " + string.Join(", ", info.Fonts));

        var summary = $"{info.Slides} slide · {info.AspectText}";
        return warnings.Count > 0
            ? PreflightResult.Warning(summary + " · " + warnings[0], details, info.Thumbnail)
            : PreflightResult.Ok(summary, details, info.Thumbnail);
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[2];
            return stream.Read(header) == 2 && header[0] == 'P' && header[1] == 'K';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static List<string> MissingFonts(IReadOnlyList<string> used)
    {
        HashSet<string> installed;
        try
        {
            installed = InstalledFonts.Value;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Elenco dei font installati non disponibile: controllo font saltato");
            return [];
        }

        if (installed.Count == 0)
            return [];

        return used.Where(f => !installed.Contains(f)).ToList();
    }

    /// <summary>
    /// Nomi dei font installati: famiglie di WPF più i nomi del registro (per tutti gli utenti e per l'utente).
    /// Servono entrambi: WPF raggruppa "Calibri Light" dentro "Calibri", mentre PowerPoint la chiama per nome.
    /// </summary>
    private static HashSet<string> LoadInstalledFonts()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            foreach (var name in family.FamilyNames.Values)
                names.Add(name);
        }

        AddRegistryFonts(names, Microsoft.Win32.Registry.LocalMachine);
        AddRegistryFonts(names, Microsoft.Win32.Registry.CurrentUser);
        return names;
    }

    private static void AddRegistryFonts(HashSet<string> names, Microsoft.Win32.RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null)
                return;

            // Valori del tipo "Calibri Light (TrueType)" o "Cambria & Cambria Math (TrueType)".
            foreach (var value in key.GetValueNames())
            {
                var name = value;
                var paren = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (paren > 0)
                    name = name[..paren];

                foreach (var part in name.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    names.Add(part);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registro non leggibile: resta l'elenco di WPF.
        }
    }

    // ---------------------------------------------------------------- video

    private static async Task<PreflightResult> CheckVideoAsync(string path, VlcService vlc, bool includeFrame, CancellationToken token)
    {
        using var media = new Media(vlc.Instance, new Uri(Path.GetFullPath(path)));
        var status = await media.Parse(MediaParseOptions.ParseLocal, timeout: 8000, token);
        if (status is MediaParsedStatus.Failed or MediaParsedStatus.Timeout)
            return PreflightResult.Error(status == MediaParsedStatus.Timeout ? "Video: lettura troppo lenta" : "Video non leggibile da VLC");

        var tracks = media.Tracks;
        var video = tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        var audio = tracks.FirstOrDefault(t => t.TrackType == TrackType.Audio);
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, media.Duration));

        if (video.TrackType != TrackType.Video && audio.TrackType != TrackType.Audio)
            return PreflightResult.Error("Nessuna traccia audio o video riconosciuta");

        var details = new List<string> { $"Durata: {FormatDuration(duration)}", $"Dimensione: {FormatSize(new FileInfo(path).Length)}" };
        var warnings = new List<string>();
        byte[]? thumb = null;
        string summary;

        if (video.TrackType == TrackType.Video)
        {
            var width = video.Data.Video.Width;
            var height = video.Data.Video.Height;
            var codec = media.CodecDescription(TrackType.Video, video.Codec);
            summary = $"{width}×{height} · {FormatDuration(duration)}";
            details.Add($"Risoluzione: {width}×{height}");
            details.Add($"Codec video: {(string.IsNullOrWhiteSpace(codec) ? FourCc(video.Codec) : codec)}");

            if (width > 3840 || height > 2160)
                warnings.Add("Risoluzione oltre il 4K: decodifica pesante");
            else if (width >= 3840)
                details.Add("4K: richiede una GPU adeguata per la riproduzione fluida.");

            if (audio.TrackType != TrackType.Audio)
                details.Add("Nessuna traccia audio.");

            if (includeFrame)
            {
                using var grabber = new VideoFrameGrabber(vlc, path);
                var frame = await grabber.GrabAsync(duration, token);
                if (frame is not null)
                    thumb = Thumbnails.EncodeJpeg(Thumbnails.Fit(frame));
                else
                    details.Add("Anteprima del primo fotogramma non disponibile.");
            }
        }
        else
        {
            summary = $"Solo audio · {FormatDuration(duration)}";
            warnings.Add("Il file non ha video: in onda si vede solo il Tappo");
        }

        if (duration <= TimeSpan.Zero)
            warnings.Add("Durata sconosciuta: countdown e scorrimento non disponibili");

        details.AddRange(warnings);
        return warnings.Count > 0
            ? PreflightResult.Warning(summary + " · " + warnings[0], details, thumb)
            : PreflightResult.Ok(summary, details, thumb);
    }

    // ---------------------------------------------------------------- formati

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };

    private static string FourCc(uint codec)
    {
        var chars = new[] { (char)(codec & 0xFF), (char)((codec >> 8) & 0xFF), (char)((codec >> 16) & 0xFF), (char)((codec >> 24) & 0xFF) };
        return new string(chars).Trim('\0', ' ');
    }
}
