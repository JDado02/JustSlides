using System.Runtime.InteropServices;
using System.Windows.Threading;
using Regia.Core.Input;
using Serilog;

namespace Regia.Output.Input;

/// <summary>Che cos'è la finestra in primo piano, per decidere se l'hook deve intervenire.</summary>
public enum ForegroundKind
{
    /// <summary>Un'altra app, o la finestra della regia: l'hook non fa nulla (la regia ha i suoi tasti locali).</summary>
    Other,

    /// <summary>Una finestra di output nostra (Tappo, contenuto, cornice di simulazione).</summary>
    OutputWindow,

    /// <summary>Lo slideshow del PowerPoint avviato da noi.</summary>
    SlideShow
}

/// <summary>
/// Hook di tastiera globale (<c>WH_KEYBOARD_LL</c>) CONDIZIONATO: interviene solo se in primo piano c'è una finestra di
/// output o lo slideshow di PowerPoint. Serve al clicker del relatore e a chi clicca sullo slideshow: senza, PowerPoint
/// gestirebbe da solo le frecce (saltando <c>SlideNavigator</c> e mostrando la schermata nera finale).
/// Con qualsiasi altra app in primo piano non tocca nulla: i tasti nudi non vengono rubati al sistema.
/// Va creato sul thread UI (la callback arriva su quel thread). Nella callback si decide e basta: l'azione parte in
/// <c>BeginInvoke</c>, perché Windows stacca in silenzio gli hook lenti.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    /// <summary>Ogni quanto si reinstalla l'hook: Windows lo rimuove senza avvisare se una callback è troppo lenta.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private readonly HookProc _proc; // in un campo: il GC non deve raccoglierlo finché l'hook è attivo
    private readonly Dispatcher _dispatcher;
    private readonly Func<KeyMap> _map;
    private readonly Func<nint, uint, ForegroundKind> _classify;
    private readonly Action<KeyAction, bool> _perform;
    private readonly DispatcherTimer _refreshTimer;
    private readonly HashSet<int> _swallowed = [];
    private nint _hook;
    private bool _installFailureReported;
    private bool _installedOnce;

    /// <param name="map">Associazioni correnti (si rilegge a ogni tasto: i cambi dalle impostazioni valgono subito).</param>
    /// <param name="classify">Dato (hwnd, pid) della finestra in primo piano, dice se l'hook deve intervenire.</param>
    /// <param name="perform">Esegue l'azione sul thread UI; il secondo argomento è l'autorepeat.</param>
    public KeyboardHook(Dispatcher dispatcher, Func<KeyMap> map, Func<nint, uint, ForegroundKind> classify, Action<KeyAction, bool> perform)
    {
        _dispatcher = dispatcher;
        _map = map;
        _classify = classify;
        _perform = perform;
        _proc = Callback;

        Install();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    /// <summary>L'hook è installato. Se no (log + avviso) restano i tasti locali della regia.</summary>
    public bool IsInstalled => _hook != 0;

    /// <summary>Cambia quando l'hook non si riesce a installare (testo) o torna a funzionare (null).</summary>
    public event Action<string?>? WarningChanged;

    private void Install()
    {
        if (_hook != 0)
            return;

        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook != 0)
        {
            // Information solo la prima volta: la reinstallazione periodica non deve riempire il log.
            if (!_installedOnce)
            {
                _installedOnce = true;
                Log.Information("Hook di tastiera installato (solo con output o slideshow in primo piano)");
            }

            if (_installFailureReported)
            {
                _installFailureReported = false;
                WarningChanged?.Invoke(null);
            }

            return;
        }

        Log.Warning("Hook di tastiera non installato (errore Win32 {Error}): restano i tasti con la regia in primo piano", Marshal.GetLastWin32Error());
        if (!_installFailureReported)
        {
            _installFailureReported = true;
            WarningChanged?.Invoke("Tasti globali non attivi: il clicker funziona solo con la finestra della regia in primo piano.");
        }
    }

    /// <summary>Windows può togliere l'hook senza dirlo: lo si rimette (a meno che sia in corso la pressione di un tasto assorbito).</summary>
    private void Refresh()
    {
        try
        {
            if (_swallowed.Count == 0)
            {
                Uninstall();
                Install();
            }
            else if (_hook == 0)
            {
                Install();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Errore nel rinnovare l'hook di tastiera");
        }
    }

    private void Uninstall()
    {
        var hook = _hook;
        _hook = 0;
        if (hook != 0)
            UnhookWindowsHookEx(hook);
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode == HcAction && Handle((int)wParam, Marshal.PtrToStructure<KbdLlHookStruct>(lParam)))
                return 1;
        }
        catch (Exception ex)
        {
            // Mai un'eccezione verso Windows: il tasto passa com'è.
            Log.Warning(ex, "Errore nell'hook di tastiera");
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>True = il tasto è nostro e non deve arrivare a nessun altro.</summary>
    private bool Handle(int message, KbdLlHookStruct key)
    {
        var vk = (int)key.VkCode;

        if (message is WmKeyUp or WmSysKeyUp)
            return _swallowed.Remove(vk); // il rilascio di un tasto assorbito va assorbito anche lui

        if (message is not (WmKeyDown or WmSysKeyDown))
            return false;

        var repeat = _swallowed.Contains(vk);

        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        var kind = _classify(hwnd, pid);
        if (kind == ForegroundKind.Other)
            return false;

        var modifiers = CurrentModifiers();
        var map = _map();
        if (map.Find(new KeyChord(vk, modifiers)) is { } action)
        {
            _swallowed.Add(vk);
            Post(action, repeat);
            return true;
        }

        // Sullo slideshow PowerPoint non deve arrivare nessun tasto: frecce, B/W (schermo nero), numeri, Esc... andrebbero
        // a PowerPoint saltando SlideNavigator. Si lasciano passare Alt e Windows (Alt+Tab, Win+D...), Ctrl (Ctrl+Shift+Esc)
        // e i soli modificatori: l'operatore non deve restare intrappolato.
        if (kind == ForegroundKind.SlideShow &&
            !KeyChord.IsModifierKey(vk) &&
            (modifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            _swallowed.Add(vk);
            return true;
        }

        return false;
    }

    private void Post(KeyAction action, bool repeat)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _perform(action, repeat);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Errore nell'eseguire l'azione {Action} da tasto globale", action);
            }
        });
    }

    private static KeyModifiers CurrentModifiers()
    {
        var result = KeyModifiers.None;
        if (IsDown(VkControl))
            result |= KeyModifiers.Control;
        if (IsDown(VkMenu))
            result |= KeyModifiers.Alt;
        if (IsDown(VkShift))
            result |= KeyModifiers.Shift;
        if (IsDown(VkLWin) || IsDown(VkRWin))
            result |= KeyModifiers.Windows;
        return result;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        _refreshTimer.Stop();
        Uninstall();
        _swallowed.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
