using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Regia.Core.Ppt;
using Serilog;

namespace Regia.PptHost;

/// <summary>
/// Pilota PowerPoint via COM in late binding (<c>dynamic</c>, nessuna dipendenza dalla versione di Office).
/// Va usato SOLO dal thread STA dedicato; ogni chiamata passa dal suo Dispatcher. Gli eventi dello slideshow
/// si ricavano da un polling a 250 ms su quel thread (slide corrente, finestra dello slideshow ancora viva,
/// processo POWERPNT ancora vivo).
/// </summary>
internal sealed class PowerPointDriver
{
    private const int PollIntervalMs = 250;
    private const int PlacementChecks = 16;           // ~4 s di verifica della posizione dopo il lancio
    private const int MaxConsecutivePollFailures = 3;

    private readonly BusyTracker _busy;
    private readonly DispatcherTimer _poll;

    private dynamic? _app;
    private dynamic? _presentation;
    private dynamic? _showWindow;
    private int _powerPointPid;

    private StartShowArgs? _target;
    private IntPtr _showHwnd;
    private int _placementChecksLeft;
    private bool _showActive;
    private int _lastPosition;
    private int _total;
    private int _pollFailures;

    private string? _registryPath;
    private bool _registryTouched;
    private object? _savedMonitorValue;
    private RegistryValueKind _savedMonitorKind;

    public PowerPointDriver(BusyTracker busy)
    {
        _busy = busy;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _poll.Tick += (_, _) => PollTick();
    }

    public event Action<int, int>? SlideChanged;

    /// <summary>Lo slideshow è finito (true = PowerPoint morto o non risponde, non una fine normale).</summary>
    public event Action<bool, string?>? ShowEnded;

    public bool IsLaunched => _app is not null;

    /// <summary>Avvia PowerPoint (senza aprire file). Se ce n'è già uno dell'utente, rifiuta: non ci si aggancia a istanze non nostre.</summary>
    public int Launch()
    {
        if (_app is not null)
        {
            if (IsPowerPointAlive())
                return _powerPointPid;

            Log.Warning("PowerPoint non risponde più (pid {Pid}): si riparte da zero", _powerPointPid);
            DiscardState();
        }

        var existing = Process.GetProcessesByName("POWERPNT");
        var foreign = existing.Length;
        foreach (var process in existing)
            process.Dispose();

        if (foreign > 0)
        {
            Log.Warning("PowerPoint è già aperto dall'utente ({Count} processi): la regia non si aggancia", foreign);
            throw new PptHostException(PptErrors.ForeignInstance, "PowerPoint è già aperto: chiuderlo prima di mandare in onda una presentazione.");
        }

        var type = Type.GetTypeFromProgID("PowerPoint.Application")
                   ?? throw new PptHostException(PptErrors.NotInstalled, "PowerPoint non risulta installato.");

        _app = Activator.CreateInstance(type)
               ?? throw new PptHostException(PptErrors.Generic, "Impossibile avviare PowerPoint.");

        // Nessun avviso e nessuna macro: la regia non deve mai trovarsi davanti a una finestra di dialogo.
        TrySet(() => _app!.DisplayAlerts = 1);          // ppAlertsNone
        TrySet(() => _app!.AutomationSecurity = 3);     // msoAutomationSecurityForceDisable

        _powerPointPid = ResolvePid();
        Log.Information("PowerPoint avviato (pid {Pid}, versione {Version})", _powerPointPid, TryGet(() => (string)_app!.Version));
        return _powerPointPid;
    }

    public OpenResult Open(string path)
    {
        Launch();

        if (!File.Exists(path))
            throw new PptHostException(PptErrors.FileNotFound, $"File non trovato: {path}");

        EndShow();

        var others = TryGet(() => (int)_app!.Presentations.Count);
        if (others > 0)
            Log.Warning("PowerPoint ha già {Count} presentazioni aperte prima di aprire {Path}", others, path);

        TrySet(() => _app!.DisplayAlerts = 1);  // ppAlertsNone, di nuovo: PowerPoint può averlo azzerato

        // ReadOnly, senza finestra di documento.
        _presentation = _app!.Presentations.Open(path, -1, 0, 0);
        MarkClean();

        int slides = _presentation.Slides.Count;
        double width = _presentation.PageSetup.SlideWidth;
        double height = _presentation.PageSetup.SlideHeight;
        Log.Information("Presentazione aperta: {Path} ({Slides} slide, {W}x{H})", path, slides, width, height);
        return new OpenResult(slides, width, height);
    }

