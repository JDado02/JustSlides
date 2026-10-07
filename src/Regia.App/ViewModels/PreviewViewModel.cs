using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Regia.Core.Media;
using Regia.Output.Preflight;
using Serilog;

namespace Regia.App.ViewModels;

/// <summary>
/// Una pagina nella striscia della Preview dei PDF. Classe (non record) con <c>ToString</c> esplicito: l'accessibilità
/// di WPF chiama <c>ToString()</c> per il nome dell'elemento, e quello di un record stamperebbe l'immagine, che nasce su
/// un thread di background (eccezione "oggetto di proprietà di un altro thread").
/// </summary>
public sealed class PdfPageThumb(int number, ImageSource image)
{
    public int Number { get; } = number;

    public ImageSource Image { get; } = image;

    public override string ToString() => $"Pagina {Number}";
}

/// <summary>
/// Preview della voce selezionata: immagine grande, pagine del PDF, primo fotogramma del video, miniatura del
/// PowerPoint e righe del pre-flight. Solo anteprima: l'onda non passa mai da qui. Ogni caricamento è annullabile
/// e un risultato in ritardo di una selezione precedente viene scartato.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private const int ImageDecodeWidth = 1280;
    private const int PdfThumbWidth = 220;

    private MediaItem? _item;
    private CancellationTokenSource? _cts;
    private int _version;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private ImageSource? _image;

    [ObservableProperty]
    private string _placeholder = "Nessuna selezione";

    [ObservableProperty]
    private PreflightStatus _status = PreflightStatus.Pending;

    [ObservableProperty]
    private IReadOnlyList<string> _lines = [];

    public ObservableCollection<PdfPageThumb> Pages { get; } = [];

    public bool HasImage => Image is not null;

    public bool HasPages => Pages.Count > 0;

    /// <summary>Mostra la voce (o niente). Va chiamato dal thread UI.</summary>
    public void Show(MediaItem? item)
    {
        if (_item is not null)
            _item.PropertyChanged -= OnItemChanged;

        _item = item;
        if (item is not null)
            item.PropertyChanged += OnItemChanged;

        Reload();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Solo i cambi che modificano l'anteprima (non la percentuale di copia, né i campi dell'elenco).
        if (e.PropertyName is nameof(MediaItem.ThumbnailPath) or nameof(MediaItem.CopyState)
            or nameof(MediaItem.Preflight) or nameof(MediaItem.PreflightDetails) or nameof(MediaItem.PreflightSummary)
            or nameof(MediaItem.StatusDetail))
        {
            Reload();
        }
    }

    private void Reload()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var version = ++_version;
        var token = _cts.Token;
        var item = _item;

        Pages.Clear();
        OnPropertyChanged(nameof(HasPages));

        if (item is null)
        {
            Title = "";
            Subtitle = "";
            Image = null;
            Lines = [];
            Status = PreflightStatus.Pending;
            Placeholder = "Nessuna selezione";
            return;
        }

        Title = item.DisplayName;
        Status = item.Preflight;

        if (item.Kind == MediaKind.TestPattern)
        {
            Subtitle = "Schermata di prova";
            Image = null;
            Lines = ["Mira per controllare posizione, bordi e proporzioni dell'output."];
            Placeholder = "Schermata di prova";
            return;
        }

        Subtitle = KindName(item.Kind) + (string.IsNullOrEmpty(item.PreflightSummary) ? "" : " · " + item.PreflightSummary);
        Lines = BuildLines(item);

        if (item.CopyState != CopyState.Ready)
        {
            Image = null;
            Placeholder = item.CopyState switch
            {
                CopyState.Copying => $"Copia in corso... {item.CopyProgress}%",
                CopyState.Failed => "Copia non riuscita",
                _ => "Tipo di file non supportato"
            };
            return;
        }

        Placeholder = "Anteprima in preparazione...";
        _ = LoadAsync(item, version, token);
    }

    private static IReadOnlyList<string> BuildLines(MediaItem item)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(item.StatusDetail))
            lines.Add(item.StatusDetail);
        lines.AddRange(item.PreflightDetails);
        if (lines.Count == 0 && item.CopyState == CopyState.Ready)
            lines.Add("Controllo in corso...");
        return lines;
    }

    private async Task LoadAsync(MediaItem item, int version, CancellationToken token)
    {
        try
        {
            ImageSource? image = null;
            switch (item.Kind)
            {
                case MediaKind.Image:
                    image = await Task.Run(() => Thumbnails.Load(item.Path, ImageDecodeWidth), token);
                    break;

                case MediaKind.Pdf:
                    image = await Task.Run(() => Thumbnails.Load(item.ThumbnailPath), token);
                    _ = LoadPdfPagesAsync(item, version, token);
                    break;

                default: // video e PowerPoint: miniatura del pre-flight
                    image = await Task.Run(() => Thumbnails.Load(item.ThumbnailPath), token);
                    break;
            }

            if (version != _version)
                return;

            Image = image;
            if (image is null)
                Placeholder = item.Preflight == PreflightStatus.Pending ? "Anteprima in preparazione..." : "Anteprima non disponibile";
        }
        catch (OperationCanceledException)
        {
            // Selezione cambiata.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Anteprima non caricata: {Path}", item.RelativePath);
            if (version == _version)
            {
                Image = null;
                Placeholder = "Anteprima non disponibile";
            }
        }
    }

    private async Task LoadPdfPagesAsync(MediaItem item, int version, CancellationToken token)
    {
        var context = SynchronizationContext.Current;

        void Add(int number, ImageSource image)
        {
            void Apply()
            {
                if (version != _version)
                    return;

                Pages.Add(new PdfPageThumb(number, image));
                OnPropertyChanged(nameof(HasPages));
            }

            if (context is null)
                Apply();
            else
                context.Post(_ => Apply(), null);
        }

        try
        {
            await Task.Run(() => PdfPreview.RenderAsync(item.Path, PdfThumbWidth, Add, token), token);
        }
        catch (OperationCanceledException)
        {
            // Selezione cambiata.
        }
    }

    private static string KindName(MediaKind kind) => kind switch
    {
        MediaKind.Image => "Immagine",
        MediaKind.Pdf => "PDF",
        MediaKind.Video => "Video",
        MediaKind.Ppt => "PowerPoint",
        _ => "File"
    };
}
