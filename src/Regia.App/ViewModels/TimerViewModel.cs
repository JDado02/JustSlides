using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Regia.Core.Media;
using Regia.Core.Timer;
using Regia.Core.Wave;
using Regia.Output;
using Regia.Output.Audio;
using Serilog;

namespace Regia.App.ViewModels;

/// <summary>
/// Timer del relatore in onda: parte quando il contenuto è davvero in onda (fine dissolvenza), ricomincia a ogni GO e sparisce
/// appena si lascia l'onda (dissolvenza al Tappo, PANIC, errore). Prende un'istantanea delle impostazioni della voce al
/// momento dell'avvio: modificarle nella scaletta NON tocca il timer in onda. I comandi di questo ViewModel (pannello sotto
/// il Program) valgono solo per la messa in onda corrente e non cambiano ciò che è salvato.
/// </summary>
public sealed partial class TimerViewModel : ObservableObject, IDisposable
{
    private const int DefaultSeconds = 10 * 60;

    private readonly WaveController _wave;
    private readonly OutputHost _output;
    private readonly Func<string?> _audioDeviceId;
    private readonly SpeakerTimer _timer = new();
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _epoch = DateTime.UtcNow;

    private MediaItem? _item;

    public TimerViewModel(WaveController wave, OutputHost output, Func<string?> audioDeviceId)
    {
        _wave = wave;
        _output = output;
        _audioDeviceId = audioDeviceId;

        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => Update();
        _wave.StateChanged += OnWaveStateChanged;
    }

    /// <summary>Un contenuto è in onda: i comandi del pannello hanno senso.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdjust))]
    private bool _isOnAir;

    /// <summary>C'è un timer avviato (in corso o in pausa) sull'output.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdjust))]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    private bool _isPaused;

    [ObservableProperty]
    private string _displayText = "--:--";

    [ObservableProperty]
    private TimerLevel _level;

    /// <summary>Angolo dell'output (dal vivo: vale subito sull'output).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornerTopLeft))]
    [NotifyPropertyChangedFor(nameof(CornerTopRight))]
    [NotifyPropertyChangedFor(nameof(CornerBottomLeft))]
    [NotifyPropertyChangedFor(nameof(CornerBottomRight))]
    private TimerCorner _corner = TimerCorner.BottomRight;

    /// <summary>Suono a zero per QUESTA messa in onda (si può cambiare anche con il timer già partito).</summary>
    [ObservableProperty]
    private bool _sound;

    public bool CanAdjust => IsOnAir && IsActive;

    public string ToggleText => IsActive ? "Togli timer" : "Metti timer";

    public string PauseText => IsPaused ? "Riprendi" : "Pausa";

    public bool CornerTopLeft
    {
        get => Corner == TimerCorner.TopLeft;
        set => SetCorner(value, TimerCorner.TopLeft);
    }

    public bool CornerTopRight
    {
        get => Corner == TimerCorner.TopRight;
        set => SetCorner(value, TimerCorner.TopRight);
    }

    public bool CornerBottomLeft
    {
        get => Corner == TimerCorner.BottomLeft;
        set => SetCorner(value, TimerCorner.BottomLeft);
    }

    public bool CornerBottomRight
    {
        get => Corner == TimerCorner.BottomRight;
        set => SetCorner(value, TimerCorner.BottomRight);
    }

    private DateTime Now => _epoch + _clock.Elapsed;

    private void SetCorner(bool selected, TimerCorner corner)
    {
        if (!selected || Corner == corner)
            return;

        Corner = corner;
        Update();
    }

    /// <summary>Metti timer / Togli timer. Senza timer in corso parte dalla durata salvata della voce (10:00 se non ne ha una).</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (!IsOnAir)
            return;

        if (IsActive)
        {
            Stop();
            Log.Information("Timer: tolto dall'operatore");
            return;
        }

        var seconds = _item?.Timer is { DurationSeconds: > 0 } saved ? saved.DurationSeconds : DefaultSeconds;
        Begin(seconds);
        Log.Information("Timer: messo dall'operatore ({Seconds} s)", seconds);
    }

    [RelayCommand]
    private void AddMinute() => Adjust(60);

    [RelayCommand]
    private void SubtractMinute() => Adjust(-60);

    private void Adjust(int seconds)
    {
        if (!CanAdjust)
            return;

        _timer.AddSeconds(seconds, Now);
        Log.Information("Timer: {Delta:+0;-0} s dall'operatore", seconds);
        Update();
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (!CanAdjust)
            return;

        if (_timer.IsPaused)
            _timer.Resume(Now);
        else
            _timer.Pause(Now);

        Update();
    }

    [RelayCommand]
    private void Restart()
    {
        if (!CanAdjust)
            return;

        _timer.Restart(Now);
        Log.Information("Timer: riavviato dall'operatore");
        Update();
    }

    private void OnWaveStateChanged(WaveState old, WaveState now)
    {
        try
        {
            if (now == WaveState.InOnda && old != WaveState.InOnda)
            {
                _item = _wave.CurrentItem;
                IsOnAir = true;

                // Istantanea delle impostazioni salvate: da qui in poi la scaletta non le cambia più sotto i piedi del relatore.
                var settings = _item?.Timer ?? SpeakerTimerSettings.Default;
                Corner = settings.Corner;
                Sound = settings.Sound;
                if (settings.IsRunnable)
                {
                    Begin(settings.DurationSeconds);
                    Log.Information("Timer: avviato per {Item} ({Seconds} s, angolo {Corner}, suono {Sound})",
                        _item?.DisplayName, settings.DurationSeconds, settings.Corner, settings.Sound);
                }
            }
            else if (now != WaveState.InOnda && old == WaveState.InOnda)
            {
                Stop();
                IsOnAir = false;
                _item = null;
            }
        }
        catch (Exception ex)
        {
            // Il timer è un di più: un suo guasto non deve mai disturbare la regia.
            Log.Warning(ex, "Timer: errore nel cambio di stato dell'onda");
            Stop();
        }
    }

    private void Begin(int seconds)
    {
        _timer.Start(seconds, Now);
        IsActive = true;
        IsPaused = false;
        _tick.Start();
        Update();
    }

    private void Stop()
    {
        _tick.Stop();
        _timer.Stop();
        IsActive = false;
        IsPaused = false;
        DisplayText = "--:--";
        Level = TimerLevel.Normal;
        _output.HideTimer();
    }

    private void Update()
    {
        try
        {
            if (!_timer.IsActive)
                return;

            var now = Now;
            if (_timer.Tick(now))
            {
                Log.Information("Timer: arrivato a zero ({Item}), suono {Sound}", _item?.DisplayName, Sound);
                if (Sound)
                    TimerChime.Play(_audioDeviceId());
            }

            var remaining = _timer.Remaining(now);
            DisplayText = SpeakerTimer.Format(remaining);
            Level = _timer.Level(now);
            IsPaused = _timer.IsPaused;
            _output.ShowTimer(DisplayText, Level, Corner);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Timer: errore nell'aggiornamento");
        }
    }

    public void Dispose()
    {
        _tick.Stop();
        _wave.StateChanged -= OnWaveStateChanged;
    }
}
