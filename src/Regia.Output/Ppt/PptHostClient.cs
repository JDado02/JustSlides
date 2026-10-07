using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Regia.Core.Ppt;
using Regia.Core.Settings;
using Serilog;

namespace Regia.Output.Ppt;

public enum PptHostState
{
    Stopped,
    Starting,
    Running
}

/// <summary>
/// Lato regia del collegamento con PptHost: avvia il processo (e PowerPoint) dentro un Job Object, parla con lui
/// sulla named pipe e lo sorveglia. Se PptHost o PowerPoint non rispondono, in quest'ordine: (1) <see cref="Faulted"/>
/// (il presenter porta subito al Tappo), (2) termina PptHost, (3) termina il POWERPNT.EXE <i>nostro</i>, (4) logga,
/// (5) resta pronto a ripartire alla richiesta successiva. La regia non tocca mai PowerPoint direttamente.
/// Gli eventi arrivano da thread del pool: chi li ascolta deve passare al thread UI.
/// </summary>
public sealed class PptHostClient : IDisposable
{
    public const string ForeignInstanceMessage =
        "PowerPoint è già aperto (probabilmente dall'utente): chiuderlo prima di mandare in onda una presentazione.";

    private const string HostExeName = "Regia.PptHost.exe";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartShowTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShortOperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestSlack = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);

    private readonly OwnedProcessStore _store = new(OwnedProcessStore.DefaultPath);
    private readonly object _gate = new();
    private readonly object _watchdogLock = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PptMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private JobObject? _job;
    private Task? _startTask;
    private PptHostState _state = PptHostState.Stopped;
    private string? _note;
    private int _generation;
    private bool _disposed;
    private long _nextId;

    private Process? _host;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private WatchdogPolicy? _watchdog;
    private OwnedProcess? _hostRecord;
    private OwnedProcess? _powerPoint;

    public PptHostClient(AppSettings settings)
    {
        Settings = settings;
    }

    /// <summary>Impostazioni correnti (timeout): vanno aggiornate quando l'operatore le cambia.</summary>
    public AppSettings Settings { get; set; }

    public PptHostState State => _state;

    /// <summary>Stato per l'operatore ("PowerPoint: pronto"...).</summary>
    public string StatusText => _state switch
    {
        PptHostState.Starting => "PowerPoint: avvio in corso...",
        PptHostState.Running => "PowerPoint: pronto",
        _ => _note is null ? "PowerPoint: non avviato" : "PowerPoint: " + _note
    };

    /// <summary>La slide corrente è cambiata (slide, totale).</summary>
    public event Action<int, int>? SlideChanged;

    /// <summary>Lo slideshow è finito da solo (o PowerPoint è morto: <see cref="ShowEndedData.Faulted"/>).</summary>
    public event Action<ShowEndedData>? ShowEnded;

    /// <summary>PptHost/PowerPoint sono fuori uso: il watchdog sta per terminarli. Parametro: motivo.</summary>
    public event Action<string>? Faulted;

    /// <summary>Messaggio per l'operatore non legato a un GO (es. pre-avvio fallito).</summary>
    public event Action<string>? Warning;

    public event Action? StatusChanged;

    /// <summary>Esiste un POWERPNT.EXE che non abbiamo avviato noi (di norma aperto dall'utente).</summary>
    private bool _prewarmInFlight;

    public bool ForeignPowerPointRunning
    {
        get
        {
            var ours = _powerPoint?.Pid;
            var processes = Process.GetProcessesByName("POWERPNT");
            try
            {
                return processes.Any(p => p.Id != ours);
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
    }

    /// <summary>Avvio della regia: termina PptHost/PowerPoint rimasti da un crash precedente (solo se PID e ora di avvio coincidono).</summary>
    public void CleanupOrphans()
    {
        foreach (var record in _store.Load())
        {
            if (TryKillOwned(record, "orfano"))
                Log.Warning("Terminato {Role} orfano (pid {Pid}) di una sessione precedente", record.Role, record.Pid);
        }

        _store.Clear();
    }

    /// <summary>Avvia PptHost e PowerPoint in background, così il primo GO su un PPT è rapido.</summary>
    public void Prewarm()
    {
        if (_disposed)
            return;

        // Già in avvio o in funzione (più PPT in lista fanno più richieste): la seconda richiesta vedrebbe il
        // POWERPNT appena lanciato da noi prima che sia registrato e lo scambierebbe per quello dell'utente.
        lock (_gate)
        {
            if (_prewarmInFlight)
                return;

            _prewarmInFlight = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync();
            }
            catch (PptException ex) when (ex.IsForeignInstance)
            {
                Warning?.Invoke(ForeignInstanceMessage);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pre-avvio di PowerPoint non riuscito");
                Warning?.Invoke("PowerPoint non è partito: " + ex.Message);
            }
            finally
            {
                lock (_gate)
                    _prewarmInFlight = false;
            }
        });
    }

    /// <summary>PptHost in esecuzione e PowerPoint avviato (se serve li riavvia). Lancia <see cref="PptException"/>.</summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ForeignPowerPointRunning)
            throw new PptException(PptErrors.ForeignInstance, ForeignInstanceMessage);

        Task start;
        lock (_gate)
        {
            if (_state == PptHostState.Running)
                start = Task.CompletedTask;
            else
                start = _startTask ??= Task.Run(StartHostAsync);
        }

        await start.WaitAsync(cancellationToken);

        // Idempotente: se PowerPoint è vivo risponde subito, se è morto lo riavvia.
        var response = await SendAsync(PptCommands.Launch, null, OpenTimeout(), cancellationToken);
        if (PptProtocol.ReadData<LaunchResult>(response) is { PowerPointPid: > 0 } launch)
            RegisterPowerPoint(launch.PowerPointPid);
    }

    /// <summary>Invia un comando e attende la risposta. Lancia <see cref="PptException"/> (errore di PowerPoint, timeout, host caduto).</summary>
    public async Task<PptMessage> SendAsync(string command, object? args, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var response = await SendRawAsync(command, args, timeout, cancellationToken);
        if (!response.Ok)
            throw new PptException(response.Code ?? PptErrors.Generic, response.Error ?? "errore sconosciuto");

        return response;
    }

    /// <summary>Tempo massimo di attesa di una risposta: sopra quello del watchdog, che scatta per primo se PowerPoint è bloccato.</summary>
    public TimeSpan OpenTimeout() => TimeSpan.FromMilliseconds(Settings.PptOpenTimeoutMs) + RequestSlack;

    public TimeSpan StartShowRequestTimeout() => StartShowTimeout + RequestSlack;

    public TimeSpan DefaultTimeout() => ShortOperationTimeout + RequestSlack;

    // --- Avvio ----------------------------------------------------------------------------------------------------

    private async Task StartHostAsync()
    {
        int generation;
        lock (_gate)
        {
            generation = ++_generation;
            _state = PptHostState.Starting;
            _note = null;
        }

        RaiseStatusChanged();

        try
        {
            _job ??= new JobObject();

            var exe = Path.Combine(AppContext.BaseDirectory, HostExeName);
            if (!File.Exists(exe))
                throw new PptException(PptException.HostStartFailed, $"{HostExeName} non trovato accanto alla regia");

            var pipeName = "Regia.PptHost." + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--parent");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            var process = Process.Start(startInfo)
                          ?? throw new PptException(PptException.HostStartFailed, "Impossibile avviare PptHost");
            _job.Assign(process);

            lock (_gate)
            {
                _host = process;
                _pipe = pipe;
                _hostRecord = new OwnedProcess(process.Id, process.StartTime.ToUniversalTime(), "PptHost");
                _powerPoint = null;
                _watchdog = new WatchdogPolicy(TimeSpan.FromMilliseconds(Settings.PptHostTimeoutMs), OperationTimeout, () => _clock.Elapsed);
            }

            PersistProcesses();
            Log.Information("PptHost avviato (pid {Pid}, pipe {Pipe})", process.Id, pipeName);

            using (var connectTimeout = new CancellationTokenSource(ConnectTimeout))
            {
                try
                {
                    await pipe.WaitForConnectionAsync(connectTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new PptException(PptException.HostStartFailed, "PptHost non si è collegato in tempo");
                }
            }

            var utf8 = new UTF8Encoding(false);
            var reader = new StreamReader(pipe, utf8);
            lock (_gate)
                _writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };

            _ = ReadLoopAsync(generation, reader);

            await SendAsync(PptCommands.Hello, null, HelloTimeout);

            lock (_gate)
            {
                _state = PptHostState.Running;
                _watchdog!.Reset();
            }

            _ = WatchdogLoopAsync(generation);
            Log.Information("PptHost pronto");
            RaiseStatusChanged();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Avvio di PptHost non riuscito");
            TearDown(generation, "avvio non riuscito");
            throw ex as PptException ?? new PptException(PptException.HostStartFailed, "PptHost non è partito: " + ex.Message);
        }
        finally
        {
            lock (_gate)
                _startTask = null;
        }
    }

    /// <summary>PID del POWERPNT.EXE avviato da noi; 0 se non ce n'è uno (sessione audio da non toccare).</summary>
    public int PowerPointPid => _powerPoint?.Pid ?? 0;

    private void RegisterPowerPoint(int pid)
    {
        if (_powerPoint?.Pid == pid)
            return;

        try
        {
            using var process = Process.GetProcessById(pid);
            _job?.Assign(process);
            _powerPoint = new OwnedProcess(pid, process.StartTime.ToUniversalTime(), "PowerPoint");
            PersistProcesses();
            Log.Information("PowerPoint (pid {Pid}) registrato: assegnato al Job Object", pid);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PowerPoint (pid {Pid}) non registrato", pid);
        }
    }

    private TimeSpan OperationTimeout(string operation) => operation switch
    {
        PptCommands.Launch or PptCommands.Open => TimeSpan.FromMilliseconds(Settings.PptOpenTimeoutMs),
        PptCommands.StartShow => StartShowTimeout,
        _ => ShortOperationTimeout
    };

    // --- Pipe -----------------------------------------------------------------------------------------------------

    private async Task<PptMessage> SendRawAsync(string command, object? args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        StreamWriter? writer;
        lock (_gate)
            writer = _writer;

        if (writer is null)
            throw new PptException(PptException.HostFailure, "PptHost non è in esecuzione");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<PptMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await writer.WriteLineAsync(PptProtocol.Serialize(PptProtocol.Request(id, command, args)));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new PptException(PptException.HostFailure, "PptHost non è raggiungibile: " + ex.Message);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            Log.Warning("Nessuna risposta al comando {Command} entro {Seconds:0.#} s", command, timeout.TotalSeconds);
            throw new PptException(PptException.RequestTimeout, $"PowerPoint non ha risposto a \"{command}\" entro {timeout.TotalSeconds:0} s");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(int generation, StreamReader reader)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    break;

                if (!PptProtocol.TryParse(line, out var message))
                {
                    Log.Warning("Riga non valida da PptHost, ignorata: {Line}", line.Length > 200 ? line[..200] : line);
                    continue;
                }

                switch (message.Kind)
                {
                    case PptMessageKind.Response:
                        if (_pending.TryGetValue(message.Id, out var tcs))
                            tcs.TrySetResult(message);
                        break;
                    case PptMessageKind.Event:
                        Dispatch(message);
                        break;
                }
            }

            HandleFailure(generation, "PptHost ha chiuso la pipe");
        }
        catch (Exception ex)
        {
            HandleFailure(generation, "errore di lettura dalla pipe: " + ex.Message);
        }
    }

    private void Dispatch(PptMessage message)
    {
        try
        {
            switch (message.Name)
            {
                case PptEvents.SlideChanged when PptProtocol.ReadData<SlideChangedData>(message) is { } slide:
                    SlideChanged?.Invoke(slide.Slide, slide.Total);
                    break;
                case PptEvents.ShowEnded:
                    ShowEnded?.Invoke(PptProtocol.ReadData<ShowEndedData>(message) ?? new ShowEndedData(false, null));
                    break;
            }
        }
        catch (Exception ex)
        {
            // Un ascoltatore che lancia non deve fermare la lettura della pipe.
            Log.Error(ex, "Errore nella gestione dell'evento {Event}", message.Name);
        }
    }

    // --- Watchdog -------------------------------------------------------------------------------------------------

    private async Task WatchdogLoopAsync(int generation)
    {
        using var timer = new PeriodicTimer(PingInterval);
        while (await timer.WaitForNextTickAsync())
        {
            if (generation != _generation || _disposed)
                return;

            _ = PingAsync(generation);

            WatchdogVerdict verdict;
            lock (_watchdogLock)
                verdict = _watchdog?.Evaluate() ?? WatchdogVerdict.Healthy;

            if (verdict != WatchdogVerdict.Healthy)
            {
                HandleFailure(generation, verdict == WatchdogVerdict.PingTimeout
                    ? $"PptHost non risponde al ping da oltre {Settings.PptHostTimeoutMs} ms"
                    : "PowerPoint è bloccato in una chiamata COM oltre il tempo massimo");
                return;
            }
        }
    }

    private async Task PingAsync(int generation)
    {
        try
        {
            var response = await SendRawAsync(PptCommands.Ping, null, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (generation != _generation)
                return;

            var ping = PptProtocol.ReadData<PingResult>(response);
            lock (_watchdogLock)
                _watchdog?.RecordPong(ping?.BusyOperation, TimeSpan.FromMilliseconds(ping?.BusyMs ?? 0));
        }
        catch (Exception ex)
        {
            Log.Debug("Ping non riuscito: {Message}", ex.Message);
        }
    }

    /// <summary>Guasto: Tappo (evento), kill di PptHost e del nostro PowerPoint, log, pronto a ripartire.</summary>
    private void HandleFailure(int generation, string reason)
    {
        lock (_gate)
        {
            if (generation != _generation || _state == PptHostState.Stopped || _disposed)
                return;
        }

        Log.Error("PptHost/PowerPoint fuori uso: {Reason}", reason);

        // (1) subito il Tappo, prima di qualsiasi altra cosa.
        try
        {
            Faulted?.Invoke(reason);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nella notifica del guasto");
        }

        // (2)(3)(4)(5)
        TearDown(generation, reason);
    }

    private void TearDown(int generation, string reason)
    {
        Process? host;
        OwnedProcess? hostRecord;
        OwnedProcess? powerPoint;
        NamedPipeServerStream? pipe;

        lock (_gate)
        {
            if (generation != _generation)
                return;

            _generation++;
            host = _host;
            hostRecord = _hostRecord;
            powerPoint = _powerPoint;
            pipe = _pipe;
            _host = null;
            _hostRecord = null;
            _powerPoint = null;
            _pipe = null;
            _writer = null;
            _watchdog = null;
            _state = PptHostState.Stopped;
            _note = _disposed ? null : "riavviato dopo un guasto";
        }

        foreach (var (id, tcs) in _pending.ToArray())
        {
            if (tcs.TrySetException(new PptException(PptException.HostFailure, "PptHost è stato terminato: " + reason)))
                _pending.TryRemove(id, out _);
        }

        if (host is not null && hostRecord is not null)
        {
            Log.Warning("Termino PptHost (pid {Pid})", hostRecord.Pid);
            TryKillOwned(hostRecord, "guasto", host);
        }

        if (powerPoint is not null)
        {
            Log.Warning("Termino PowerPoint (pid {Pid})", powerPoint.Pid);
            TryKillOwned(powerPoint, "guasto");
        }

        try
        {
            pipe?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Chiusura pipe");
        }

        host?.Dispose();
        _store.Clear();
        Log.Information("PptHost fermo: ripartirà alla prossima richiesta ({Reason})", reason);
        RaiseStatusChanged();
    }

    /// <summary>
    /// Termina un processo solo se è davvero quello registrato (stesso PID e stessa ora di avvio):
    /// un PID riusato o un PowerPoint dell'utente non si toccano mai.
    /// </summary>
    private static bool TryKillOwned(OwnedProcess record, string why, Process? known = null)
    {
        try
        {
            using var process = known ?? Process.GetProcessById(record.Pid);
            if (process.HasExited)
                return false;

            if (!record.IsSameProcess(process.Id, process.StartTime.ToUniversalTime()))
            {
                Log.Warning("Il processo {Pid} non è più quello registrato ({Role}): non lo termino", record.Pid, record.Role);
                return false;
            }

            process.Kill();
            process.WaitForExit(2000);
            Log.Information("Processo {Role} (pid {Pid}) terminato ({Why})", record.Role, record.Pid, why);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // già sparito
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile terminare {Role} (pid {Pid})", record.Role, record.Pid);
            return false;
        }
    }

    private void PersistProcesses()
    {
        var records = new List<OwnedProcess>();
        if (_hostRecord is not null)
            records.Add(_hostRecord);
        if (_powerPoint is not null)
            records.Add(_powerPoint);

        _store.Save(records);
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Chiusura ordinata di PowerPoint, con un tetto di tempo; poi si termina quello che resta.
        try
        {
            if (_state == PptHostState.Running)
                Task.Run(() => SendRawAsync(PptCommands.Quit, null, TimeSpan.FromSeconds(2), CancellationToken.None)).Wait(2500);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Quit di PptHost non riuscito");
        }

        TearDown(_generation, "chiusura della regia");
        _job?.Dispose();
        _job = null;
    }
}
