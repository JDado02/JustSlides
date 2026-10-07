using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Regia.App.Services;
using Regia.Core.Input;
using Regia.Core.Show;
using Regia.Core.Media;
using Regia.Core.Settings;
using Regia.Core.Wave;
using Regia.Output;
using Regia.Output.Audio;
using Regia.Output.Content;
using Regia.Output.Monitors;
using Regia.Output.Ppt;
using Regia.Output.Transitions;
using Serilog;

namespace Regia.App.ViewModels;

/// <summary>
/// ViewModel della regia. Lo stato dell'onda vive in <see cref="WaveController"/> (Core), la scaletta e il file
/// show in <see cref="ShowController"/>: qui si espongono solo comandi e proprietà per la UI.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly OutputHost _output;
    private readonly KeyBindings _keys;
    private readonly ShowController _show;
    private readonly WaveController _wave;
    private readonly TappoTransitions _transitions;
    private readonly ContentPresenterFactory _factory;
    private readonly PptHostClient _ppt;
    private bool _isScrubbing;
    private bool _syncingPosition;
    private bool _syncingVolume;
    private MediaItem? _liveItem;
    private long _lastSeekTick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private WaveState _state = WaveState.Tappo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedVideo))]
    [NotifyPropertyChangedFor(nameof(HasEditableSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedSession))]
    [NotifyPropertyChangedFor(nameof(IsSelectedAudio))]
    [NotifyPropertyChangedFor(nameof(EndReturnToTappo))]
    [NotifyPropertyChangedFor(nameof(EndHoldLastFrame))]
    [NotifyPropertyChangedFor(nameof(EndLoop))]
    [NotifyPropertyChangedFor(nameof(SelectedVolume))]
    private MediaItem? _selectedItem;

    /// <summary>Volume del video in onda (cursore del pannello video).</summary>
    [ObservableProperty]
    private int _volume = 100;

    /// <summary>Posizione del video in onda in secondi (cursore di scorrimento).</summary>
    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private bool _isMuted;

    /// <summary>Avviso fisso: PowerPoint suona sul predefinito di Windows, che non è il dispositivo scelto. Separato da <see cref="Warning"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioDeviceWarning))]
    private string? _audioDeviceWarning;

    private readonly DefaultDeviceMonitor _deviceMonitor = new();

    public MainViewModel(
        OutputHost output,
        ShowController show,
        PreviewViewModel preview,
        AppSettings settings,
        WaveController wave,
        TappoTransitions transitions,
        ContentPresenterFactory factory,
        PptHostClient ppt,
        ProgramViewModel program,
        KeyBindings keys)
    {
        _keys = keys;
        _output = output;
        _show = show;
        Preview = preview;
        Program = program;
        _wave = wave;
        _transitions = transitions;
        _factory = factory;
        _ppt = ppt;
        Settings = settings;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => ShowExcluded || o is not MediaItem { Excluded: true };
        if (ItemsView is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
        {
            live.LiveFilteringProperties.Add(nameof(MediaItem.Excluded));
            live.IsLiveFiltering = true;
        }

        Items.CollectionChanged += OnItemsChanged;
        _show.Sync.StatusChanged += () => OnUi(RefreshSourceStatus);
        _show.SaveFailed += message => OnUi(() => Warning = message);
        _show.SettingsReplaced += OnSettingsReplaced;

        _wave.StateChanged += (_, now) =>
        {
            State = now;
            _show.Sync.OnWaveChanged(); // esegue gli aggiornamenti/rimozioni rimandati dal file che era in onda
        };
        _wave.PageChanged += OnPageChanged;
        _wave.PlaybackChanged += OnPlaybackChanged;
        _wave.ErrorOccurred += message => Warning = message;
        _factory.Warning += message => Warning = message;

        // PptHost lavora su thread suoi: avvisi e stato si riportano sul thread UI.
        _ppt.Warning += message => OnUi(() => Warning = message);
        _ppt.StatusChanged += () => OnUi(() => OnPropertyChanged(nameof(PptStatusText)));

        // Il predefinito di Windows può cambiare in qualsiasi momento (anche a show in corso).
        _deviceMonitor.Changed += () => OnUi(RefreshAudioDeviceWarning);
    }

    public bool HasAudioDeviceWarning => !string.IsNullOrEmpty(AudioDeviceWarning);

    /// <summary>C'è un video o un PowerPoint in onda: si mostrano volume dal vivo e Mute.</summary>
    public bool IsAudioOnAir => _wave.HasAudio;

    /// <summary>Audio senza countdown (PowerPoint): pannello ridotto, solo volume e Mute.</summary>
    public bool IsSlideShowAudioOnAir => _wave.HasAudio && _wave.Progress is null;

    private void RefreshAudioDeviceWarning() =>
        AudioDeviceWarning = Regia.Core.Audio.AudioDeviceWarning.Evaluate(
            Settings.AudioDeviceId, Settings.AudioDeviceName, DefaultDeviceMonitor.GetDefaultId(), HasPptItems);

    /// <summary>Apre le impostazioni audio di Windows per cambiare il dispositivo predefinito.</summary>
    [RelayCommand]
    private void OpenWindowsSoundSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile aprire le impostazioni audio di Windows");
        }
    }

    /// <summary>Stato di PowerPoint/PptHost per l'operatore.</summary>
    public string PptStatusText => _ppt.StatusText;

    /// <summary>C'è almeno un file PowerPoint in lista: lo stato di PowerPoint è rilevante.</summary>
    public bool HasPptItems => Items.Any(i => i.Kind == MediaKind.Ppt);

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public AppSettings Settings { get; private set; }

    public ObservableCollection<MediaItem> Items => _show.Scaletta.Items;

    /// <summary>Vista della scaletta: nasconde le voci escluse (a meno di "Mostra esclusi").</summary>
    public ICollectionView ItemsView { get; }

    public PreviewViewModel Preview { get; }

    /// <summary>Cosa è in onda, prossima slide, etichette dei tasti.</summary>
    public ProgramViewModel Program { get; }

    [ObservableProperty]
    private bool _showExcluded;

    partial void OnShowExcludedChanged(bool value) => ItemsView.Refresh();

    /// <summary>Messaggio sulla cartella contenuti (non configurata / non raggiungibile); vuoto se tutto bene.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceMessage))]
    private string _sourceMessage = "";

    public bool HasSourceMessage => SourceMessage.Length > 0;

    /// <summary>Cartella irraggiungibile (rosso) invece che non configurata (arancione).</summary>
    public bool SourceMessageIsError => _show.Sync.Status == SourceStatus.Unreachable;

    public bool SourceNotConfigured => _show.Sync.Status == SourceStatus.NotConfigured;

    /// <summary>Cartella collegata, per la barra in alto.</summary>
    public string SourceLabel => string.IsNullOrWhiteSpace(Settings.SourceFolder)
        ? "Cartella contenuti: non configurata"
        : "Cartella contenuti: " + Settings.SourceFolder;

    private void RefreshSourceStatus()
    {
        SourceMessage = _show.Sync.StatusMessage;
        OnPropertyChanged(nameof(SourceMessageIsError));
        OnPropertyChanged(nameof(SourceNotConfigured));
        OnPropertyChanged(nameof(SourceLabel));
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPptItems));
        RefreshAudioDeviceWarning();

        // La voce selezionata è sparita (file tolto dalla cartella): si passa a una vicina.
        if (SelectedItem is not null && !Items.Contains(SelectedItem))
        {
            var index = e.OldStartingIndex < 0 ? 1 : Math.Max(1, e.OldStartingIndex);
            SelectedItem = Items.Count > 1 ? Items[Math.Min(index, Items.Count - 1)] : Items.FirstOrDefault();
        }

        // PowerPoint ci mette qualche secondo ad avviarsi: lo si prepara appena compare un PPT, così il GO è rapido.
        if (e.NewItems is not null && e.NewItems.OfType<MediaItem>().Any(i => i.Kind == MediaKind.Ppt))
            PrewarmPowerPoint();
    }

    private void PrewarmPowerPoint()
    {
        if (_ppt.ForeignPowerPointRunning)
            Warning = PptHostClient.ForeignInstanceMessage;
        else
            _ppt.Prewarm();
    }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    /// <summary>Avviso rosso fisso: il monitor di output non c'è (cavo, proiettore, matrice). Null = tutto a posto.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputLost))]
    private string? _outputLostMessage;

    /// <summary>L'output è perso: finestre nascoste, GO rifiutato finché non torna.</summary>
    public bool HasOutputLost => !string.IsNullOrEmpty(OutputLostMessage);

    /// <summary>Riposiziona le finestre di output e ricarica il Tappo (monitor tornato o cambiato).</summary>
    public Task ReapplyOutputAsync() => ApplyOutputAsync();

    /// <summary>"Pagina N/M" per i PDF; vuoto per i contenuti senza pagine.</summary>
    public string PageText => _wave.Page is { } page
        ? $"{(_wave.CurrentItem?.Kind == MediaKind.Ppt ? "Slide" : "Pagina")} {page.Current} / {page.Total}"
        : "";

    public bool HasPage => _wave.Page is not null;

    /// <summary>C'è un video in onda (o in entrata): si mostrano countdown e comandi di riproduzione.</summary>
    public bool IsVideoOnAir => _wave.Progress is not null;

    /// <summary>Salto dei pulsanti "indietro / avanti" del video.</summary>
    public const double SeekStepSeconds = 10;

    /// <summary>Countdown "tempo rimanente" del video in onda.</summary>
    public string RemainingText => _wave.Progress is { } p ? "-" + FormatTime(p.Remaining) : "";

    /// <summary>"Trascorso / durata" del video in onda.</summary>
    public string ElapsedText => _wave.Progress is { } p ? $"{FormatTime(p.Elapsed)} / {FormatTime(p.Duration)}" : "";

    public string PauseButtonText => _wave.IsPaused ? "PLAY" : "PAUSA";

    partial void OnSelectedItemChanged(MediaItem? value)
    {
        // "AGGIORNATO" resta finché l'operatore non guarda la voce.
        if (value is { IsUpdated: true })
            value.IsUpdated = false;

        _show.NoteSelection(value);
        Preview.Show(value);
    }

    /// <summary>Una voce vera (non la schermata di prova): sessione e relatore sono modificabili.</summary>
    public bool HasEditableSelection => SelectedItem is { IsFixed: false };

    /// <summary>Sessione della voce selezionata; scriverla a mano la rende definitiva (la scansione non la riscrive).</summary>
    public string SelectedSession
    {
        get => SelectedItem?.Session ?? "";
        set
        {
            if (SelectedItem is not { IsFixed: false } item || item.Session == value)
                return;

            item.Session = value;
            item.SessionEdited = true;
            OnPropertyChanged();
        }
    }

    public bool IsSelectedVideo => SelectedItem?.Kind == MediaKind.Video;

    /// <summary>Video o PowerPoint selezionato: ha un volume salvato.</summary>
    public bool IsSelectedAudio => SelectedItem?.Kind is MediaKind.Video or MediaKind.Ppt;

    /// <summary>Volume del video selezionato, ricordato per ogni file; impostabile anche prima del GO.</summary>
    public int SelectedVolume
    {
        get => SelectedItem?.Volume ?? 100;
        set
        {
            if (SelectedItem is not { Kind: MediaKind.Video or MediaKind.Ppt } item)
                return;

            value = Math.Clamp(value, 0, 100);
            if (item.Volume == value)
                return;

            item.Volume = value;

            // Se è proprio il video in onda, lo si sente subito (anche sul cursore dal vivo).
            if (ReferenceEquals(item, _wave.CurrentItem))
                Volume = value;

            OnPropertyChanged();
        }
    }

    public bool EndReturnToTappo
    {
        get => SelectedItem?.VideoEnd == VideoEndAction.ReturnToTappo;
        set => SetVideoEnd(value, VideoEndAction.ReturnToTappo);
    }

    public bool EndHoldLastFrame
    {
        get => SelectedItem?.VideoEnd == VideoEndAction.HoldLastFrame;
        set => SetVideoEnd(value, VideoEndAction.HoldLastFrame);
    }

    public bool EndLoop
    {
        get => SelectedItem?.VideoEnd == VideoEndAction.Loop;
        set => SetVideoEnd(value, VideoEndAction.Loop);
    }

    public string StateText => State switch
    {
        WaveState.Tappo => "TAPPO",
        WaveState.Caricamento => "CARICAMENTO",
        WaveState.InTransizioneIn => "IN ONDA...",
        WaveState.InOnda => "IN ONDA",
        WaveState.InTransizioneOut => "AL TAPPO...",
        WaveState.Errore => "ERRORE - TAPPO",
        _ => State.ToString()
    };

    /// <summary>Applica all'avvio le impostazioni lette da disco.</summary>
    public async Task InitializeAsync()
    {
        await ApplyOutputAsync();
        RefreshAudioDeviceWarning();
        RefreshSourceStatus();

        SelectedItem = _show.RestoredSelection ?? Items.FirstOrDefault();
    }

    /// <summary>
    /// "Aggiungi file": copia i file nella cartella collegata (una sola fonte di verità); li trova la scansione.
    /// </summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var errors = await _show.Sync.CopyIntoFolderAsync(paths);
        if (errors.Count > 0)
            Warning = "Impossibile aggiungere: " + string.Join("; ", errors);
    }

    /// <summary>Riordino per trascinamento: sposta <paramref name="item"/> prima o dopo <paramref name="target"/>.</summary>
    public void MoveItem(MediaItem item, MediaItem target, bool after)
    {
        if (_show.Scaletta.Move(item, target, after))
            Log.Information("Scaletta: {Item} spostato {Where} {Target}", item.DisplayName, after ? "dopo" : "prima di", target.DisplayName);
    }

    /// <summary>Nasconde/mostra la voce selezionata (la cartella non si tocca). Il file in onda non si nasconde.</summary>
    [RelayCommand]
    private void ToggleExcludeSelected()
    {
        if (SelectedItem is not { IsFixed: false } item)
            return;

        if (!item.Excluded && ReferenceEquals(item, _wave.CurrentItem))
        {
            Warning = $"\"{item.DisplayName}\" è in onda: riporta prima la regia al Tappo.";
            return;
        }

        item.Excluded = !item.Excluded;
        Log.Information("Voce {Item}: {State}", item.DisplayName, item.Excluded ? "esclusa dalla scaletta" : "di nuovo in scaletta");
    }

    /// <summary>"Nuovo evento": archivia lo show e riparte da zero (scaletta, impostazioni, cartella). Solo a Tappo.</summary>
    [RelayCommand]
    private void NewEvent()
    {
        if (State is not (WaveState.Tappo or WaveState.Errore))
        {
            Warning = "Nuovo evento: riporta prima la regia al Tappo.";
            return;
        }

        var answer = MessageBox.Show(Application.Current?.MainWindow!,
            "Iniziare un nuovo evento?\n\nLa scaletta, le impostazioni dell'evento (monitor, Tappo, audio, dissolvenza) e il " +
            "collegamento alla cartella contenuti vengono azzerati. Le copie locali dei file vengono cancellate; " +
            "i file nella cartella contenuti non vengono toccati.",
            "Nuovo evento", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
            return;

        Log.Information("Nuovo evento richiesto dall'operatore");
        SelectedItem = null;
        _show.NewEvent();
        SelectedItem = Items.FirstOrDefault();
        RefreshSourceStatus();
    }

    private async void OnSettingsReplaced(AppSettings settings)
    {
        try
        {
            await ApplyAppliedSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Impossibile applicare le impostazioni del nuovo evento");
            Warning = "Impossibile applicare le impostazioni: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task GoAsync()
    {
        if (HasOutputLost)
        {
            Warning = "GO rifiutato: " + OutputLostMessage;
            Log.Warning("GO rifiutato: monitor di output non disponibile");
            return;
        }

        if (SelectedItem is null)
        {
            Log.Warning("Comando GO ignorato: nessun file selezionato");
            return;
        }

        if (!SelectedItem.CanGoOnAir)
        {
            Warning = GoRefusedMessage(SelectedItem);
            Log.Warning("GO rifiutato: {Message}", Warning);
            return;
        }

        Warning = _output.Warning;
        await _wave.GoAsync(SelectedItem);
    }

    private static string GoRefusedMessage(MediaItem item) => item.CopyState switch
    {
        CopyState.Copying => $"\"{item.DisplayName}\" non è ancora pronto: copia in corso ({item.CopyProgress}%).",
        CopyState.Failed => $"\"{item.DisplayName}\": copia non riuscita ({item.StatusDetail}).",
        CopyState.Unsupported => $"\"{item.DisplayName}\": tipo di file non supportato.",
        _ => $"\"{item.DisplayName}\" ha un errore nel controllo: {item.PreflightSummary}."
    };

    [RelayCommand]
    private async Task BackToTappoAsync()
    {
        await _wave.StopAsync();
    }

    [RelayCommand]
    private void TogglePause() => _wave.TogglePause();

    partial void OnVolumeChanged(int value)
    {
        if (_syncingVolume)
            return;

        _wave.SetVolume(value); // solo dal vivo: il volume salvato nel file non cambia
    }

    /// <summary>
    /// Spostamento del cursore di posizione da parte dell'utente. Gli aggiornamenti automatici passano da
    /// <c>_syncingPosition</c>, quindi tutto il resto viene da una persona (mouse, tocco, accessibilità).
    /// Durante il trascinamento si limita la frequenza per non intasare VLC; la posizione finale la manda <see cref="EndScrub"/>.
    /// </summary>
    partial void OnPositionSecondsChanged(double value)
    {
        if (_syncingPosition)
            return;

        if (!_isScrubbing)
        {
            _wave.SeekTo(TimeSpan.FromSeconds(value));
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastSeekTick < 80)
            return;

        _lastSeekTick = now;
        _wave.SeekTo(TimeSpan.FromSeconds(value), log: false);
    }

    /// <summary>L'utente ha afferrato il cursore: da qui in poi la posizione non si aggiorna da sola.</summary>
    public void BeginScrub() => _isScrubbing = true;

    /// <summary>Cursore rilasciato: posizione definitiva.</summary>
    public void EndScrub()
    {
        if (!_isScrubbing)
            return;

        _isScrubbing = false;
        _wave.SeekTo(TimeSpan.FromSeconds(PositionSeconds));
    }

    [RelayCommand]
    private void SeekBack() => _wave.SeekBy(TimeSpan.FromSeconds(-SeekStepSeconds));

    [RelayCommand]
    private void SeekForward() => _wave.SeekBy(TimeSpan.FromSeconds(SeekStepSeconds));

    partial void OnIsMutedChanged(bool value) => _wave.SetMuted(value);

    /// <summary>Cambia l'azione di fine video del file selezionato (vale dal prossimo GO; non per il file in onda).</summary>
    private void SetVideoEnd(bool selected, VideoEndAction action)
    {
        if (!selected || SelectedItem is not { Kind: MediaKind.Video } item || item.VideoEnd == action)
            return;

        if (ReferenceEquals(item, _wave.CurrentItem))
        {
            Warning = "Il video è in onda: l'azione di fine si cambia dopo averlo fermato.";
            OnPropertyChanged(nameof(EndReturnToTappo));
            OnPropertyChanged(nameof(EndHoldLastFrame));
            OnPropertyChanged(nameof(EndLoop));
            return;
        }

        item.VideoEnd = action;
        OnPropertyChanged(nameof(EndReturnToTappo));
        OnPropertyChanged(nameof(EndHoldLastFrame));
        OnPropertyChanged(nameof(EndLoop));
        Log.Information("Fine video di {Item}: {Action}", item.DisplayName, action);
    }

    private void OnPlaybackChanged()
    {
        // Rete di sicurezza: se il rilascio del mouse sul cursore è andato perso, si riprende ad aggiornare la posizione.
        if (_isScrubbing && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Released)
            EndScrub();

        if (_wave.Progress is { } progress)
        {
            // Durata prima della posizione: il cursore ha il massimo = durata.
            if (!_isScrubbing)
            {
                _syncingPosition = true;
                DurationSeconds = progress.Duration.TotalSeconds;
                PositionSeconds = progress.Elapsed.TotalSeconds;
                _syncingPosition = false;
            }

        }

        // Quando un video o un PowerPoint va in onda il cursore dal vivo riparte dal suo volume salvato.
        if (_wave.HasAudio)
        {
            if (!ReferenceEquals(_liveItem, _wave.CurrentItem))
            {
                _liveItem = _wave.CurrentItem;
                _syncingVolume = true;
                Volume = _wave.LiveVolume;
                _syncingVolume = false;
            }
        }
        else
        {
            _liveItem = null;
        }

        OnPropertyChanged(nameof(IsAudioOnAir));
        OnPropertyChanged(nameof(IsSlideShowAudioOnAir));
        OnPropertyChanged(nameof(IsVideoOnAir));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(PauseButtonText));
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    [RelayCommand]
    private void NextPage() => _wave.Next();

    [RelayCommand]
    private void PreviousPage() => _wave.Previous();

    /// <summary>PANIC: Tappo immediato da qualsiasi stato.</summary>
    [RelayCommand]
    private void Panic()
    {
        _wave.Panic();
    }

    private string? _keyboardWarning;

    /// <summary>Avviso sull'hook di tastiera (non installato); null = tolto. Toglie solo il proprio testo, non quello di altri avvisi.</summary>
    public void ShowKeyboardWarning(string? message)
    {
        if (message is not null)
        {
            _keyboardWarning = message;
            Warning = message;
        }
        else
        {
            if (Warning == _keyboardWarning)
                Warning = null;
            _keyboardWarning = null;
        }
    }

    /// <summary>
    /// Esegue l'azione associata a un tasto (finestra della regia e hook globale passano da qui).
    /// False = l'azione non si applica adesso e il tasto va lasciato a chi lo vuole (es. le frecce scorrono la lista a Tappo).
    /// </summary>
    public bool PerformKeyAction(KeyAction action)
    {
        switch (action)
        {
            case KeyAction.Go:
                GoCommand.Execute(null);
                return true;

            case KeyAction.Panic:
                PanicCommand.Execute(null);
                return true;

            case KeyAction.Next or KeyAction.Previous when State is WaveState.Tappo or WaveState.Errore:
                return false;

            case KeyAction.Next:
                NextPageCommand.Execute(null);
                return true;

            case KeyAction.Previous:
                PreviousPageCommand.Execute(null);
                return true;

            case KeyAction.SelectUp:
                MoveSelection(-1);
                return true;

            case KeyAction.SelectDown:
                MoveSelection(1);
                return true;

            case KeyAction.PlayPause:
                if (IsVideoOnAir)
                    TogglePauseCommand.Execute(null);
                return true;

            case KeyAction.Mute:
                IsMuted = !IsMuted;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Sposta la selezione sulla lista visibile (salta le voci escluse nascoste); si ferma ai bordi.</summary>
    private void MoveSelection(int delta)
    {
        var visible = ItemsView.Cast<MediaItem>().ToList();
        var index = SelectedItem is null ? -1 : visible.IndexOf(SelectedItem);
        var next = SelectionStep.Move(visible.Count, index, delta);
        if (next >= 0 && next != index)
            SelectedItem = visible[next];
    }

    private void OnPageChanged()
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPage));
    }

    /// <summary>Solleva un'eccezione non gestita sul thread UI: serve a provare gli handler globali.</summary>
    [RelayCommand]
    private void SimulateError()
    {
        Log.Warning("Simulazione di errore non gestito richiesta dall'operatore");
        Application.Current.Dispatcher.BeginInvoke(new Action(
            () => throw new InvalidOperationException("Errore simulato dall'operatore (Ctrl+Shift+F12)")));
    }

    /// <summary>Errore durante la proiezione: Tappo immediato, log, la regia resta operativa.</summary>
    public void HandleError(string context, Exception ex)
    {
        _wave.Fail(context, ex);
    }

    public SettingsViewModel CreateSettingsViewModel()
    {
        return new SettingsViewModel(
            Settings,
            DisplayEnumerator.GetMonitors(),
            AudioDeviceEnumerator.GetOutputDevices(),
            ApplySettingsAsync,
            () => _output.IdentifyMonitors(DisplayEnumerator.GetMonitors()))
        {
            Keys = new KeyBindingsViewModel(_keys)
        };
    }

    /// <summary>Salva e applica le nuove impostazioni. Restituisce l'eventuale avviso per l'operatore.</summary>
    private async Task<string?> ApplySettingsAsync(AppSettings settings)
    {
        await ApplyAppliedSettingsAsync(settings, persist: true);
        return _output.Warning;
    }

    private async Task ApplyAppliedSettingsAsync(AppSettings settings, bool persist = false)
    {
        Settings = settings.Normalize();
        _transitions.Settings = Settings;
        _factory.Settings = Settings;
        _ppt.Settings = Settings;
        if (persist)
            _show.UpdateSettings(Settings);

        RefreshAudioDeviceWarning();
        RefreshSourceStatus();
        await ApplyOutputAsync();
    }

    private async Task ApplyOutputAsync()
    {
        await _output.ApplyAsync(Settings, DisplayEnumerator.GetMonitors());
        Warning = _output.Warning;
    }
}
