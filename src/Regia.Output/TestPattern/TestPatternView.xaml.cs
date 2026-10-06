using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Regia.Output.TestPattern;

/// <summary>Schermata di prova: barre colore, cerchio, risoluzione e orologio (mostra che il contenuto "vive").</summary>
public partial class TestPatternView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public TestPatternView()
    {
        InitializeComponent();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => UpdateTexts();
    }

    private void UpdateTexts()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss.f");

        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        InfoText.Text = $"CONTENUTO DI PROVA  -  {w} x {h} px  -  {dpi.PixelsPerInchX:0} DPI";
    }
}
