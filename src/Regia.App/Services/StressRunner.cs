using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Regia.App.ViewModels;
using Regia.Core.Input;
using Regia.Core.Media;
using Regia.Core.Settings;
using Regia.Core.Stress;
using Regia.Core.Wave;
using Regia.Output.Interop;
using Regia.Output.Ppt;
using Serilog;

namespace Regia.App.Services;

/// <summary>
/// Stress test (<c>JustSlides.exe --stress N [--faults]</c>): manda in onda a ripetizione i file della scaletta passando dagli
/// stessi comandi dell'operatore, con azioni, uscite e (con <c>--faults</c>) guasti iniettati, e misura memoria, handle e tempi.
/// La sequenza viene da <see cref="StressPlan"/> (seme registrato nel log). Esc = PANIC e interrompe il test.
/// Gira sul thread UI come l'operatore; ogni attesa ha un tetto, a tetto scaduto il ciclo è "fallito" e si torna al Tappo.
/// </summary>
public sealed class StressRunner
{
    private static readonly TimeSpan GoTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleCpuWindow = TimeSpan.FromSeconds(20);

    private readonly MainViewModel _viewModel;
    private readonly WaveController _wave;
    private readonly PptHostClient _ppt;
    private readonly OutputSupervisor _supervisor;
    private readonly CancellationTokenSource _cts = new();
    private bool _ownPanic;

    public StressRunner(MainViewModel viewModel, WaveController wave, PptHostClient ppt, OutputSupervisor supervisor)
    {
        _viewModel = viewModel;
        _wave = wave;
        _ppt = ppt;
        _supervisor = supervisor;

        // Un PANIC che non viene da noi è l'operatore (Esc): si ferma tutto.
        _viewModel.PanicPerformed += () =>
        {
            if (!_ownPanic && !_cts.IsCancellationRequested)
            {
                Log.Warning("STRESS: interrotto dall'operatore (PANIC)");
                _cts.Cancel();
            }
        };
    }

    public async Task RunAsync(int cycles, bool faults)
    {
        var seed = Environment.TickCount;
        var report = new StressReport();
        var interrupted = false;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var folder = Path.Combine(SettingsStore.DefaultDirectory, "logs");
        var csvPath = Path.Combine(folder, $"stress-{stamp}.csv");
        var idleLines = new List<string>();

        try
        {
            await WaitForItemsReadyAsync();

            // Si prova solo ciò che l'operatore potrebbe mandare in onda: pronto e di tipo supportato.
            var targets = _viewModel.Items
                .Where(i => i.CanGoOnAir && i.Kind is MediaKind.Image or MediaKind.Pdf or MediaKind.Video or MediaKind.Ppt)
                .ToList();

            if (targets.Count == 0)
            {
                Log.Error("STRESS: nessun file pronto in scaletta, test annullato");
                _viewModel.StressStatus = "STRESS TEST annullato: nessun file pronto in scaletta.";
                return;
            }

            var plan = StressPlan.Build(targets.Select(i => new StressTarget(i.DisplayName, i.Kind)).ToList(), cycles, seed, faults);
            Log.Warning("STRESS: inizio, {Cycles} cicli, guasti {Faults}, seme {Seed}, {Files} file, report in {Path}",
                cycles, faults ? "sì" : "no", seed, targets.Count, csvPath);

            await WaitForReadyAsync(ExitTimeout);
            idleLines.Add(await MeasureIdleCpuAsync("iniziale"));

            var lastCpu = ProcessMetrics.Take().CpuTime;
            var lastTime = Stopwatch.GetTimestamp();

            foreach (var cycle in plan)
            {
                _cts.Token.ThrowIfCancellationRequested();
                _viewModel.StressStatus = $"STRESS TEST {cycle.Index}/{plan.Count}: {cycle.Target.Name}" +
                                          (cycle.Fault != StressFault.None ? $" (guasto: {cycle.Fault})" : "") +
                                          "   —   Esc = interrompi";

                var item = targets.First(t => t.DisplayName == cycle.Target.Name && t.Kind == cycle.Target.Kind);
                var (outcome, loadMs, note) = await RunCycleAsync(cycle, item, targets);

                var snapshot = ProcessMetrics.Take();
                var elapsed = Stopwatch.GetElapsedTime(lastTime);
                var cpu = elapsed.TotalSeconds > 0
                    ? (snapshot.CpuTime - lastCpu).TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100
                    : 0;
                lastCpu = snapshot.CpuTime;
                lastTime = Stopwatch.GetTimestamp();

                report.Add(new StressSample(
                    cycle.Index, cycle.Target.Name, cycle.Target.Kind.ToString(), cycle.Fault, outcome, loadMs,
                    _wave.State.ToString(), snapshot.PrivateMemoryMb, snapshot.Handles, snapshot.Threads,
                    snapshot.GdiObjects, snapshot.UserObjects, cpu,
                    ProcessMetrics.CountAlive(_ppt.HostPid, _ppt.PowerPointPid), note));

                Log.Information("STRESS: ciclo {Cycle}/{Total} {Outcome} ({Load} ms, {Mem:F0} MB, {Handles} handle) {Note}",
                    cycle.Index, plan.Count, outcome, loadMs, snapshot.PrivateMemoryMb, snapshot.Handles, note);

                // Un ciclo fallito lascia la regia a Tappo/Errore (RunCycle ripulisce): si continua, è proprio ciò che si vuole vedere.
                if (cycle.Index % 20 == 0)
                    WriteReport(report, csvPath, idleLines, interrupted: false);
            }

            idleLines.Add(await MeasureIdleCpuAsync("finale"));
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
        }
        catch (Exception ex)
        {
            // Il test non deve mai far cadere la regia.
            Log.Error(ex, "STRESS: errore nel test");
        }
        finally
        {
            _ownPanic = true;
            try
            {
                _viewModel.PerformKeyAction(KeyAction.Panic);
            }
            finally
            {
                _ownPanic = false;
            }

            var summary = WriteReport(report, csvPath, idleLines, interrupted);
            Log.Warning("STRESS: fine.\n{Summary}", summary.ToText());
            _viewModel.StressStatus = "STRESS TEST finito" + (interrupted ? " (interrotto)" : "") + ": " +
                                      summary.ToText().Replace("\r\n", " | ").Replace("\n", " | ").TrimEnd(' ', '|') +
                                      $" — report: {csvPath}";
        }
    }

