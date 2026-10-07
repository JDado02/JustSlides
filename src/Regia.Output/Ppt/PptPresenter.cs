using System.Windows;
using System.Windows.Threading;
using Regia.Core.Ppt;
using Regia.Core.Wave;
using Regia.Output.Audio;
using Serilog;

namespace Regia.Output.Ppt;

/// <summary>
/// Slideshow PowerPoint in onda tramite PptHost. Lo slideshow è una finestra di un altro processo: sta sopra la finestra
/// contenuto (che resta nera) e il Tappo viene riportato in cima da <see cref="OutputHost.GuardTappoOnTop"/>.
/// Next/Previous non bloccano mai la UI: il comando parte in background e la slide si aggiorna dagli eventi.
/// Gli eventi del client arrivano da thread del pool e qui si riportano sul thread UI.
/// </summary>
public sealed class PptPresenter : ISlideShowContent
{
    private const int FirstSlideRenderMs = 300;
    private static readonly TimeSpan AudioRestoreDelay = TimeSpan.FromSeconds(2);

    private readonly PptHostClient _client;
    private readonly OutputHost _output;
    private readonly string _path;
    private readonly Dispatcher _dispatcher;

    private volatile bool _closed;
    private bool _sentOpen;
    private bool _started;
    private bool _endRaised;
    private PageInfo? _page;
    private IDisposable? _guard;
    private ProcessAudioSession? _audio;
    private int _volume = 100;
    private bool _muted;

    public PptPresenter(PptHostClient client, OutputHost output, string path)
    {
        _client = client;
        _output = output;
        _path = path;
        _dispatcher = output.Content.Dispatcher;
    }

    public PageInfo? Page => _page;

    public event Action? PageChanged;

    public event Action? EndRequested;

    public event Action<Exception>? Faulted;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        // Un guasto di PptHost durante il caricamento si sente subito (oltre che come eccezione dalle richieste).
        _client.Faulted += OnClientFaulted;

        try
        {
            await _client.EnsureReadyAsync(cancellationToken);
            ThrowIfStopped(cancellationToken);

            _sentOpen = true; // da qui in poi Close() deve chiudere lo slideshow anche se PowerPoint è ancora a metà
            var opened = await _client.SendAsync(PptCommands.Open, new OpenArgs(_path), _client.OpenTimeout(), cancellationToken);
            var open = PptProtocol.ReadData<OpenResult>(opened)
                       ?? throw new InvalidOperationException("Risposta di PowerPoint non valida all'apertura");
            ThrowIfStopped(cancellationToken);

            // Audio: la sessione di POWERPNT.EXE (solo il nostro PID) con volume e Mute già impostati, prima che suoni qualcosa.
            StartAudioSession();

            // La guardia prima del lancio: appena PowerPoint si porta in primo piano il Tappo viene rimesso sopra, senza lampi.
            _guard = _output.GuardTappoOnTop();

            var (rect, gdiName, windowed) = _output.GetShowPlacement();
            var args = new StartShowArgs(gdiName, rect.X, rect.Y, rect.Width, rect.Height, windowed);
            var started = await _client.SendAsync(PptCommands.StartShow, args, _client.StartShowRequestTimeout(), cancellationToken);
            ThrowIfStopped(cancellationToken);

            var first = PptProtocol.ReadData<StartShowResult>(started);
            _page = new PageInfo(Math.Max(first?.Slide ?? 1, 1), first?.Total ?? open.Slides);
            _started = true;
            _output.AttachShowWindow(first?.Hwnd ?? 0);

            _client.SlideChanged += OnSlideChanged;
            _client.ShowEnded += OnShowEnded;

            // Il tempo di disegnare la prima slide (il Tappo copre ancora tutto), poi il focus torna alla regia.
            await Task.Delay(FirstSlideRenderMs, cancellationToken);
            ThrowIfStopped(cancellationToken);
            _output.EnsureTopmost();
            GiveFocusBackToRegia();

            PageChanged?.Invoke();
            Log.Information("Slideshow pronto: {Path} (slide {Slide}/{Total})", _path, _page.Value.Current, _page.Value.Total);
        }
        catch (PptException ex) when (ex.IsForeignInstance)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public bool Next() => Navigate(PptCommands.Next);

