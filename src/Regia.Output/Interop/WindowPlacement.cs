using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Interop;

/// <summary>Helper Win32 per posizionare le finestre di output in pixel fisici.</summary>
internal static unsafe class WindowPlacement
{
    private static readonly HWND HwndTop = new(0);
    private static readonly HWND HwndTopmost = new(-1);
    private static readonly HWND HwndNotTopmost = new(-2);

    public static HWND GetHandle(Window window) => new(new WindowInteropHelper(window).EnsureHandle());

    public static void AddExStyle(Window window, WINDOW_EX_STYLE add) => ChangeExStyle(window, add, 0);

    public static void RemoveExStyle(Window window, WINDOW_EX_STYLE remove) => ChangeExStyle(window, 0, remove);

    private static void ChangeExStyle(Window window, WINDOW_EX_STYLE add, WINDOW_EX_STYLE remove)
    {
        var hwnd = GetHandle(window);
        var current = (uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var updated = (current | (uint)add) & ~(uint)remove;
        if (updated != current)
            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)updated);
    }

    /// <summary>Imposta la finestra proprietaria a livello Win32 (funziona anche a finestra già mostrata).</summary>
    public static void SetOwner(Window window, Window? owner)
    {
        var hwnd = GetHandle(window);
        var ownerHandle = owner is null ? 0 : (nint)GetHandle(owner).Value;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_HWNDPARENT, ownerHandle);
    }

    public static void SetPosition(Window window, PixelRect rect, ZOrder zOrder)
    {
        var hwnd = GetHandle(window);
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        HWND insertAfter = default;

        switch (zOrder)
        {
            case ZOrder.Unchanged:
                flags |= SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
                break;
            case ZOrder.Top:
                insertAfter = HwndTop;
                break;
            case ZOrder.Topmost:
                insertAfter = HwndTopmost;
                break;
            case ZOrder.NotTopmost:
                insertAfter = HwndNotTopmost;
                break;
        }

        PInvoke.SetWindowPos(hwnd, insertAfter, rect.X, rect.Y, rect.Width, rect.Height, flags);
    }
}

internal enum ZOrder
{
    Unchanged,
    Top,
    Topmost,
    NotTopmost
}

/// <summary>
/// Tiene una finestra WPF sul rettangolo in pixel voluto. WPF, con monitor a DPI diversi,
/// può ridimensionare la finestra su WM_DPICHANGED: dopo ogni cambio riapplichiamo il rettangolo.
/// </summary>
internal sealed class PixelPlacement
{
    private readonly Window _window;
    private PixelRect _rect;
    private bool _hasRect;

    public PixelPlacement(Window window)
    {
        _window = window;
    }

    public void Set(PixelRect rect, ZOrder zOrder)
    {
        _rect = rect;
        _hasRect = true;
        WindowPlacement.SetPosition(_window, rect, zOrder);
    }

    /// <summary>Da chiamare da OnDpiChanged della finestra.</summary>
    public void ReapplyDeferred()
    {
        if (!_hasRect)
            return;

        _window.Dispatcher.BeginInvoke(
            () => WindowPlacement.SetPosition(_window, _rect, ZOrder.Unchanged),
            DispatcherPriority.Send);
    }
}
