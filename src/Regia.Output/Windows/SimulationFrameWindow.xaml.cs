using System.ComponentModel;
using System.Windows;
using Regia.Output.Interop;

namespace Regia.Output.Windows;

/// <summary>
/// Cornice ridimensionabile per la modalità simulazione: le finestre contenuto e Tappo
/// si posizionano sopra la sua area client (<see cref="Viewport"/>).
/// </summary>
public partial class SimulationFrameWindow : Window
{
    private bool _allowClose;

    public event Action? ViewportChanged;

    public SimulationFrameWindow()
    {
        InitializeComponent();
        LocationChanged += (_, _) => ViewportChanged?.Invoke();
        SizeChanged += (_, _) => ViewportChanged?.Invoke();
        StateChanged += (_, _) => ViewportChanged?.Invoke();
        Viewport.SizeChanged += (_, _) => ViewportChanged?.Invoke();
    }

    /// <summary>Area client in pixel fisici, null se la finestra è ridotta a icona.</summary>
    internal PixelRect? GetViewportRect()
    {
        if (WindowState == WindowState.Minimized || !IsLoaded || Viewport.ActualWidth < 1)
            return null;

        var topLeft = Viewport.PointToScreen(new Point(0, 0));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return new PixelRect(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(Viewport.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(Viewport.ActualHeight * dpi.DpiScaleY));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(() => ViewportChanged?.Invoke());
    }

    /// <summary>Chiudere la cornice distruggerebbe le finestre di output possedute: si riduce a icona.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
        }

        base.OnClosing(e);
    }

    internal void CloseForReal()
    {
        _allowClose = true;
        Close();
    }
}
