using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        set
        {
            CancelCrossfade();
            View.Source = value;
        }
    }

    private TaskCompletionSource? _crossfade;

    /// <summary>
    /// Passa a <paramref name="next"/> con una breve dissolvenza incrociata dentro il Tappo (slide del Tappo PowerPoint).
    /// Completa a fine animazione o se interrotta da <see cref="Frame"/> / <see cref="CancelCrossfade"/>.
    /// </summary>
    public Task CrossfadeToAsync(ImageSource next, TimeSpan duration)
    {
        CancelCrossfade();

        var done = _crossfade = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Incoming.Source = next;

        var animation = new DoubleAnimation(0, 1, duration) { EasingFunction = new SineEase() };
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_crossfade, done))
                return;

            // Fine: l'immagine in arrivo diventa quella base e quella sopra si spegne.
            View.Source = next;
            Incoming.BeginAnimation(OpacityProperty, null);
            Incoming.Opacity = 0;
            Incoming.Source = null;
            _crossfade = null;
            done.TrySetResult();
        };

        Incoming.BeginAnimation(OpacityProperty, animation);
        return done.Task;
    }

    /// <summary>Interrompe un crossfade in corso lasciando l'immagine base com'è.</summary>
    public void CancelCrossfade()
    {
        var pending = _crossfade;
        _crossfade = null;
        Incoming.BeginAnimation(OpacityProperty, null);
        Incoming.Opacity = 0;
        Incoming.Source = null;
        pending?.TrySetResult();
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
