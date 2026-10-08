using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using Regia.Core.Ppt;
using Serilog;

namespace Regia.PptHost;

/// <summary>
/// Lato PptHost della named pipe. Un thread legge le richieste: il Ping risponde subito da qui (anche se il thread STA
/// è bloccato in una chiamata COM), tutto il resto passa dal Dispatcher STA nell'ordine di arrivo. Risposte ed eventi
/// escono da un solo punto, con un lucchetto.
/// </summary>
internal sealed class HostServer
{
    private readonly string _pipeName;
    private readonly Dispatcher _sta;
    private readonly BusyTracker _busy;
    private readonly Func<PowerPointDriver> _driverFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private StreamWriter? _writer;
    private PowerPointDriver? _driver;

    public HostServer(string pipeName, Dispatcher sta, BusyTracker busy, Func<PowerPointDriver> driverFactory)
    {
        _pipeName = pipeName;
        _sta = sta;
        _busy = busy;
        _driverFactory = driverFactory;
    }

    /// <summary>Connette alla pipe della regia e serve le richieste fino alla chiusura.</summary>
    public async Task RunAsync()
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10_000);
        Log.Information("Connesso alla pipe {Pipe}", _pipeName);

        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(pipe, utf8);
        _writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };

        // Il driver nasce sul thread STA (il suo DispatcherTimer appartiene a quel thread).
        _driver = await _sta.InvokeAsync(_driverFactory);
        _driver.SlideChanged += (slide, total) =>
            _ = SendAsync(PptProtocol.Event(PptEvents.SlideChanged, new SlideChangedData(slide, total)));
        _driver.ShowEnded += (faulted, reason) =>
            _ = SendAsync(PptProtocol.Event(PptEvents.ShowEnded, new ShowEndedData(faulted, reason)));

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                Log.Information("Pipe chiusa dalla regia");
                break;
            }

            if (!PptProtocol.TryParse(line, out var message) || message.Kind != PptMessageKind.Request)
            {
                Log.Warning("Riga non valida dalla regia, ignorata: {Line}", line.Length > 200 ? line[..200] : line);
                continue;
            }

            if (message.Name == PptCommands.Ping)
            {
                var (operation, busyMs) = _busy.Snapshot();
                _ = SendAsync(PptProtocol.Success(message.Id, new PingResult(operation, busyMs)));
                continue;
            }

            // Accodato qui, nell'ordine di arrivo; la risposta parte a lavoro finito.
            var work = _sta.InvokeAsync(() => Execute(message));
            _ = message.Name switch
            {
                PptCommands.SelfTest => RespondSelfTestAsync(message.Id, work.Task),
                PptCommands.ExportSlides => RespondExportAsync(message.Id, work.Task),
                _ => RespondAsync(message.Id, work.Task)
            };

            if (message.Name == PptCommands.Quit)
                break;
        }

        // Pipe chiusa senza Quit (regia morta): si chiude PowerPoint con garbo, con un tetto di tempo.
        await ShutdownAsync();
    }

    private async Task RespondAsync(long id, Task<object?> work)
    {
        PptMessage response;
        try
        {
            response = PptProtocol.Success(id, await work);
        }
        catch (PptHostException ex)
        {
            Log.Warning("Comando fallito ({Code}): {Message}", ex.Code, ex.Message);
            response = PptProtocol.Failure(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore imprevisto in un comando");
            response = PptProtocol.Failure(id, PptErrors.Generic, $"{ex.GetType().Name}: {ex.Message}");
        }

        await SendAsync(response);
    }

    /// <summary>
    /// La prova tecnica può restare ferma su un dialogo di PowerPoint (accesso, attivazione): il thread STA sarebbe bloccato e non
    /// risponderebbe mai. Da qui, fuori dal thread STA, si controlla se un dialogo resta aperto: in tal caso si risponde subito con il
    /// suo titolo (la regia poi termina PowerPoint).
    /// </summary>
    private async Task RespondSelfTestAsync(long id, Task<object?> work)
    {
        using var cts = new CancellationTokenSource();
        var dialog = _driver!.WatchForDialogAsync(TimeSpan.FromSeconds(2.5), cts.Token);

        var first = await Task.WhenAny(work, dialog);
        cts.Cancel();

        if (first == dialog && !work.IsCompleted && await dialog is { } title)
        {
            Log.Warning("Verifica PowerPoint bloccata da un dialogo (\"{Title}\")", title);
            await SendAsync(PptProtocol.Success(id,
                new PptSelfTestResult(PptSelfTestSteps.Dialog, null, null, null, 0, title, 0)));
            return;
        }

        await RespondAsync(id, work);
    }

    /// <summary>
    /// Come la prova tecnica: un dialogo di PowerPoint (file con password, avviso) bloccherebbe il thread STA. Se resta aperto si
    /// risponde subito con un errore e la regia termina PowerPoint (il nostro).
    /// </summary>
    private async Task RespondExportAsync(long id, Task<object?> work)
    {
        using var cts = new CancellationTokenSource();
        var dialog = _driver!.WatchForDialogAsync(TimeSpan.FromSeconds(2.5), cts.Token);

        var first = await Task.WhenAny(work, dialog);
        cts.Cancel();

        if (first == dialog && !work.IsCompleted && await dialog is { } title)
        {
            Log.Warning("Esportazione del Tappo bloccata da un dialogo (\"{Title}\")", title);
            await SendAsync(PptProtocol.Failure(id, PptErrors.Dialog, $"PowerPoint mostra una finestra (\"{title}\"): il file potrebbe avere una password."));
            return;
        }

        await RespondAsync(id, work);
    }

    /// <summary>Eseguito sul thread STA.</summary>
    private object? Execute(PptMessage message)
    {
        var driver = _driver!;
        _busy.Begin(message.Name);
        try
        {
            switch (message.Name)
            {
                case PptCommands.Hello:
                    return new HelloResult(Environment.ProcessId, typeof(HostServer).Assembly.GetName().Version?.ToString() ?? "?");

                case PptCommands.Launch:
                    return new LaunchResult(driver.Launch());

                case PptCommands.Open:
                    var open = PptProtocol.ReadData<OpenArgs>(message)
                               ?? throw new PptHostException(PptErrors.Generic, "Open senza percorso");
                    return driver.Open(open.Path);

                case PptCommands.StartShow:
                    var start = PptProtocol.ReadData<StartShowArgs>(message)
                                ?? throw new PptHostException(PptErrors.Generic, "Start senza rettangolo");
                    return driver.StartShow(start);

                case PptCommands.Next:
                    return driver.Next();

                case PptCommands.Previous:
                    return driver.Previous();

                case PptCommands.GoTo:
                    var go = PptProtocol.ReadData<SlideChangedData>(message)
                             ?? throw new PptHostException(PptErrors.Generic, "GoTo senza slide");
                    return driver.GoTo(go.Slide);

                case PptCommands.EndShow:
                    driver.EndShow();
                    return null;

                case PptCommands.SelfTest:
                    return driver.SelfTest();

                case PptCommands.ExportSlides:
                    var export = PptProtocol.ReadData<ExportSlidesArgs>(message)
                                 ?? throw new PptHostException(PptErrors.Generic, "Esportazione senza argomenti");
                    return driver.ExportSlides(export);

                case PptCommands.Quit:
                    driver.Quit();
                    return null;

                default:
                    throw new PptHostException(PptErrors.Generic, $"Comando sconosciuto: {message.Name}");
            }
        }
        finally
        {
            _busy.End();
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            var quit = _sta.InvokeAsync(() => _driver?.Quit()).Task;
            await Task.WhenAny(quit, Task.Delay(3000));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Chiusura di PowerPoint non riuscita");
        }
    }

    private async Task SendAsync(PptMessage message)
    {
        var writer = _writer;
        if (writer is null)
            return;

        await _writeLock.WaitAsync();
        try
        {
            await writer.WriteLineAsync(PptProtocol.Serialize(message));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Debug("Scrittura sulla pipe non riuscita (regia chiusa?): {Message}", ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