    // ---------------------------------------------------------------- un ciclo

    private async Task<(StressOutcome Outcome, int LoadMs, string Note)> RunCycleAsync(
        StressCycle cycle, MediaItem item, List<MediaItem> targets)
    {
        var notes = new List<string>();
        var loadMs = 0;

        try
        {
            if (!await WaitForReadyAsync(ExitTimeout))
                return (StressOutcome.Failed, 0, "la regia non era pronta (Tappo/Errore) a inizio ciclo");

            _viewModel.SelectedItem = item;
            var watch = Stopwatch.StartNew();
            _viewModel.PerformKeyAction(KeyAction.Go);

            // Avanti durante la dissolvenza in entrata: va provato mentre la transizione è in corso.
            if (cycle.Actions.Contains(StressAction.RudeNextDuringFade))
            {
                await WaitForAsync(() => _wave.State is WaveState.InTransizioneIn or WaveState.InOnda or WaveState.Errore, GoTimeout);
                if (_wave.State == WaveState.InTransizioneIn)
                {
                    _viewModel.PerformKeyAction(KeyAction.Next);
                    notes.Add("avanti durante la dissolvenza");
                }
            }

            if (!await WaitForAsync(() => _wave.State is WaveState.InOnda or WaveState.Errore, GoTimeout))
                return await FailCycleAsync($"timeout GO ({_wave.State})", 0);

            loadMs = (int)watch.ElapsedMilliseconds;

            if (_wave.State == WaveState.Errore && cycle.Fault == StressFault.None)
                return await FailCycleAsync("la messa in onda è finita in Errore", loadMs);

            foreach (var action in cycle.Actions.Where(a => a != StressAction.RudeNextDuringFade))
            {
                if (_wave.State != WaveState.InOnda)
                {
                    notes.Add($"fuori onda prima di {action} (contenuto finito da solo?)");
                    break;
                }

                if (!await PerformActionAsync(action, notes))
                    return await FailCycleAsync($"azione {action} non risolta ({_wave.State})", loadMs);
            }

            if (cycle.Fault != StressFault.None)
                return await InjectFaultAsync(cycle, loadMs, notes);

            return await ExitAsync(cycle, item, targets, loadMs, notes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "STRESS: eccezione nel ciclo {Cycle}", cycle.Index);
            return await FailCycleAsync("eccezione: " + ex.Message, loadMs);
        }
    }

