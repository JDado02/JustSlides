using System.Windows;
using System.Windows.Media;
using Regia.Output.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Windows;

/// <summary>
/// Finestra Tappo (sopra al contenuto). La transizione si fa solo animandone l'opacità;
/// a trasparenza completa diventa click-through (WS_EX_TRANSPARENT).
/// </summary>
public partial class TappoWindow : Window
{
    private bool _clickThrough;

    internal PixelPlacement Placement { get; }

    public TappoWindow()
    {
        InitializeComponent();
        Placement = new PixelPlacement(this);
    }

    /// <summary>Immagine mostrata (null = nero). Letterbox/pillarbox con Stretch=Uniform.</summary>
    public ImageSource? Frame
    {
        get => View.Source;
        set => View.Source = value;
    }

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value)
                return;

            _clickThrough = value;
            if (value)
                WindowPlacement.AddExStyle(this, WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
            else
                WindowPlacement.RemoveExStyle(this, WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
        }
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
