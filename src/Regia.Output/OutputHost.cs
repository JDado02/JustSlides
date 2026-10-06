using System.IO;
using Regia.Core.Monitors;
using Regia.Core.Settings;
using Regia.Output.Content;
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
        WindowPlacement.SetOwner(Tappo, _simulation);
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
        WindowPlacement.SetOwner(Tappo, null);
        _simulation.CloseForReal();
        _simulation = null;
    }

    private void EnsureHandles()
    {
        WindowPlacement.GetHandle(Content);
        WindowPlacement.GetHandle(Tappo);
    }

    private void ShowWindows()
    {
        if (_shown)
            return;

        Content.Show();
        Tappo.Show();
        _shown = true;
    }

    /// <summary>Riporta il Tappo sopra a tutto (es. dopo che PowerPoint si è portato in primo piano).</summary>
    public void EnsureTopmost()
    {
        if (!_shown)
            return;

        Tappo.Placement.Set(CurrentRect(), Mode == OutputMode.Real ? ZOrder.Topmost : ZOrder.NotTopmost);
    }

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
