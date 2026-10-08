using System.IO;
using Regia.Core.Monitors;
using Regia.Core.Settings;
using Regia.Output.Content;
using Regia.Output.Input;
using Regia.Output.Interop;
using Regia.Output.Tappo;
using Regia.Output.Transitions;
using Regia.Output.Windows;
using Serilog;

namespace Regia.Output;

public enum OutputMode
{
    Real,
    Simulation
}

/// <summary>
/// Possiede le due finestre di output (contenuto sotto, Tappo sopra), le posiziona sul monitor
/// scelto (pixel fisici) o nella finestra di simulazione, e gestisce la sorgente del Tappo.
/// </summary>
public sealed class OutputHost : IDisposable
{
    private readonly VlcService _vlc;
    private SimulationFrameWindow? _simulation;
    private ITappoSource? _tappoSource;
    private int _tappoLoadVersion;
    private bool _shown;
    private nint _showWindow;
    private bool _showAttached;

    public OutputHost(VlcService vlc)
    {
        _vlc = vlc;
        Content = new ContentWindow();
        Tappo = new TappoWindow();
        Fader = new TappoFader(Tappo, () => _tappoSource);
    }

    public ContentWindow Content { get; }

    public TappoWindow Tappo { get; }

    public TappoFader Fader { get; }

    public OutputMode Mode { get; private set; } = OutputMode.Simulation;

    /// <summary>Messaggio per l'operatore (es. monitor non trovato); null = tutto a posto.</summary>
    public string? Warning { get; private set; }

    public MonitorInfo? CurrentMonitor { get; private set; }

    /// <summary>Risoluzione a cui decodificare/renderizzare i contenuti: quella del monitor, 1080p in simulazione.</summary>
    public OutputSize OutputPixelSize =>
        Mode == OutputMode.Real && CurrentMonitor is { } monitor
            ? new OutputSize(monitor.Width, monitor.Height)
            : new OutputSize(1920, 1080);

    /// <summary>
    /// Applica le impostazioni: sceglie reale o simulazione, riposiziona le finestre e ricarica il Tappo.
    /// Non lancia mai eccezioni: in caso di problemi ripiega sulla simulazione e imposta Warning.
    /// </summary>
    public async Task ApplyAsync(AppSettings settings, IReadOnlyList<MonitorInfo> monitors)
    {
        try
        {
            ResolveTarget(settings, monitors, out var target, out var warning);
            Warning = warning;
            CurrentMonitor = target;

            if (target is not null)
                ShowOnMonitor(target);
            else
                ShowInSimulation();

            await LoadTappoAsync(settings.Tappo);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nell'applicazione delle impostazioni di output");
            Warning = "Errore nella configurazione dell'output: " + ex.Message;
        }
    }

    private static void ResolveTarget(AppSettings settings, IReadOnlyList<MonitorInfo> monitors, out MonitorInfo? target, out string? warning)
    {
        target = null;
        warning = null;

        if (settings.SimulationMode)
            return;

        if (settings.OutputMonitor is null)
        {
            warning = "Nessun monitor di output scelto: modalità simulazione. Sceglilo nelle Impostazioni.";
            return;
        }

        var found = MonitorMatcher.Find(settings.OutputMonitor, monitors);
        if (found is null)
        {
            warning = $"Monitor di output \"{settings.OutputMonitor.FriendlyName}\" non trovato: modalità simulazione.";
            return;
        }

        // Mai coprire il monitor della regia: l'operatore resterebbe senza controlli.
        if (found.IsPrimary)
        {
            warning = "Il monitor di output scelto è quello della regia: modalità simulazione.";
            return;
        }

        target = found;
    }

    private void ShowOnMonitor(MonitorInfo monitor)
    {
        Mode = OutputMode.Real;
        DetachSimulation();

        EnsureHandles();
        var rect = PixelRect.FromMonitor(monitor);

        // Contenuto prima, Tappo per ultimo: così il Tappo resta sopra.
        Content.Placement.Set(rect, ZOrder.Topmost);
        Tappo.Placement.Set(rect, ZOrder.Topmost);
        ShowWindows();

        // Dopo Show WPF può aver riadattato la finestra: riapplico il rettangolo.
        Content.Placement.Set(rect, ZOrder.Unchanged);
        Tappo.Placement.Set(rect, ZOrder.Topmost);

        Log.Information("Output su monitor {Name} ({W}x{H} @ {X},{Y}, {Dpi} DPI)",
            monitor.FriendlyName, monitor.Width, monitor.Height, monitor.X, monitor.Y, monitor.Dpi);
    }

