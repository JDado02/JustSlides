using System.Runtime.InteropServices;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Content;

/// <summary>
/// Finestra figlia nera su cui LibVLC disegna il video (<c>MediaPlayer.Hwnd</c>). Al posto del VideoView di
/// LibVLCSharp.WPF, che passa da WindowsFormsHost e crea una finestra in più sull'output: qui c'è un solo HWND
/// figlio della finestra contenuto, quindi nessun rischio di z-order col Tappo.
/// </summary>
internal sealed unsafe class VideoHost : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        fixed (char* className = "static")
        {
            var handle = PInvoke.CreateWindowEx(
                0,
                className,
                null,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_CLIPCHILDREN | WINDOW_STYLE.WS_CLIPSIBLINGS,
                0, 0, 1, 1,
                new HWND(hwndParent.Handle),
                default,
                default,
                null);

            if (handle.IsNull)
                throw new InvalidOperationException("Impossibile creare la finestra video");

            return new HandleRef(this, handle);
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        PInvoke.DestroyWindow(new HWND(hwnd.Handle));
    }
}
