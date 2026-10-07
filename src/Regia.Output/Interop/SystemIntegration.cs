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

    /// <summary>
    /// Porta in primo piano la finestra principale di un'altra istanza già in esecuzione, cercando solo tra i processi con quel nome
    /// (mai per titolo: "JustSlides" è anche il nome di una cartella e di altre finestre).
    /// </summary>
    public static bool BringOtherInstanceToFront(string processName)
    {
        var self = Environment.ProcessId;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == self)
                    continue;

                var hwnd = new HWND(process.MainWindowHandle);
                if (hwnd.IsNull)
                    continue;

                if (PInvoke.IsIconic(hwnd))
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

                return PInvoke.SetForegroundWindow(hwnd);
            }
        }

        return false;
    }
}
