using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Regia.Core.Media;
using Regia.Core.Monitors;
using Regia.Core.Settings;
using Regia.Output.Audio;

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
    [NotifyPropertyChangedFor(nameof(IsVideoTappo))]
    private bool _isImageTappo;

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
        IsImageTappo = current.Tappo.Kind == TappoKind.Image;
        TappoPath = current.Tappo.Path;
        SourceFolder = current.SourceFolder;
        FadeDurationMs = current.FadeDurationMs;
        HardCut = current.HardCut;
    }

    public ObservableCollection<MonitorItem> Monitors { get; } = [];

    public ObservableCollection<AudioDeviceItem> AudioDevices { get; } = [];

    public bool IsVideoTappo
    {
        get => !IsImageTappo;
        set => IsImageTappo = !value;
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
            Title = "Scegli il file del Tappo (immagine o video)",
            // Sempre immagini e video insieme: il tipo del Tappo si imposta da solo dal file scelto.
            Filter = "Immagini e video|*.jpg;*.jpeg;*.png;*.bmp;*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v" +
                     "|Immagini|*.jpg;*.jpeg;*.png;*.bmp" +
                     "|Video|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v" +
                     "|Tutti i file|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        TappoPath = dialog.FileName;

        // Un video diventa "Video in loop", ogni altro file (immagine) "Immagine".
        IsImageTappo = MediaKindDetector.FromPath(dialog.FileName) != MediaKind.Video;
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
        var settings = _current with
        {
            // Se il monitor salvato non è collegato ora lo conservo, così non lo perdo.
            OutputMonitor = SelectedMonitor?.Info.ToId() ?? _current.OutputMonitor,
            SimulationMode = SimulationMode,
            Tappo = new TappoSettings
            {
                Kind = IsImageTappo ? TappoKind.Image : TappoKind.Video,
                Path = TappoPath?.Trim() ?? ""
            },
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
