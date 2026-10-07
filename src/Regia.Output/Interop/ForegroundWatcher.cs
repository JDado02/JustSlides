using System.Runtime.InteropServices;
using System.Windows.Threading;
using Serilog;

namespace Regia.Output.Interop;

/// <summary>
/// Richiama <c>onChange</c> ogni volta che un'altra finestra si porta in primo piano (EVENT_SYSTEM_FOREGROUND) e, per
/// sicurezza, una volta al secondo. Serve a riportare il Tappo sopra lo slideshow di PowerPoint, che è una finestra di un
/// altro processo e non si può "possedere": dopo ogni attivazione sua il Tappo va rimesso in cima.
/// Va creato e distrutto sul thread UI (la callback arriva su quel thread).
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    private readonly WinEventProc _callback; // tenuto in un campo: il GC non deve raccoglierlo finché l'hook è attivo
    private readonly Action _onChange;
    private readonly DispatcherTimer _timer;
    private IntPtr _hook;

    public ForegroundWatcher(Action onChange, Dispatcher dispatcher)
    {
        _onChange = onChange;
        _callback = OnForegroundChanged;

        // Anche le attivazioni della regia stessa: in simulazione alzano la catena di finestre sopra lo slideshow.
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
        if (_hook == IntPtr.Zero)
            Log.Warning("SetWinEventHook non riuscito: resta il controllo ogni secondo");

        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Safe();
        _timer.Start();
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime) =>
        Safe();

    private void Safe()
    {
        try
        {
            _onChange();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Errore nel riportare il Tappo in primo piano");
        }
    }

    public void Dispose()
    {
        _timer.Stop();

        var hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero)
            UnhookWinEvent(hook);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
