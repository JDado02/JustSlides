using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Regia.Core.Settings;
using Regia.Core.Wave;
using Regia.Output;
using Regia.Output.Monitors;
using Serilog;

namespace Regia.App.ViewModels;

/// <summary>
/// Milestone 1: stato onda minimale (la macchina a stati completa arriva in M2) e comandi
/// GO di prova / Torna al Tappo / PANIC. I comandi non validi nello stato corrente vengono
/// ignorati e loggati.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly OutputHost _output;
    private readonly SettingsStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private WaveState _state = WaveState.Tappo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warning;

    public MainViewModel(OutputHost output, SettingsStore store, AppSettings settings)
    {
        _output = output;
        _store = store;
        Settings = settings;
    }

    public AppSettings Settings { get; private set; }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

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

    [RelayCommand]
    private async Task GoAsync()
    {
        if (State is not (WaveState.Tappo or WaveState.Errore))
        {
            Log.Warning("Comando GO ignorato: stato {State}", State);
            return;
        }

        try
        {
            Log.Information("GO: contenuto di prova in onda");
            State = WaveState.InTransizioneIn;

            var completed = await _output.Fader.FadeOutAsync(Settings.FadeDurationMs, Settings.HardCut);
            if (!completed)
            {
                Log.Information("GO: dissolvenza interrotta");
                return;
            }

            State = WaveState.InOnda;
            Log.Information("In onda: contenuto di prova");
        }
        catch (Exception ex)
        {
            HandleError("GO", ex);
        }
    }

    [RelayCommand]
    private async Task BackToTappoAsync()
    {
        if (State != WaveState.InOnda)
        {
            Log.Warning("Comando Torna al Tappo ignorato: stato {State}", State);
            return;
        }

        try
        {
            Log.Information("Ritorno al Tappo");
            State = WaveState.InTransizioneOut;

            var completed = await _output.Fader.FadeInAsync(Settings.FadeDurationMs, Settings.HardCut);
            if (!completed)
            {
                Log.Information("Ritorno al Tappo: dissolvenza interrotta");
                return;
            }

            State = WaveState.Tappo;
            Log.Information("Sul Tappo");
        }
        catch (Exception ex)
        {
            HandleError("Torna al Tappo", ex);
        }
    }

    /// <summary>PANIC: Tappo immediato da qualsiasi stato.</summary>
    [RelayCommand]
    private void Panic()
    {
        Log.Warning("PANIC (stato precedente: {State})", State);
        try
        {
            _output.Fader.Panic();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore durante il PANIC");
        }

        State = WaveState.Tappo;
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
        Log.Error(ex, "Errore in {Context}: ritorno al Tappo", context);

        try
        {
            _output.Fader.Panic();
        }
        catch (Exception panicEx)
        {
            Log.Error(panicEx, "Errore anche nel ritorno al Tappo");
        }

        State = WaveState.Errore;
        Warning = $"Errore ({context}): {ex.Message}";
    }

    public SettingsViewModel CreateSettingsViewModel()
    {
        return new SettingsViewModel(
            Settings,
            DisplayEnumerator.GetMonitors(),
            ApplySettingsAsync,
            () => _output.IdentifyMonitors(DisplayEnumerator.GetMonitors()));
    }

    /// <summary>Salva e applica le nuove impostazioni. Restituisce l'eventuale avviso per l'operatore.</summary>
    private async Task<string?> ApplySettingsAsync(AppSettings settings)
    {
        Settings = settings.Normalize();

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