    private void ShowInSimulation()
    {
        Mode = OutputMode.Simulation;

        if (_simulation is null)
        {
            _simulation = new SimulationFrameWindow();
            _simulation.ViewportChanged += LayoutSimulation;
            _simulation.Show();
            _simulation.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        EnsureHandles();
        WindowPlacement.SetOwner(Content, _simulation);
        ShowWindows();
        LayoutSimulation();

        Log.Information("Output in modalità simulazione");
    }

    private void LayoutSimulation()
    {
        if (_simulation is null || Mode != OutputMode.Simulation)
            return;

        var rect = _simulation.GetViewportRect();
        if (rect is null)
            return;

        // Né l'una né l'altra topmost; contenuto prima, Tappo dopo, così il Tappo sta sopra.
        Content.Placement.Set(rect.Value, ZOrder.NotTopmost);
        Tappo.Placement.Set(rect.Value, ZOrder.NotTopmost);
    }

    private void DetachSimulation()
    {
        if (_simulation is null)
            return;

        _simulation.ViewportChanged -= LayoutSimulation;
        WindowPlacement.SetOwner(Content, null);
        _simulation.CloseForReal();
        _simulation = null;
    }

    private void EnsureHandles()
    {
        WindowPlacement.GetHandle(Content);
        WindowPlacement.GetHandle(Tappo);

        // Il Tappo è posseduto dal contenuto: Windows tiene sempre una finestra posseduta SOPRA il suo
        // proprietario, comunque si clicchi o si attivi qualcosa. Senza questo, un clic sul video (col Tappo
        // trasparente e click-through) portava il contenuto sopra il Tappo: al ritorno al Tappo restava lo schermo nero.
        WindowPlacement.SetOwner(Tappo, Content);
    }

    /// <summary>
    /// Il monitor di output è sparito: Windows sposta da solo le finestre sul monitor della regia e le lascerebbe lì a
    /// coprirla. Si nascondono; <see cref="ApplyAsync"/> le rimostra e riposiziona quando l'output torna.
    /// </summary>
    public void Suspend()
    {
        if (!_shown)
            return;

        _shown = false;
        TryClose(() => Tappo.Hide());
        TryClose(() => Content.Hide());
        Log.Warning("Finestre di output nascoste: monitor di output non disponibile");
    }

    private void ShowWindows()
    {
        if (_shown)
            return;

        Content.Show();
        Tappo.Show();
        _shown = true;
    }

    /// <summary>
    /// Riporta il Tappo sopra a tutto (es. dopo che PowerPoint si è portato in primo piano). In simulazione le finestre
    /// non sono topmost: <c>NOTOPMOST</c> su una finestra già non-topmost non cambia l'ordine, serve <c>HWND_TOP</c>.
    /// </summary>
    public void EnsureTopmost()
    {
        if (!_shown)
            return;

        // Monitor reale: il Tappo è topmost, lo slideshow (altro processo) no e non si può alzare da fuori, quindi il Tappo gli
        // sta sopra per costruzione. Simulazione: nessuna finestra di output è topmost, il Tappo viene riportato in cima a ogni
        // cambio di finestra attiva e la finestra della regia resta sopra a tutte (vedi AttachShowWindow): mai coperta dal Tappo.
        Tappo.Placement.Set(CurrentRect(), Mode == OutputMode.Real ? ZOrder.Topmost : ZOrder.Top);

        // In simulazione la cornice (nera) non deve coprire lo slideshow: si mette dietro di lui (chiamata asincrona e mai
        // verso un PowerPoint che non risponde: un SetWindowPos verso una finestra bloccata potrebbe congelare la regia).
        if (_showWindow != 0 && Mode == OutputMode.Simulation && _simulation is not null && !WindowPlacement.IsHung(_showWindow))
            WindowPlacement.PlaceBehind(_simulation, _showWindow);
    }

    /// <summary>
    /// Uno slideshow PowerPoint (finestra di un altro processo, non topmost e non controllabile nello z-order) sta sotto il Tappo.
    /// La finestra Contenuto non può essere sopra di lui: si riduce a 1×1 e smette di essere topmost (il Tappo, suo "posseduto",
    /// resta topmost e a pieno schermo), così lo slideshow è visibile quando il Tappo sfuma.
    /// </summary>
    public void AttachShowWindow(long hwnd)
    {
        _showAttached = true;
        _showWindow = (nint)hwnd;
        Log.Information("Slideshow sotto il Tappo (finestra 0x{Hwnd:X})", hwnd);
        if (!_shown)
            return;

        // In simulazione la cornice possiede Contenuto (che possiede il Tappo) e spostare un proprietario muove tutto il gruppo:
        // per metterla dietro lo slideshow si scioglie il legame cornice→Contenuto finché lo slideshow c'è.
        if (Mode == OutputMode.Simulation)
        {
            WindowPlacement.SetOwner(Content, null);
            SetOperatorOnTop(true);
        }

        var rect = CurrentRect();
        Content.Placement.Set(new PixelRect(rect.X, rect.Y, 1, 1), ZOrder.NotTopmost);
        EnsureTopmost();
    }

    /// <summary>Lo slideshow è finito: la finestra Contenuto torna a pieno schermo e il Tappo sopra di lei.</summary>
    public void DetachShowWindow()
    {
        _showAttached = false;
        if (_showWindow == 0)
            return;

        _showWindow = 0;
        if (!_shown)
            return;

        var rect = CurrentRect();
        if (Mode == OutputMode.Real)
        {
            Content.Placement.Set(rect, ZOrder.Topmost);
            Tappo.Placement.Set(rect, ZOrder.Topmost);
        }
        else
        {
            // Si ripristina il legame cornice→Contenuto e la cornice rimette tutto a posto.
            SetOperatorOnTop(false);
            if (_simulation is not null)
                WindowPlacement.SetOwner(Content, _simulation);
            LayoutSimulation();
        }
    }

    /// <summary>
    /// In simulazione, durante uno slideshow, la finestra della regia sta sopra a tutte quelle di output (cornice, Tappo,
    /// slideshow): senza, il Tappo opaco coprirebbe i comandi e non ci sarebbe modo di togliere.
    /// </summary>
    private static void SetOperatorOnTop(bool onTop)
    {
        try
        {
            if (System.Windows.Application.Current?.MainWindow is { } main)
                main.Topmost = onTop;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Finestra della regia: impossibile cambiare Topmost");
        }
    }

    /// <summary>
    /// Per l'hook di tastiera: la finestra in primo piano è una nostra finestra di output, o lo slideshow di PowerPoint
    /// (processo <paramref name="powerPointPid"/>, quello avviato da noi) mentre è sotto il Tappo?
    /// Chiamato a ogni tasto: nessun lavoro, solo confronti.
    /// </summary>
    public ForegroundKind ClassifyForeground(nint hwnd, uint pid, int powerPointPid)
    {
        if (!_shown)
            return ForegroundKind.Other;

        if (IsOutputWindow(hwnd))
            return ForegroundKind.OutputWindow;

        return _showAttached && powerPointPid > 0 && pid == (uint)powerPointPid ? ForegroundKind.SlideShow : ForegroundKind.Other;
    }

    /// <summary>
    /// Per lo specchio della simulazione: la finestra che sta SOTTO il Tappo (lo slideshow di PowerPoint, se c'è, altrimenti
    /// la finestra contenuto). Da chiamare sul thread UI.
    /// </summary>
    public nint MirrorBaseWindow => _showAttached && _showWindow != 0 ? _showWindow : HandleOf(Content);

    /// <summary>Per lo specchio della simulazione: la finestra del Tappo (sopra a tutto).</summary>
    public nint MirrorTopWindow => HandleOf(Tappo);

    /// <summary>Una delle nostre finestre di output (contenuto, Tappo, cornice di simulazione). Da chiamare sul thread UI.</summary>
    public bool IsOutputWindow(nint hwnd) =>
        hwnd != 0 && (hwnd == HandleOf(Content) || hwnd == HandleOf(Tappo) || (_simulation is { } frame && hwnd == HandleOf(frame)));

    private static nint HandleOf(System.Windows.Window window) => new System.Windows.Interop.WindowInteropHelper(window).Handle;

    /// <summary>
    /// Finché il valore restituito non viene eliminato, il Tappo viene riportato in cima a ogni cambio di finestra in primo
    /// piano. Da usare mentre uno slideshow di PowerPoint (altro processo) è sotto il Tappo.
    /// </summary>
    public IDisposable GuardTappoOnTop()
    {
        EnsureTopmost();
        return new ForegroundWatcher(EnsureTopmost, Tappo.Dispatcher);
    }

    /// <summary>
    /// Dove far partire lo slideshow PowerPoint: schermo intero sul monitor di output (con il nome GDI per la chiave
    /// DisplayMonitor), oppure finestra senza cornice nella cornice di simulazione.
    /// </summary>
    public (PixelRect Rect, string? GdiDeviceName, bool Windowed) GetShowPlacement()
    {
        if (Mode == OutputMode.Real && CurrentMonitor is { } monitor)
            return (PixelRect.FromMonitor(monitor), monitor.GdiDeviceName, false);

        return (CurrentRect(), null, true);
    }

    /// <summary>
    /// Area dello schermo che la cattura del Program deve riprendere: il monitor di output o, in simulazione, l'area utile
    /// della cornice. Null se le finestre di output non sono mostrate o la cornice è ridotta a icona (nulla da catturare).
    /// Da chiamare sul thread UI.
    /// </summary>
    public PixelRect? CaptureRect =>
        !_shown ? null
        : Mode == OutputMode.Real && CurrentMonitor is { } monitor ? PixelRect.FromMonitor(monitor)
        : Mode == OutputMode.Simulation ? _simulation?.GetViewportRect()
        : null;

    private PixelRect CurrentRect()
    {
        if (Mode == OutputMode.Real && CurrentMonitor is not null)
            return PixelRect.FromMonitor(CurrentMonitor);

        return _simulation?.GetViewportRect() ?? new PixelRect(0, 0, 640, 360);
    }

    /// <summary>Sostituisce la sorgente del Tappo. Se il file manca o non si apre, il Tappo resta nero.</summary>
    private async Task LoadTappoAsync(TappoSettings settings)
    {
        var version = ++_tappoLoadVersion;

        var old = _tappoSource;
        _tappoSource = null;
        old?.Dispose();
        Tappo.Frame = null;

        TappoSlides = null;

        if (settings.Kind == TappoKind.Slides)
        {
            await LoadSlidesTappoAsync(settings, version);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            Log.Information("Nessun file Tappo configurato: Tappo nero");
            return;
        }

        if (!File.Exists(settings.Path))
        {
            Warning = $"File Tappo non trovato: {settings.Path}";
            Log.Warning("File Tappo non trovato: {Path}", settings.Path);
            return;
        }

        ITappoSource? source = null;
        try
        {
            source = settings.Kind == TappoKind.Video
                ? new VideoTappoSource(_vlc, settings.Path)
                : new ImageTappoSource(settings.Path);

            await source.AttachAsync(Tappo);

            // Se nel frattempo è stato richiesto un altro Tappo, scarto questo.
            if (version != _tappoLoadVersion)
            {
                source.Dispose();
                return;
            }

            _tappoSource = source;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Impossibile caricare il Tappo {Path}", settings.Path);
            Warning = $"Impossibile aprire il Tappo ({Path.GetFileName(settings.Path)}): {ex.Message}";
            source?.Dispose();
            Tappo.Frame = null;
        }
    }

    /// <summary>Tappo PowerPoint (solo immagini) attivo, altrimenti null. Per le frecce a schermo della regia.</summary>
    public SlidesTappoSource? TappoSlides { get; private set; }

    /// <summary>Il Tappo PowerPoint è cambiato (slide mostrata o Tappo ricaricato).</summary>
    public event Action? TappoSlidesChanged;

    private async Task LoadSlidesTappoAsync(TappoSettings settings, int version)
    {
        var files = TappoSlidesCache.ListSlides(settings.SlidesDir);
        if (files.Count == 0)
        {
            Warning = "Slide del Tappo PowerPoint non trovate: apri le Impostazioni, scegli di nuovo il file e premi Applica.";
            Log.Warning("Slide del Tappo PowerPoint non trovate in {Dir}", settings.SlidesDir);
            TappoSlidesChanged?.Invoke();
            return;
        }

        SlidesTappoSource? source = null;
        try
        {
            source = new SlidesTappoSource(files, settings.SlidesMode, settings.SlideSeconds, settings.SlideIndex);
            await source.AttachAsync(Tappo);

            if (version != _tappoLoadVersion)
            {
                source.Dispose();
                return;
            }

            source.IndexChanged += () => TappoSlidesChanged?.Invoke();
            _tappoSource = source;
            TappoSlides = source;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Impossibile caricare il Tappo PowerPoint {Dir}", settings.SlidesDir);
            Warning = "Impossibile aprire il Tappo PowerPoint: " + ex.Message;
            source?.Dispose();
            Tappo.Frame = null;
        }

        TappoSlidesChanged?.Invoke();
    }

    public void IdentifyMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        IdentifyOverlay.ShowOnAll(monitors, TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _tappoSource?.Dispose();
        _tappoSource = null;

        // In chiusura WPF può aver già chiuso le finestre: ogni passo è indipendente e tollerante.
        var simulation = _simulation;
        _simulation = null;
        if (simulation is not null)
            simulation.ViewportChanged -= LayoutSimulation;

        TryClose(() => Tappo.Close());
        TryClose(() => Content.Close());
        TryClose(() => simulation?.CloseForReal());
    }

    private static void TryClose(Action close)
    {
        try
        {
            close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Finestra di output già chiusa");
        }
    }
}