    public StartShowResult StartShow(StartShowArgs args)
    {
        if (_presentation is null)
            throw new PptHostException(PptErrors.NoPresentation, "Nessuna presentazione aperta.");

        TrySet(() => _app!.DisplayAlerts = 1);
        MarkClean();

        var settings = _presentation.SlideShowSettings;
        settings.ShowType = args.Windowed ? 2 : 1;   // ppShowTypeWindow (solo simulazione) / ppShowTypeSpeaker. MAI Kiosk.
        settings.ShowPresenterView = 0;
        settings.LoopUntilStopped = 0;
        if (args.Windowed)
            TrySet(() => settings.ShowScrollbar = 0); // solo simulazione: niente barra di scorrimento nella finestra

        if (!args.Windowed)
            SetDisplayMonitor(args.GdiDeviceName);

        try
        {
            _showWindow = settings.Run();
            _showHwnd = FindShowWindow();
            _target = args;
            _total = _presentation.Slides.Count;

            if (_showHwnd == IntPtr.Zero)
                throw new PptHostException(PptErrors.NoShow, "Lo slideshow è partito ma la sua finestra non è stata trovata.");

            Place();
            _placementChecksLeft = PlacementChecks;

            _lastPosition = ReadPosition();
            _showActive = true;
            _pollFailures = 0;
            _poll.Start();
        }
        catch
        {
            // Mai lasciare uno slideshow a metà davanti al pubblico: se l'avvio non è andato a buon fine si chiude tutto.
            EndShow();
            throw;
        }

        Log.Information("Slideshow avviato (hwnd 0x{Hwnd:X}, {Mode}, rettangolo {X},{Y} {W}x{H})",
            _showHwnd.ToInt64(), args.Windowed ? "finestra" : "schermo intero", args.X, args.Y, args.Width, args.Height);
        return new StartShowResult(_lastPosition, _total, _showHwnd.ToInt64());
    }

