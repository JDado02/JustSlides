using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Regia.Core.Media;
using Regia.Core.Settings;
using Regia.Core.Wave;
using Regia.Output;
using Regia.Output.Audio;
using Regia.Output.Content;
using Regia.Output.Monitors;
using Regia.Output.Transitions;
using Serilog;

namespace Regia.App.ViewModels;

/// <summary>
/// ViewModel della regia. Lo stato dell'onda vive in <see cref="WaveController"/> (Core): qui si
/// espongono solo comandi e proprietà per la UI. La lista dei file è provvisoria (la scaletta vera
/// arriva in M6): non viene salvata.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly OutputHost _output;
    private readonly SettingsStore _store;
    private readonly WaveController _wave;
    private readonly TappoTransitions _transitions;
    private readonly ContentPresenterFactory _factory;
    private bool _isScrubbing;
    private bool _syncingPosition;
    private bool _syncingVolume;
    private long _lastSeekTick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private WaveState _state = WaveState.Tappo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedVideo))]
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

    public MainViewModel(
        OutputHost output,
        SettingsStore store,
        AppSettings settings,
        WaveController wave,
        TappoTransitions transitions,
        ContentPresenterFactory factory)
    {
        _output = output;
        _store = store;
        _wave = wave;
        _transitions = transitions;
        _factory = factory;
        Settings = settings;

        Items.Add(MediaItem.TestPattern);
        SelectedItem = Items[0];

        _wave.StateChanged += (_, now) => State = now;
        _wave.PageChanged += OnPageChanged;
        _wave.PlaybackChanged += OnPlaybackChanged;
        _wave.ErrorOccurred += message => Warning = message;
        _factory.Warning += message => Warning = message;
    }

    public AppSettings Settings { get; private set; }

    public ObservableCollection<MediaItem> Items { get; } = [];

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    /// <summary>"Pagina N/M" per i PDF; vuoto per i contenuti senza pagine.</summary>
    public string PageText => _wave.Page is { } page ? $"Pagina {page.Current} / {page.Total}" : "";

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

    public bool IsSelectedVideo => SelectedItem?.Kind == MediaKind.Video;

    /// <summary>Volume del video selezionato, ricordato per ogni file; impostabile anche prima del GO.</summary>
    public int SelectedVolume
    {
        get => SelectedItem?.Volume ?? 100;
        set
        {
            if (SelectedItem is not { Kind: MediaKind.Video } item)
                return;

            value = Math.Clamp(value, 0, 100);
            if (item.Volume == value)
                return;

            if (ReferenceEquals(item, _wave.CurrentItem))
                Volume = value; // è in onda: si applica dal vivo (e il controller lo ricorda nel file)
            else
                item.Volume = value;

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
    }

    /// <summary>Aggiunge file alla lista provvisoria; quelli non gestiti vengono scartati con un avviso.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var rejected = new List<string>();

        foreach (var path in paths)
        {
            var item = MediaItem.FromPath(path);
            if (item.Kind is MediaKind.Image or MediaKind.Pdf or MediaKind.Video)
            {
                Items.Add(item);
                SelectedItem = item;
                Log.Information("File aggiunto alla lista: {Path} ({Kind})", path, item.Kind);
            }
            else
            {
                rejected.Add(item.DisplayName);
                Log.Warning("File non supportato in questa versione: {Path} ({Kind})", path, item.Kind);
            }
        }

        if (rejected.Count > 0)
            Warning = "File non supportati (per ora solo JPG, PNG, PDF e video): " + string.Join(", ", rejected);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        // Il file in onda non si toglie dalla lista: la regia lo sta ancora usando.
        if (SelectedItem is null || SelectedItem.Kind == MediaKind.TestPattern || ReferenceEquals(SelectedItem, _wave.CurrentItem))
            return;

        var index = Items.IndexOf(SelectedItem);
        Items.Remove(SelectedItem);
        SelectedItem = Items.Count > 0 ? Items[Math.Min(index, Items.Count - 1)] : null;
    }

    [RelayCommand]
    private async Task GoAsync()
    {
        if (SelectedItem is null)
        {
            Log.Warning("Comando GO ignorato: nessun file selezionato");
            return;
        }

        Warning = _output.Warning;
        await _wave.GoAsync(SelectedItem);
    }

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

        _wave.SetVolume(value);
        OnPropertyChanged(nameof(SelectedVolume));
    }

    /// <summary>Spostamento del cursore di posizione da parte dell'utente (con limite di frequenza per non intasare VLC).</summary>
    partial void OnPositionSecondsChanged(double value)
    {
        if (_syncingPosition || !_isScrubbing)
            return;

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

        var index = Items.IndexOf(item);
        if (index < 0)
            return;

        var updated = item with { VideoEnd = action };
        Items[index] = updated;
        SelectedItem = updated;
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

            // Al video che va in onda si mostra il suo volume.
            if (_wave.CurrentItem is { Kind: MediaKind.Video } current && Volume != current.Volume)
            {
                _syncingVolume = true;
                Volume = current.Volume;
                _syncingVolume = false;
                OnPropertyChanged(nameof(SelectedVolume));
            }
        }

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
            () => _output.IdentifyMonitors(DisplayEnumerator.GetMonitors()));
    }

    /// <summary>Salva e applica le nuove impostazioni. Restituisce l'eventuale avviso per l'operatore.</summary>
    private async Task<string?> ApplySettingsAsync(AppSettings settings)
    {
        Settings = settings.Normalize();
        _transitions.Settings = Settings;
        _factory.Settings = Settings;

        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Impossibile salvare le impostazioni");
            Warning = "Impossibile salvare le impostazioni: " + ex.Message;
        }

        await ApplyOutputAsync();
        return _output.Warning;
    }

    private async Task ApplyOutputAsync()
    {
        await _output.ApplyAsync(Settings, DisplayEnumerator.GetMonitors());
        Warning = _output.Warning;
    }
}
