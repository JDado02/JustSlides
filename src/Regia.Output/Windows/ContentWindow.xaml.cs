using System.Windows;
using Regia.Output.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Windows;

/// <summary>Finestra contenuto (sotto il Tappo). In M1 mostra la schermata di prova.</summary>
public partial class ContentWindow : Window
{
    internal PixelPlacement Placement { get; }

    public ContentWindow()
    {
        InitializeComponent();
        Placement = new PixelPlacement(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.AddExStyle(this, WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Placement.ReapplyDeferred();
    }
}
