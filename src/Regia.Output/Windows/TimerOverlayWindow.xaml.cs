using System.Windows;
using System.Windows.Media;
using Regia.Core.Timer;
using Regia.Output.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Windows;

/// <summary>
/// Timer del relatore sull'output: finestra trasparente a pieno schermo, click-through e senza attivazione, posseduta dal
/// Tappo (Windows la tiene sopra di lui, quindi sopra anche allo slideshow di PowerPoint). La dissolvenza del Tappo non la
/// tocca: sparisce con <c>Hide()</c> prima di tornare al Tappo.
/// </summary>
public partial class TimerOverlayWindow : Window
{
    private static readonly Brush Normal = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));
    private static readonly Brush Over = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)));

    internal PixelPlacement Placement { get; }

    public TimerOverlayWindow()
    {
        InitializeComponent();
        Placement = new PixelPlacement(this);
        SizeChanged += (_, _) => ApplyScale();
    }

    /// <summary>Aggiorna testo, colore e angolo. Nessun lavoro se nulla è cambiato (chiamato più volte al secondo).</summary>
    public void SetState(string text, TimerLevel level, TimerCorner corner)
    {
        if (TimeText.Text != text)
            TimeText.Text = text;

        var brush = level switch
        {
            TimerLevel.Over => Over,
            TimerLevel.Warning => Warning,
            _ => Normal
        };
        if (!ReferenceEquals(TimeText.Foreground, brush))
            TimeText.Foreground = brush;

        var horizontal = corner is TimerCorner.TopLeft or TimerCorner.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var vertical = corner is TimerCorner.TopLeft or TimerCorner.TopRight ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        if (Pill.HorizontalAlignment != horizontal)
            Pill.HorizontalAlignment = horizontal;
        if (Pill.VerticalAlignment != vertical)
            Pill.VerticalAlignment = vertical;
    }

    // Dimensioni proporzionali all'altezza dell'output (in DIP): su qualsiasi risoluzione la pastiglia è alta circa il 7%.
    private void ApplyScale()
    {
        var height = ActualHeight;
        if (height < 50)
            return;

        TimeText.FontSize = height * 0.05;
        Pill.Margin = new Thickness(height * 0.03);
        Pill.Padding = new Thickness(height * 0.018, height * 0.005, height * 0.018, height * 0.005);
        Pill.CornerRadius = new CornerRadius(height * 0.012);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.AddExStyle(this,
            WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Placement.ReapplyDeferred();
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