    private async Task<bool> PerformActionAsync(StressAction action, List<string> notes)
    {
        switch (action)
        {
            case StressAction.Next:
                _viewModel.PerformKeyAction(KeyAction.Next);
                break;
            case StressAction.Previous:
                _viewModel.PerformKeyAction(KeyAction.Previous);
                break;
            case StressAction.PlayPause:
                _viewModel.PerformKeyAction(KeyAction.PlayPause);
                break;
            case StressAction.SeekForward:
                _viewModel.SeekForwardCommand.Execute(null);
                break;
            case StressAction.SeekBack:
                _viewModel.SeekBackCommand.Execute(null);
                break;

            case StressAction.RudeDoubleGo:
                // Il primo GO da InOnda è un cambio file (via Tappo, stesso file); il secondo, subito dopo, deve essere ignorato.
                _viewModel.PerformKeyAction(KeyAction.Go);
                _viewModel.PerformKeyAction(KeyAction.Go);
                notes.Add("doppio GO");
                return await WaitForAsync(() => _wave.State == WaveState.InOnda, GoTimeout);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(400), _cts.Token);

        // Un video corto può finire da solo mentre si agisce (fine = ritorno al Tappo): non è un guasto della regia.
        return _wave.State is WaveState.InOnda or WaveState.InTransizioneOut or WaveState.Tappo or WaveState.Errore;
    }

    private async Task<(StressOutcome, int, string)> ExitAsync(
        StressCycle cycle, MediaItem item, List<MediaItem> targets, int loadMs, List<string> notes)
    {
        if (cycle.Exit == StressExit.SwitchFile && _wave.State == WaveState.InOnda && targets.Count > 1)
        {
            // Cambio file: GO su un altro file mentre uno è in onda (Tappo → chiusura → nuovo file).
            var other = targets[(targets.IndexOf(item) + 1) % targets.Count];
            _viewModel.SelectedItem = other;
            _viewModel.PerformKeyAction(KeyAction.Go);
            notes.Add("cambio file → " + other.DisplayName);

            if (!await WaitForAsync(() => _wave.State is WaveState.InOnda or WaveState.Errore, GoTimeout) ||
                _wave.State == WaveState.Errore)
                return await FailCycleAsync($"cambio file non riuscito ({_wave.State})", loadMs);
        }

        if (cycle.Exit == StressExit.Panic)
        {
            _ownPanic = true;
            try
            {
                _viewModel.PerformKeyAction(KeyAction.Panic);
            }
            finally
            {
                _ownPanic = false;
            }

            // PANIC è un taglio netto: lo stato deve essere Tappo subito, senza attese.
            if (_wave.State != WaveState.Tappo)
                return await FailCycleAsync($"dopo PANIC lo stato è {_wave.State}", loadMs);

            return (StressOutcome.Ok, loadMs, string.Join("; ", notes));
        }

        if (_wave.State == WaveState.InOnda)
            _viewModel.PerformKeyAction(KeyAction.BackToTappo);

        if (!await WaitForAsync(() => _wave.State is WaveState.Tappo or WaveState.Errore, ExitTimeout) || _wave.State == WaveState.Errore)
            return await FailCycleAsync($"ritorno al Tappo non riuscito ({_wave.State})", loadMs);

        return (StressOutcome.Ok, loadMs, string.Join("; ", notes));
    }

    // ---------------------------------------------------------------- guasti

    private async Task<(StressOutcome, int, string)> InjectFaultAsync(StressCycle cycle, int loadMs, List<string> notes)
    {
        Log.Warning("STRESS: guasto {Fault} nel ciclo {Cycle}", cycle.Fault, cycle.Index);

        switch (cycle.Fault)
        {
            case StressFault.KillPowerPoint or StressFault.KillPptHost:
            {
                if (!_ppt.KillOwnedProcessForTest(host: cycle.Fault == StressFault.KillPptHost))
                    return await FailCycleAsync("kill non riuscito (processo non nostro o già assente)", loadMs);

                // Il watchdog deve accorgersene: Tappo immediato e Errore, la regia resta viva.
                if (!await WaitForAsync(() => _wave.State is WaveState.Errore or WaveState.Tappo, FaultTimeout))
                    return await FailCycleAsync($"dopo il kill lo stato è rimasto {_wave.State}", loadMs);
                break;
            }

            case StressFault.SimulatedFail:
                _viewModel.HandleError("Stress: errore simulato", new InvalidOperationException("Errore simulato dallo stress test"));
                if (_wave.State != WaveState.Errore)
                    return await FailCycleAsync($"errore simulato ma lo stato è {_wave.State}", loadMs);
                break;

            case StressFault.OutputLostAndReturned:
            {
                _ownPanic = true;
                try
                {
                    _supervisor.InjectLost();
                }
                finally
                {
                    _ownPanic = false;
                }

                if (_wave.State != WaveState.Tappo || !_viewModel.HasOutputLost)
                    return await FailCycleAsync($"output perso: stato {_wave.State}, banner {_viewModel.HasOutputLost}", loadMs);

                // GO rifiutato finché l'output manca.
                _viewModel.PerformKeyAction(KeyAction.Go);
                await Task.Delay(TimeSpan.FromMilliseconds(600), _cts.Token);
                if (_wave.State != WaveState.Tappo)
                    return await FailCycleAsync($"GO accettato con l'output perso ({_wave.State})", loadMs);

                _supervisor.InjectReturned();
                if (!await WaitForAsync(() => !_viewModel.HasOutputLost, FaultTimeout))
                    return await FailCycleAsync("output tornato ma il banner è rimasto", loadMs);
                notes.Add("output perso e tornato");
                break;
            }
        }

        // Ripristino: da Errore si torna a Tappo con un PANIC (come farebbe l'operatore), poi la regia deve essere pronta.
        _ownPanic = true;
        try
        {
            if (_wave.State != WaveState.Tappo)
                _viewModel.PerformKeyAction(KeyAction.Panic);
        }
        finally
        {
            _ownPanic = false;
        }

        if (!await WaitForReadyAsync(ExitTimeout))
            return await FailCycleAsync("la regia non è tornata pronta dopo il guasto", loadMs);

        return (StressOutcome.FaultRecovered, loadMs, string.Join("; ", notes));
    }