    public bool Previous() => Navigate(PptCommands.Previous);

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _audio?.SetVolume(_volume);
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _audio?.SetMuted(muted);
    }

    public Task FadeAudioOutAsync(TimeSpan duration) =>
        _closed || _audio is null ? Task.CompletedTask : _audio.FadeOutAsync(duration);

    public void Close()
    {
        if (_closed)
            return;

        _closed = true;

        // Silenzio subito; la sessione torna a 100% quando lo slideshow si è chiuso (EndShow è asincrono).
        _audio?.Release(AudioRestoreDelay);
        _audio = null;
        _client.Faulted -= OnClientFaulted;
        _client.SlideChanged -= OnSlideChanged;
        _client.ShowEnded -= OnShowEnded;

        _guard?.Dispose();
        _guard = null;
        _output.DetachShowWindow();

        // Non blocca: se PowerPoint si pianta ci pensa il watchdog.
        if (_sentOpen)
            _ = EndShowAsync();
    }

    private void StartAudioSession()
    {
        try
        {
            var pid = _client.PowerPointPid;
            if (pid <= 0)
            {
                Log.Warning("Audio PowerPoint non controllabile: PID del processo sconosciuto");
                return;
            }

            _audio = new ProcessAudioSession(pid);
            _audio.SetVolume(_volume);
            _audio.SetMuted(_muted);
        }
        catch (Exception ex)
        {
            // L'audio non deve mai far cadere lo slideshow.
            Log.Warning(ex, "Audio PowerPoint non controllabile");
        }
    }

    private bool Navigate(string command)
    {
        if (_closed || !_started)
            return false;

        _ = NavigateAsync(command);
        return true;
    }

    private async Task NavigateAsync(string command)
    {
        try
        {
            var response = await _client.SendAsync(command, null, _client.DefaultTimeout());
            if (_closed || PptProtocol.ReadData<NavigateResult>(response) is not { } result)
                return;

            if (result.AtEnd)
            {
                RaiseEnd();
            }
            else if (result.Moved && result.Slide > 0)
            {
                SetPage(result.Slide, result.Total);
            }
        }
        catch (PptException ex)
        {
            // Timeout o host caduto: ci pensa il watchdog (Faulted). Qui basta il log.
            Log.Warning("Comando {Command} non eseguito: {Message}", command, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nel comando {Command} a PowerPoint", command);
        }
    }

    private async Task EndShowAsync()
    {
        try
        {
            await _client.SendAsync(PptCommands.EndShow, null, _client.DefaultTimeout());
        }
        catch (Exception ex)
        {
            Log.Debug("Chiusura dello slideshow: {Message}", ex.Message);
        }
    }

    private void OnSlideChanged(int slide, int total) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_closed && _started)
                SetPage(slide, total);
        });

    private void OnShowEnded(ShowEndedData data) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;

            if (data.Faulted)
                Faulted?.Invoke(new InvalidOperationException("PowerPoint non è più utilizzabile: " + data.Reason));
            else
                RaiseEnd(); // fine per tempi automatici o collegamenti nel file
        });

    private void OnClientFaulted(string reason) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
                Faulted?.Invoke(new TimeoutException("PowerPoint/PptHost non rispondono: " + reason));
        });

    private void SetPage(int slide, int total)
    {
        var page = new PageInfo(slide, total);
        if (_page == page)
            return;

        _page = page;
        PageChanged?.Invoke();
    }

    private void RaiseEnd()
    {
        if (_closed || _endRaised)
            return;

        _endRaised = true;
        Log.Information("Fine dello slideshow: {Path}", _path);
        EndRequested?.Invoke();
    }

    private void ThrowIfStopped(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
            throw new OperationCanceledException("Slideshow chiuso durante il caricamento");
    }

    /// <summary>PowerPoint ha rubato il primo piano: la regia deve poter ricevere subito frecce e tasti (clicker incluso).</summary>
    private static void GiveFocusBackToRegia()
    {
        try
        {
            Application.Current?.MainWindow?.Activate();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Focus alla regia non restituito");
        }
    }
}
