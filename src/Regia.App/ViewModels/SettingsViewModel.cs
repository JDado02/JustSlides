using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Regia.Core.Media;
using Regia.Core.Monitors;
using Regia.Core.Ppt;
using Regia.Core.Settings;
using Regia.Output.Audio;
using Regia.Output.Ppt;

namespace Regia.App.ViewModels;

/// <summary>Voce della lista "Uscita audio". Id vuoto = predefinito di Windows.</summary>
public sealed record AudioDeviceItem(string Id, string Name, string Display);

public sealed class MonitorItem
{
    public MonitorItem(MonitorInfo info, int number, bool selectable)
    {
        Info = info;
        IsSelectable = selectable;
        var scale = info.Dpi * 100 / 96;
        Display = $"{number} · {info.FriendlyName} — {info.Width}×{info.Height} ({scale}%)"
                  + (info.IsPrimary ? " — regia" : "");
        Tooltip = info.DevicePath;
    }

    public MonitorInfo Info { get; }

    public bool IsSelectable { get; }

    public string Display { get; }

    public string Tooltip { get; }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Func<AppSettings, Task<string?>> _apply;
    private readonly Action _identify;
    private readonly AppSettings _current;

    /// <summary>Sezione "Aggiornamenti": solo su richiesta, agisce subito (non passa da "Applica").</summary>
    public UpdateViewModel? Updates { get; init; }

