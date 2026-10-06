using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Regia.Core.Monitors;
using Regia.Core.Settings;

namespace Regia.App.ViewModels;

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

    [ObservableProperty]
    private MonitorItem? _selectedMonitor;

    [ObservableProperty]
    private bool _simulationMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoTappo))]
    private bool _isImageTappo;

    [ObservableProperty]
    private string _tappoPath = "";

    [ObservableProperty]
    private double _fadeDurationMs;

    [ObservableProperty]
    private bool _hardCut;

    [ObservableProperty]
    private string? _status;

    public SettingsViewModel(
        AppSettings current,
        IReadOnlyList<MonitorInfo> monitors,
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

        SimulationMode = current.SimulationMode;
        IsImageTappo = current.Tappo.Kind == TappoKind.Image;
        TappoPath = current.Tappo.Path;
        FadeDurationMs = current.FadeDurationMs;
        HardCut = current.HardCut;
    }

    public ObservableCollection<MonitorItem> Monitors { get; } = [];

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
            Title = IsImageTappo ? "Scegli l'immagine del Tappo" : "Scegli il video del Tappo",
            Filter = IsImageTappo
                ? "Immagini|*.jpg;*.jpeg;*.png;*.bmp|Tutti i file|*.*"
                : "Video|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v|Tutti i file|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            TappoPath = dialog.FileName;
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
            FadeDurationMs = (int)Math.Round(FadeDurationMs),
            HardCut = HardCut
        };

        Status = "Applico...";
        var warning = await _apply(settings);
        Status = warning ?? "Impostazioni applicate.";
    }
}
