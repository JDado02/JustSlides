using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Interop;

/// <summary>Piccole funzioni di sistema usate dall'app (standby, istanza singola).</summary>
public static class SystemIntegration
{
    /// <summary>Impedisce standby e spegnimento schermo per tutta la vita del processo.</summary>
    public static void PreventSleep()
    {
        PInvoke.SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
    }

    /// <summary>Porta in primo piano la finestra con il titolo indicato (istanza già in esecuzione).</summary>
    public static bool BringWindowToFront(string title)
    {
        var hwnd = PInvoke.FindWindow(null, title);
        if (hwnd.IsNull)
            return false;

        if (PInvoke.IsIconic(hwnd))
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

        return PInvoke.SetForegroundWindow(hwnd);
    }
}