    /// <summary>
    /// Finestra dello slideshow: <c>SlideShowWindow.HWND</c> restituisce vuoto con PowerPoint non visibile, quindi si cerca
    /// tra le finestre visibili del nostro POWERPNT. A schermo intero (relatore) è <c>screenClass</c>; in modalità finestra
    /// (solo simulazione) PowerPoint usa il frame principale <c>PPTFrameClass</c>. La finestra compare qualche istante dopo <c>Run()</c>.
    /// </summary>
    private IntPtr FindShowWindow()
    {
        var seen = new List<string>();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            IntPtr screen = IntPtr.Zero, frame = IntPtr.Zero;
            seen.Clear();

            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd))
                    return true;

                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != (uint)_powerPointPid)
                    return true;

                var name = new System.Text.StringBuilder(64);
                Native.GetClassName(hwnd, name, name.Capacity);
                var className = name.ToString();
                seen.Add(className);

                if (className == "screenClass")
                    screen = hwnd;
                else if (className == "PPTFrameClass")
                    frame = hwnd;

                return true;
            }, IntPtr.Zero);

            if (screen != IntPtr.Zero)
                return screen;
            if (frame != IntPtr.Zero)
                return frame;

            Thread.Sleep(50);
        }

        Log.Warning("Finestra dello slideshow non trovata; finestre visibili di PowerPoint: {Classes}", string.Join(", ", seen));
        return IntPtr.Zero;
    }

    public NavigateResult Next()
    {
        var view = RequireView();

        int position = view.CurrentShowPosition;
        SlideMove move = SlideNavigator.DecideNext(position, _total, ClicksRemaining((object)view));
        if (move == SlideMove.AtEnd)
        {
            // Non si chiama Next: PowerPoint mostrerebbe la schermata nera "Fine della presentazione".
            Log.Information("Avanti sull'ultima slide ({Position}/{Total}): fine, nessun comando a PowerPoint", position, _total);
            return new NavigateResult(false, true, position, _total);
        }

        view.Next();
        return Moved((object)view);
    }

    public NavigateResult Previous()
    {
        var view = RequireView();

        int position = view.CurrentShowPosition;
        if (SlideNavigator.DecidePrevious(position) == SlideMove.AtStart)
        {
            Log.Information("Indietro sulla prima slide: ignorato");
            return new NavigateResult(false, false, position, _total);
        }

        view.Previous();
        return Moved((object)view);
    }

    public NavigateResult GoTo(int slide)
    {
        var view = RequireView();
        slide = Math.Clamp(slide, 1, Math.Max(_total, 1));
        view.GotoSlide(slide);
        return Moved((object)view);
    }

    /// <summary>Chiude slideshow e presentazione (PowerPoint resta avviato per il file successivo). Idempotente.</summary>
    public void EndShow()
    {
        _poll.Stop();
        _showActive = false;
        _target = null;

        // Prima "pulita", poi chiusa: se PowerPoint la trova modificata alla chiusura chiede "vuoi salvare?", e quel dialogo
        // modale blocca ogni chiamata COM (e il watchdog finisce per terminare tutto). Chiudere la presentazione termina anche lo show.
        MarkClean();
        if (_presentation is not null)
        {
            TrySet(() => _presentation!.Close());
            Release((object?)_presentation);
            _presentation = null;
        }

        if (_showWindow is not null)
        {
            TrySet(() => _showWindow!.View.Exit());   // di norma già chiuso insieme alla presentazione
            Release((object?)_showWindow);
            _showWindow = null;
        }

        RestoreDisplayMonitor();
    }

    /// <summary>Chiude tutto e termina PowerPoint (solo quello avviato da noi).</summary>
    public void Quit()
    {
        EndShow();

        if (_app is null)
            return;

        TrySet(() => _app!.Quit());
        Release((object?)_app);
        _app = null;
        Log.Information("PowerPoint chiuso (pid {Pid})", _powerPointPid);
    }

    private NavigateResult Moved(object viewObject)
    {
        dynamic view = viewObject;
        int position = view.CurrentShowPosition;
        return new NavigateResult(true, false, position, _total);
    }

    private dynamic RequireView()
    {
        if (_showWindow is null || !_showActive)
            throw new PptHostException(PptErrors.NoShow, "Nessuno slideshow in corso.");

        return _showWindow.View;
    }

    private int ReadPosition()
    {
        try
        {
            return (int)_showWindow!.View.CurrentShowPosition;
        }
        catch (COMException)
        {
            return 0;
        }
    }

    /// <summary>Click di animazione ancora da fare sulla slide corrente; 0 se PowerPoint non lo sa dire.</summary>
    private static int ClicksRemaining(object viewObject)
    {
        dynamic view = viewObject;
        try
        {
            int count = view.GetClickCount();
            int index = view.GetClickIndex();
            return Math.Max(count - index, 0);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return 0;
        }
    }

    private void PollTick()
    {
        if (!_showActive || _app is null)
            return;

        _busy.Begin("poll");
        try
        {
            if (!IsPowerPointAlive())
            {
                FinishShow(faulted: true, "il processo PowerPoint è terminato");
                return;
            }

            MarkClean();
            WarnIfDialogOpen();

            int windows = _app.SlideShowWindows.Count;
            if (windows == 0)
            {
                FinishShow(faulted: false, null);
                return;
            }

            int position = ReadPosition();
            if (position > 0 && position != _lastPosition)
            {
                _lastPosition = position;
                SlideChanged?.Invoke(position, _total);
            }

            _pollFailures = 0;
            VerifyPlacement();
        }
        catch (Exception ex)
        {
            _pollFailures++;
            Log.Warning(ex, "Polling di PowerPoint fallito ({Count}/{Max})", _pollFailures, MaxConsecutivePollFailures);
            if (_pollFailures >= MaxConsecutivePollFailures)
                FinishShow(faulted: true, "PowerPoint non risponde alle chiamate COM");
        }
        finally
        {
            _busy.End();
        }
    }

    /// <summary>
    /// La presentazione è aperta in sola lettura e non va mai salvata: si segna "salvata" così alla chiusura (anche quella
    /// decisa da PowerPoint a fine slideshow) non compare la richiesta di salvataggio.
    /// </summary>
    private void MarkClean()
    {
        if (_presentation is null)
            return;

        try
        {
            _presentation.Saved = -1;
        }
        catch (COMException)
        {
            // Presentazione già chiusa da PowerPoint: niente da fare.
        }
    }

    private bool _dialogWarned;

    /// <summary>Un dialogo modale di PowerPoint blocca le chiamate COM: se compare lo si scrive nel log (una volta) per capirne il motivo.</summary>
    private void WarnIfDialogOpen()
    {
        string? title = null;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd))
                return true;

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)_powerPointPid)
                return true;

            var cls = new System.Text.StringBuilder(32);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "#32770")
                return true;

            var text = new System.Text.StringBuilder(128);
            Native.GetWindowText(hwnd, text, text.Capacity);
            title = text.ToString();
            return false;
        }, IntPtr.Zero);

        if (title is null)
        {
            _dialogWarned = false;
        }
        else if (!_dialogWarned)
        {
            _dialogWarned = true;
            Log.Warning("PowerPoint mostra un dialogo (\"{Title}\"): le chiamate COM resteranno bloccate finché non viene chiuso", title);
        }
    }

    private void FinishShow(bool faulted, string? reason)
    {
        _poll.Stop();
        _showActive = false;
        Log.Information("Slideshow terminato da PowerPoint (guasto: {Faulted}, {Reason})", faulted, reason);
        ShowEnded?.Invoke(faulted, reason);
    }

    // --- Posizionamento -------------------------------------------------------------------------------------------

    private void Place()
    {
        if (_target is not { } target || _showHwnd == IntPtr.Zero)
            return;

        // Solo posizione e dimensione: lo z-order di una finestra altrui Windows non lo fa cambiare da fuori (né HWND_TOP né
        // HWND_TOPMOST), quindi è la regia a mettersi sopra (Tappo topmost) o sotto (Contenuto, cornice) lo slideshow.
        var flags = Native.SwpNoActivate | Native.SwpShowWindow | Native.SwpNoZOrder;

        if (target.Windowed)
        {
            // Simulazione: finestra senza cornice sul rettangolo della cornice della regia.
            var style = Native.GetWindowLongPtr(_showHwnd, Native.GwlStyle);
            var bare = style & ~(nint)(Native.WsCaption | Native.WsThickFrame | Native.WsSysMenu);
            if (bare != style)
                Native.SetWindowLongPtr(_showHwnd, Native.GwlStyle, bare);

            flags |= Native.SwpFrameChanged;
        }

        Native.SetWindowPos(_showHwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, flags);
    }

    /// <summary>PowerPoint può spostare la finestra dopo il lancio: per qualche secondo si verifica e si corregge.</summary>
    private void VerifyPlacement()
    {
        if (_placementChecksLeft <= 0 || _target is not { } target)
            return;

        _placementChecksLeft--;
        if (!Native.GetWindowRect(_showHwnd, out var rect))
            return;

        const int tolerance = 2;
        var ok = Math.Abs(rect.Left - target.X) <= tolerance
                 && Math.Abs(rect.Top - target.Y) <= tolerance
                 && Math.Abs(rect.Right - rect.Left - target.Width) <= tolerance
                 && Math.Abs(rect.Bottom - rect.Top - target.Height) <= tolerance;

        if (!ok)
        {
            Log.Warning("Slideshow fuori posizione ({L},{T} {W}x{H}), si corregge", rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top);
            Place();
        }
    }

    // --- Chiave DisplayMonitor ------------------------------------------------------------------------------------

    /// <summary>Scelta del monitor dello slideshow nel registro di PowerPoint (best effort: la posizione vera la fa SetWindowPos).</summary>
    private void SetDisplayMonitor(string? gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName))
            return;

        try
        {
            var version = TryGet(() => (string)_app!.Version) ?? "16.0";
            _registryPath = $@"Software\Microsoft\Office\{version}\PowerPoint\Options";

            using var key = Registry.CurrentUser.CreateSubKey(_registryPath);
            if (!_registryTouched)
            {
                _savedMonitorValue = key.GetValue("DisplayMonitor");
                _savedMonitorKind = _savedMonitorValue is null ? RegistryValueKind.String : key.GetValueKind("DisplayMonitor");
            }

            key.SetValue("DisplayMonitor", gdiDeviceName, RegistryValueKind.String);
            _registryTouched = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile scrivere la chiave DisplayMonitor di PowerPoint");
        }
    }

    private void RestoreDisplayMonitor()
    {
        if (!_registryTouched || _registryPath is null)
            return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true);
            if (key is not null)
            {
                if (_savedMonitorValue is null)
                    key.DeleteValue("DisplayMonitor", throwOnMissingValue: false);
                else
                    key.SetValue("DisplayMonitor", _savedMonitorValue, _savedMonitorKind);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile ripristinare la chiave DisplayMonitor di PowerPoint");
        }
        finally
        {
            _registryTouched = false;
        }
    }

    // --- Processo -------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Application.HWND</c> non è affidabile con PowerPoint non visibile. Prima del lancio non c'era nessun POWERPNT
    /// (altrimenti si rifiuta): quello che compare ora, unico, è il nostro. Il processo può impiegare qualche istante a comparire.
    /// </summary>
    private static int ResolvePid()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var processes = Process.GetProcessesByName("POWERPNT");
            try
            {
                if (processes.Length == 1)
                    return processes[0].Id;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }

            Thread.Sleep(100);
        }

        return 0;
    }

    private bool IsPowerPointAlive()
    {
        if (_powerPointPid == 0)
            return true; // pid ignoto: ci pensano i fallimenti COM

        try
        {
            using var process = Process.GetProcessById(_powerPointPid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void DiscardState()
    {
        _poll.Stop();
        _showActive = false;
        Release((object?)_showWindow);
        Release((object?)_presentation);
        Release((object?)_app);
        _showWindow = null;
        _presentation = null;
        _app = null;
        _powerPointPid = 0;
        RestoreDisplayMonitor();
    }

    // --- Utilità COM ----------------------------------------------------------------------------------------------

    private static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Rilascio COM fallito");
        }
    }

    /// <summary>Esegue un'azione COM che non deve mai fermare la regia: l'errore si logga e basta.</summary>
    private static void TrySet(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Chiamata COM non riuscita (ignorata)");
        }
    }

    private static T? TryGet<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Lettura COM non riuscita (ignorata)");
            return default;
        }
    }
}