    // ---------------------------------------------------------------- utilità

    /// <summary>Copie e pre-flight partono all'avvio: si aspetta che il numero di file pronti smetta di cambiare (tetto 90 s).</summary>
    private async Task WaitForItemsReadyAsync()
    {
        var deadline = Stopwatch.GetTimestamp() + 90L * Stopwatch.Frequency;
        var lastCount = -1;
        var stableSince = Stopwatch.GetTimestamp();

        while (Stopwatch.GetTimestamp() < deadline)
        {
            var count = _viewModel.Items.Count(i => i.CanGoOnAir);
            if (count != lastCount)
            {
                lastCount = count;
                stableSince = Stopwatch.GetTimestamp();
            }
            else if (count > 0 && Stopwatch.GetElapsedTime(stableSince) > TimeSpan.FromSeconds(5))
            {
                return;
            }

            _viewModel.StressStatus = $"STRESS TEST: attendo che i file siano pronti ({count} pronti)...";
            await Task.Delay(500, _cts.Token);
        }
    }

    /// <summary>Ciclo fallito: Tappo immediato (stato pulito per il ciclo dopo) e nota.</summary>
    private async Task<(StressOutcome, int, string)> FailCycleAsync(string reason, int loadMs)
    {
        Log.Error("STRESS: ciclo fallito: {Reason}", reason);

        _ownPanic = true;
        try
        {
            _viewModel.PerformKeyAction(KeyAction.Panic);
        }
        finally
        {
            _ownPanic = false;
        }

        await WaitForReadyAsync(TimeSpan.FromSeconds(5));
        return (StressOutcome.Failed, loadMs, reason);
    }

    private Task<bool> WaitForReadyAsync(TimeSpan timeout) =>
        WaitForAsync(() => _wave.State is WaveState.Tappo or WaveState.Errore, timeout);

    /// <summary>Attende una condizione con un tetto (controllo ogni 50 ms); false = tempo scaduto. Annullabile con Esc.</summary>
    private async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                return false;

            await Task.Delay(50, _cts.Token);
        }

        return true;
    }

    /// <summary>CPU della regia a Tappo fermo (con Tappo video è il costo del loop a pieno schermo; obiettivo &lt; 10%).</summary>
    private async Task<string> MeasureIdleCpuAsync(string phase)
    {
        _viewModel.StressStatus = $"STRESS TEST: misura della CPU a Tappo fermo ({phase}, {IdleCpuWindow.TotalSeconds:F0} s)";

        var before = ProcessMetrics.Take().CpuTime;
        var start = Stopwatch.GetTimestamp();
        await Task.Delay(IdleCpuWindow, _cts.Token);
        var used = (ProcessMetrics.Take().CpuTime - before).TotalSeconds;
        var percent = used / Stopwatch.GetElapsedTime(start).TotalSeconds / Environment.ProcessorCount * 100;

        var tappo = _viewModel.Settings.Tappo;
        var line = $"CPU regia a Tappo fermo ({phase}): {percent:F1}% su {Environment.ProcessorCount} core (Tappo: {tappo.Kind})";
        Log.Information("STRESS: {Line}", line);
        return line;
    }

    private static StressSummary WriteReport(StressReport report, string csvPath, List<string> idleLines, bool interrupted)
    {
        var summary = report.Summarize(interrupted);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            File.WriteAllText(csvPath, report.ToCsv());
            File.WriteAllText(Path.ChangeExtension(csvPath, ".txt"),
                summary.ToText() + Environment.NewLine + string.Join(Environment.NewLine, idleLines) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "STRESS: impossibile scrivere il report {Path}", csvPath);
        }

        return summary;
    }
}
