using System.Windows.Threading;
using Microsoft.Win32;
using Regia.App.ViewModels;
using Regia.Core.Input;
using Regia.Core.Monitors;
using Regia.Core.Wave;
using Regia.Output;
using Regia.Output.Monitors;
using Serilog;

namespace Regia.App.Services;

/// <summary>
/// Sorveglia il monitor di output (hotplug): proiettore spento, cavo staccato, matrice che cambia sorgente.
/// Output sparito → Tappo immediato, finestre nascoste (altrimenti Windows le porta sulla regia e la coprono), avviso rosso,
/// GO rifiutato. Output tornato e stabile → riposizionamento automatico; per ripartire si preme GO.
/// Le decisioni sono di <see cref="OutputPresenceTracker"/> (testato); qui c'è solo il collegamento a Windows e alla regia.
/// </summary>
public sealed class OutputSupervisor : IDisposable
{
    /// <summary>Rete di sicurezza: l'evento di Windows può perdersi (sospensione, driver), si rilegge ogni tanto.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly OutputHost _output;
    private readonly WaveController _wave;
    private readonly MainViewModel _viewModel;
    private readonly OutputPresenceTracker _tracker = new();
    private readonly DispatcherTimer _timer;
    private int _checking;
    private bool _pendingReapply;
    private MonitorId? _lastMonitor;
    private bool _lastSimulation;
    private bool _disposed;

    public OutputSupervisor(OutputHost output, WaveController wave, MainViewModel viewModel, Dispatcher dispatcher)
    {
        _output = output;
        _wave = wave;
        _viewModel = viewModel;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = CheckAsync();

        // L'evento arriva su un thread di sistema.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Rimandato a fine sequenza: a Tappo il controller sta ancora chiudendo il contenuto di prima.
        _wave.StateChanged += (_, _) => dispatcher.BeginInvoke(ApplyPendingIfSafe, DispatcherPriority.Background);
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync(); // stato di partenza
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        _ = _timer.Dispatcher.InvokeAsync(async () =>
        {
            Log.Information("Cambio di configurazione dei monitor (WM_DISPLAYCHANGE)");
            await CheckAsync();

            // La conferma del ritorno vuole che il monitor resti stabile: nuova lettura appena scaduto il tempo.
            await Task.Delay(OutputPresenceTracker.StableFor + TimeSpan.FromMilliseconds(100));
            if (!_disposed)
                await CheckAsync();
        });
    }

    private async Task CheckAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        try
        {
            // L'enumerazione interroga i driver: fuori dal thread UI.
            var monitors = await Task.Run(DisplayEnumerator.GetMonitors);
            if (_disposed)
                return;

            var settings = _viewModel.Settings;
            if (settings.OutputMonitor != _lastMonitor || settings.SimulationMode != _lastSimulation)
            {
                // L'operatore ha cambiato monitor o modalità: si riparte da una situazione pulita
                // (le impostazioni applicate hanno già rimostrato le finestre).
                _lastMonitor = settings.OutputMonitor;
                _lastSimulation = settings.SimulationMode;
                _tracker.Reset();
                _pendingReapply = false;
                _viewModel.OutputLostMessage = null;
            }

            var change = _tracker.Evaluate(settings.OutputMonitor, settings.SimulationMode, monitors, DateTimeOffset.UtcNow);
            switch (change)
            {
                case PresenceChange.Lost:
                    OnLost(settings.OutputMonitor?.FriendlyName);
                    break;

                case PresenceChange.Returned:
                    Log.Information("Monitor di output tornato");
                    _pendingReapply = true;
                    ApplyPendingIfSafe();
                    break;

                case PresenceChange.Changed:
                    Log.Information("Monitor di output cambiato (risoluzione/posizione/DPI)");
                    _pendingReapply = true;
                    ApplyPendingIfSafe();
                    break;
            }
        }
        catch (Exception ex)
        {
            // Mai far cadere la regia per la sorveglianza dei monitor.
            Log.Error(ex, "Errore nel controllo del monitor di output");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private void OnLost(string? name)
    {
        Log.Warning("Monitor di output {Name} non più disponibile (stato onda: {State})", name, _viewModel.State);

        // Tappo immediato se qualcosa è in onda; poi le finestre si nascondono perché Windows le ha già spostate sulla regia.
        if (_viewModel.State is not (WaveState.Tappo or WaveState.Errore))
            _viewModel.PerformKeyAction(KeyAction.Panic);

        _output.Suspend();
        _pendingReapply = false;

        _viewModel.OutputLostMessage =
            $"OUTPUT SCOLLEGATO{(string.IsNullOrWhiteSpace(name) ? "" : $" ({name})")}: in attesa che il monitor torni. " +
            "Quando torna il Tappo riappare da solo; poi premi GO per riprendere.";
    }

    /// <summary>
    /// Riposiziona le finestre solo a Tappo/Errore: farlo con un video o uno slideshow in onda rimetterebbe a posto il
    /// contenuto sopra PowerPoint. Se qualcosa è in onda si aspetta il ritorno al Tappo.
    /// </summary>
    private void ApplyPendingIfSafe()
    {
        if (_disposed || !_pendingReapply || _viewModel.State is not (WaveState.Tappo or WaveState.Errore))
            return;

        _pendingReapply = false;
        _ = ReapplyAsync();
    }

    private async Task ReapplyAsync()
    {
        try
        {
            await _viewModel.ReapplyOutputAsync();
            _viewModel.OutputLostMessage = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Impossibile riposizionare l'output dopo il ritorno del monitor");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
