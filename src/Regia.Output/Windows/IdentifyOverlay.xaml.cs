using System.Windows;
using System.Windows.Threading;
using Regia.Core.Monitors;
using Regia.Output.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Windows;

/// <summary>Numero grande + nome del monitor, mostrato per qualche secondo su ogni schermo.</summary>
public partial class IdentifyOverlay : Window
{
    private readonly PixelPlacement _placement;

    private IdentifyOverlay(int number, MonitorInfo monitor)
    {
        InitializeComponent();
        NumberText.Text = number.ToString();
        NameText.Text = monitor.FriendlyName + (monitor.IsPrimary ? " (regia)" : "");
        _placement = new PixelPlacement(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.AddExStyle(this, WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _placement.ReapplyDeferred();
    }

    /// <summary>Mostra un overlay al centro di ogni monitor e lo chiude dopo la durata indicata.</summary>
    public static void ShowOnAll(IReadOnlyList<MonitorInfo> monitors, TimeSpan duration)
    {
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var overlay = new IdentifyOverlay(i + 1, m);

            // Dimensione in pixel fisici proporzionale al monitor, centrata.
            var w = Math.Min(m.Width, (int)(600 * m.Dpi / 96.0));
            var h = Math.Min(m.Height, (int)(320 * m.Dpi / 96.0));
            var rect = new PixelRect(m.X + (m.Width - w) / 2, m.Y + (m.Height - h) / 2, w, h);

            overlay.WindowStartupLocation = WindowStartupLocation.Manual;
            WindowPlacement.GetHandle(overlay);
            overlay.Show();
            overlay._placement.Set(rect, ZOrder.Topmost);

            var timer = new DispatcherTimer { Interval = duration };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                overlay.Close();
            };
            timer.Start();
        }
    }
}