    /// <summary>Editor dei tasti: globali dell'app, si salvano subito (non passano da "Applica").</summary>
    public KeyBindingsViewModel? Keys { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimulationFallbackNote))]
    [NotifyPropertyChangedFor(nameof(HasSimulationFallbackNote))]
    private MonitorItem? _selectedMonitor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimulationFallbackNote))]
    [NotifyPropertyChangedFor(nameof(HasSimulationFallbackNote))]
    private bool _simulationMode;

    /// <summary>
    /// Simulazione tolta ma nessun monitor di output utilizzabile: la regia resta comunque in simulazione (scelta di sicurezza:
    /// mai senza uscita, mai coprire la regia). Lo si dice qui invece di lasciare la cornice accesa senza spiegazioni.
    /// </summary>
    public string? SimulationFallbackNote => !SimulationMode && SelectedMonitor is not { IsSelectable: true }
        ? "Nessun monitor di output scelto: finché non ne scegli uno (collegato e diverso da quello della regia) la regia resta in " +
          "simulazione e la cornice di simulazione rimane aperta."
        : null;

    public bool HasSimulationFallbackNote => SimulationFallbackNote is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageTappo))]
    [NotifyPropertyChangedFor(nameof(IsVideoTappo))]
    [NotifyPropertyChangedFor(nameof(IsSlidesTappo))]
    [NotifyPropertyChangedFor(nameof(ShowSeconds))]
    [NotifyPropertyChangedFor(nameof(SecondsLabel))]
    [NotifyPropertyChangedFor(nameof(TappoNote))]
    [NotifyPropertyChangedFor(nameof(HasTappoNote))]
    private TappoKind _tappoKind;

    /// <summary>Tappo PowerPoint: true = fermo su una slide, false = loop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlidesLoop))]
    [NotifyPropertyChangedFor(nameof(ShowSeconds))]
    [NotifyPropertyChangedFor(nameof(TappoNote))]
    [NotifyPropertyChangedFor(nameof(HasTappoNote))]
    private bool _slidesHold;

    /// <summary>Tappo immagine: true = più immagini in loop, false = una sola, fissa.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageFixed))]
    [NotifyPropertyChangedFor(nameof(ShowSeconds))]
    [NotifyPropertyChangedFor(nameof(TappoNote))]
    [NotifyPropertyChangedFor(nameof(HasTappoNote))]
    private bool _imageLoop;

    public bool IsImageFixed
    {
        get => !ImageLoop;
        set => ImageLoop = !value;
    }

    // Passando a "Fissa" resta una sola immagine (la prima della lista).
    partial void OnImageLoopChanged(bool value)
    {
        if (value)
            return;

        while (TappoImages.Count > 1)
            TappoImages.RemoveAt(TappoImages.Count - 1);
    }

    /// <summary>Secondi per slide/immagine, scritti a tastiera (si valida con "Applica").</summary>
    [ObservableProperty]
    private string _slideSecondsText = "6";

    /// <summary>Immagini del Tappo (tipo "Immagine"): con due o più scorrono in loop.</summary>
    public ObservableCollection<string> TappoImages { get; } = [];

    [ObservableProperty]
    private string? _selectedTappoImage;

    /// <summary>I secondi servono solo al loop delle slide PowerPoint o a più immagini.</summary>
    public bool ShowSeconds => (TappoKind == TappoKind.Slides && !SlidesHold) || (TappoKind == TappoKind.Image && ImageLoop && TappoImages.Count >= 2);

    public string SecondsLabel => TappoKind == TappoKind.Image ? "Secondi per immagine" : "Secondi per slide";

    /// <summary>Nota breve sotto la scelta del Tappo (null = nessuna).</summary>
    public string? TappoNote => TappoKind switch
    {
        TappoKind.Slides when SlidesHold =>
            "Come Tappo resta la slide selezionata: cambia solo a mano, con le frecce sotto il Program.",
        TappoKind.Slides =>
            "La presentazione viene trasformata in immagini: video e animazioni non vengono riprodotti nel Tappo.",
        TappoKind.Image when ImageLoop && TappoImages.Count >= 2 =>
            "Le immagini scorrono in loop con una dissolvenza incrociata, nell'ordine della lista.",
        TappoKind.Image when !ImageLoop && TappoImages.Count == 1 =>
            "Come Tappo resta sempre questa immagine.",
        _ => null
    };

    public bool HasTappoNote => TappoNote is not null;

    /// <summary>
    /// Esporta il PPT del Tappo in immagini (una volta). Lancia se non si può (qualcosa in onda, PowerPoint dell'utente aperto, errore).
    /// Null = non disponibile.
    /// </summary>
    public Func<string, CancellationToken, Task<TappoSlidesInfo>>? ExportTappoSlides { get; init; }

    [ObservableProperty]
    private string _tappoPath = "";

    /// <summary>Cartella contenuti collegata: i file che ci si mettono compaiono da soli in scaletta.</summary>
    [ObservableProperty]
    private string _sourceFolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PptAudioNote))]
    [NotifyPropertyChangedFor(nameof(HasPptAudioNote))]
    private AudioDeviceItem? _selectedAudioDevice;

    private readonly string? _defaultAudioId = DefaultDeviceMonitor.GetDefaultId();

    /// <summary>Avviso informativo: con questa scelta PowerPoint suonerebbe su un altro dispositivo. Null se non serve.</summary>
    public string? PptAudioNote => SelectedAudioDevice is { } d
        ? Regia.Core.Audio.AudioDeviceWarning.Evaluate(d.Id, d.Name, _defaultAudioId, hasPpt: true)
        : null;

    public bool HasPptAudioNote => PptAudioNote is not null;

    // --- Verifica PowerPoint ---------------------------------------------------------------------------------------

    /// <summary>Esegue la verifica (installazione, attivazione, prova tecnica). Null = non disponibile.</summary>
    public Func<CancellationToken, Task<PptCheckReport>>? PptCheck { get; init; }

    /// <summary>La verifica avvia PowerPoint: si può fare solo con la regia a Tappo/Errore, mai con qualcosa in onda.</summary>
    public Func<bool>? CanCheckPpt { get; init; }

    private CancellationTokenSource? _pptCheckCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyPptCommand))]
    [NotifyPropertyChangedFor(nameof(PptCheckButtonText))]
    private bool _isPptChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPptCheckResult))]
    private string? _pptCheckSummary;

    [ObservableProperty]
    private PptCheckLevel _pptCheckLevel;

    public ObservableCollection<string> PptCheckLines { get; } = [];

    public bool HasPptCheckResult => !string.IsNullOrEmpty(PptCheckSummary);

    public string PptCheckButtonText => IsPptChecking ? "Verifica in corso..." : "Verifica PowerPoint";

    private bool CanVerifyPpt() => !IsPptChecking && PptCheck is not null;

    [RelayCommand(CanExecute = nameof(CanVerifyPpt))]
    private async Task VerifyPptAsync()
    {
        if (PptCheck is not { } check)
            return;

        PptCheckLines.Clear();
        if (CanCheckPpt is { } canCheck && !canCheck())
        {
            PptCheckLevel = PptCheckLevel.Warning;
            PptCheckSummary = "La verifica avvia PowerPoint: si può fare solo con la regia a Tappo, non durante un'onda.";
            return;
        }

        PptCheckSummary = null;
        IsPptChecking = true;
        var cts = _pptCheckCts = new CancellationTokenSource();
        try
        {
            var report = await check(cts.Token);
            PptCheckLevel = report.Level;
            foreach (var line in report.Lines)
                PptCheckLines.Add(line);

            PptCheckSummary = report.Summary;
        }
        catch (OperationCanceledException)
        {
            // Finestra chiusa durante la verifica: nessun risultato da mostrare.
        }
        catch (Exception ex)
        {
            PptCheckLevel = PptCheckLevel.Error;
            PptCheckSummary = "La verifica non è riuscita: " + ex.Message;
        }
        finally
        {
            IsPptChecking = false;
        }
    }

    /// <summary>Alla chiusura della finestra: si smette di aspettare la verifica in corso.</summary>
    public void CancelPptCheck() => _pptCheckCts?.Cancel();

    [ObservableProperty]
    private double _fadeDurationMs;

    [ObservableProperty]
    private bool _hardCut;

    [ObservableProperty]
    private string? _status;

    public SettingsViewModel(
        AppSettings current,
        IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<AudioDeviceInfo> audioDevices,
        Func<AppSettings, Task<string?>> apply,
        Action identify)
    {
        _apply = apply;
        _identify = identify;
        _current = current;

        for (var i = 0; i < monitors.Count; i++)
        {
            // Il monitor della regia non è mai selezionabile come output.
            Monitors.Add(new MonitorItem(monitors[i], i + 1, selectable: !monitors[i].IsPrimary));
        }

        var match = MonitorMatcher.Find(current.OutputMonitor, monitors);
        SelectedMonitor = match is null ? null : Monitors.FirstOrDefault(m => m.Info == match);

        AudioDevices.Add(new AudioDeviceItem("", "", "Predefinito di Windows"));
        foreach (var device in audioDevices)
            AudioDevices.Add(new AudioDeviceItem(device.Id, device.Name, device.IsDefault ? device.Name + " (predefinito)" : device.Name));

        // Dispositivo salvato ma non collegato ora: lo conservo in lista, così non lo perdo.
        if (current.AudioDeviceId.Length > 0 && AudioDevices.All(a => a.Id != current.AudioDeviceId))
            AudioDevices.Add(new AudioDeviceItem(current.AudioDeviceId, current.AudioDeviceName, current.AudioDeviceName + " (non collegato)"));
        SelectedAudioDevice = AudioDevices.FirstOrDefault(a => a.Id == current.AudioDeviceId) ?? AudioDevices[0];

        SimulationMode = current.SimulationMode;
        TappoImages.CollectionChanged += (_, _) => OnTappoImagesChanged();
        if (current.Tappo.Kind == TappoKind.Image)
        {
            foreach (var image in current.Tappo.EffectiveImages)
                TappoImages.Add(image);
        }

        ImageLoop = current.Tappo.ImageLoopEffective;
        TappoKind = current.Tappo.Kind;
        SlidesHold = current.Tappo.SlidesMode == TappoSlidesMode.Hold;
        SlideSecondsText = current.Tappo.SlideSeconds.ToString();
        TappoPath = current.Tappo.Path;
        SourceFolder = current.SourceFolder;
        FadeDurationMs = current.FadeDurationMs;
        HardCut = current.HardCut;
    }

    public ObservableCollection<MonitorItem> Monitors { get; } = [];

    public ObservableCollection<AudioDeviceItem> AudioDevices { get; } = [];

    // Tre scelte esclusive (RadioButton): si agisce solo quando una diventa vera.
    public bool IsImageTappo
    {
        get => TappoKind == TappoKind.Image;
        set { if (value) TappoKind = TappoKind.Image; }
    }

    public bool IsVideoTappo
    {
        get => TappoKind == TappoKind.Video;
        set { if (value) TappoKind = TappoKind.Video; }
    }

    public bool IsSlidesTappo
    {
        get => TappoKind == TappoKind.Slides;
        set { if (value) TappoKind = TappoKind.Slides; }
    }

    public bool SlidesLoop
    {
        get => !SlidesHold;
        set => SlidesHold = !value;
    }

    private void OnTappoImagesChanged()
    {
        OnPropertyChanged(nameof(ShowSeconds));
        OnPropertyChanged(nameof(TappoNote));
        OnPropertyChanged(nameof(HasTappoNote));
    }

    [RelayCommand]
    private void AddTappoImages()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Aggiungi immagini al Tappo",
            Filter = "Immagini|*.jpg;*.jpeg;*.png;*.bmp|Tutti i file|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        foreach (var file in dialog.FileNames)
        {
            if (!TappoImages.Contains(file, StringComparer.OrdinalIgnoreCase))
                TappoImages.Add(file);
        }

        // Aggiungere più immagini = loop.
        if (TappoImages.Count >= 2)
            ImageLoop = true;

        TappoKind = TappoKind.Image;
    }

    [RelayCommand]
    private void RemoveTappoImage()
    {
        if (SelectedTappoImage is { } image)
            TappoImages.Remove(image);
    }

    [RelayCommand]
    private void MoveTappoImageUp() => MoveSelectedImage(-1);

    [RelayCommand]
    private void MoveTappoImageDown() => MoveSelectedImage(1);

    private void MoveSelectedImage(int delta)
    {
        if (SelectedTappoImage is not { } image)
            return;

        var from = TappoImages.IndexOf(image);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= TappoImages.Count)
            return;

        TappoImages.Move(from, to);
        SelectedTappoImage = image;
    }

    public double MinFade => AppSettings.MinFadeMs;

    public double MaxFade => AppSettings.MaxFadeMs;

    [RelayCommand]
    private void Identify() => _identify();

    [RelayCommand]
    private void BrowseTappo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Scegli il file del Tappo (immagine, video o PowerPoint)",
            // Sempre tutto insieme: il tipo del Tappo si imposta da solo dal file scelto.
            Filter = "Immagini, video e PowerPoint|*.jpg;*.jpeg;*.png;*.bmp;*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v;*.pptx;*.ppsx;*.ppt;*.pps" +
                     "|Immagini|*.jpg;*.jpeg;*.png;*.bmp" +
                     "|Video|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v" +
                     "|PowerPoint|*.pptx;*.ppsx;*.ppt;*.pps" +
                     "|Tutti i file|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        var kind = MediaKindDetector.FromPath(dialog.FileNames[0]);
        if (kind is MediaKind.Video or MediaKind.Ppt)
        {
            // Video e PowerPoint: un solo file (il primo).
            TappoPath = dialog.FileNames[0];
            TappoKind = kind == MediaKind.Video ? TappoKind.Video : TappoKind.Slides;
            return;
        }

        // Una o più immagini: sostituiscono la lista. Scegliendone più di una si passa al loop; con "Fissa" conta solo la prima.
        var chosen = dialog.FileNames.Where(f => MediaKindDetector.FromPath(f) is not (MediaKind.Video or MediaKind.Ppt)).ToList();
        if (chosen.Count >= 2)
            ImageLoop = true;
        else if (!ImageLoop && chosen.Count > 1)
            chosen = [chosen[0]];

        TappoImages.Clear();
        foreach (var file in chosen)
            TappoImages.Add(file);

        TappoPath = TappoImages.Count > 0 ? TappoImages[0] : "";
        TappoKind = TappoKind.Image;
    }

    [RelayCommand]
    private void BrowseSourceFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Scegli la cartella dei contenuti dell'evento",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(SourceFolder) && Directory.Exists(SourceFolder))
            dialog.InitialDirectory = SourceFolder;

        if (dialog.ShowDialog() == true)
            SourceFolder = dialog.FolderName;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!int.TryParse(SlideSecondsText?.Trim(), out var seconds)
            || seconds < TappoSettings.MinSlideSeconds || seconds > TappoSettings.MaxSlideSeconds)
        {
            if (ShowSeconds)
            {
                Status = $"{SecondsLabel}: scrivi un numero intero da {TappoSettings.MinSlideSeconds} a {TappoSettings.MaxSlideSeconds}.";
                return;
            }

            seconds = _current.Tappo.SlideSeconds; // campo nascosto: non serve, si tiene il valore di prima
        }

        var images = TappoKind == TappoKind.Image ? TappoImages.Take(ImageLoop ? int.MaxValue : 1).ToList() : [];
        var tappoPath = TappoKind == TappoKind.Image ? (images.Count > 0 ? images[0] : "") : TappoPath?.Trim() ?? "";
        var tappo = new TappoSettings
        {
            Kind = TappoKind,
            Path = tappoPath,
            ImagePaths = images,
            ImageLoop = TappoKind == TappoKind.Image ? ImageLoop : null,
            SlidesMode = SlidesHold ? TappoSlidesMode.Hold : TappoSlidesMode.Loop,
            SlideSeconds = seconds,
            // Stesso file di prima: si ricorda la slide su cui era fermo e le immagini già esportate.
            SlideIndex = string.Equals(tappoPath, _current.Tappo.Path, StringComparison.OrdinalIgnoreCase) ? _current.Tappo.SlideIndex : 1,
            SlidesDir = _current.Tappo.SlidesDir,
            SlideCount = _current.Tappo.SlideCount
        };

        if (TappoKind == TappoKind.Slides)
        {
            if (tappoPath.Length == 0 || ExportTappoSlides is null)
            {
                Status = "Tappo PowerPoint: scegli prima il file.";
                return;
            }

            Status = "Preparo le slide del Tappo con PowerPoint (una volta sola, può richiedere qualche decina di secondi)...";
            try
            {
                var info = await ExportTappoSlides(tappoPath, CancellationToken.None);
                tappo = tappo with { SlidesDir = info.Dir, SlideCount = info.Count };
            }
            catch (Exception ex)
            {
                // Niente viene applicato: il Tappo di prima resta com'è.
                Status = "Tappo PowerPoint non applicato: " + ex.Message;
                return;
            }
        }

        var settings = _current with
        {
            // Se il monitor salvato non è collegato ora lo conservo, così non lo perdo.
            OutputMonitor = SelectedMonitor?.Info.ToId() ?? _current.OutputMonitor,
            SimulationMode = SimulationMode,
            Tappo = tappo,
            SourceFolder = SourceFolder?.Trim() ?? "",
            AudioDeviceId = SelectedAudioDevice?.Id ?? "",
            AudioDeviceName = SelectedAudioDevice?.Name ?? "",
            FadeDurationMs = (int)Math.Round(FadeDurationMs),
            HardCut = HardCut
        };

        Status = "Applico...";
        var warning = await _apply(settings);
        Status = warning ?? "Impostazioni applicate.";
    }
}
