using System.Windows.Threading;
using Regia.Core.Layout;
using Regia.Output.Interop;
using Serilog;

namespace Regia.Output.Capture;

/// <summary>Dove disegnare lo specchio: la finestra di destinazione (la regia) e il riquadro, in pixel dell'area client.</summary>
public readonly record struct MirrorArea(nint DestinationWindow, PixelRect Rect);

/// <summary>
/// Anteprima dell'output in modalità SIMULAZIONE nel pannello Program: Windows compone dal vivo le vere finestre di output
/// (sotto lo slideshow di PowerPoint o la finestra contenuto, sopra il Tappo) dentro il riquadro, quindi si vede sempre
/// l'output anche se la cornice di simulazione è coperta da altre finestre (la regia stessa, altre applicazioni).
/// Sul monitor reale non serve: lì lavora <see cref="OutputCaptureService"/>, che mostra davvero ciò che arriva al monitor.
/// Solo informazione per l'operatore: qualunque errore si logga e la regia continua.
/// </summary>
public sealed class OutputMirrorService : IDisposable
{
    private const int IntervalMs = 200;

    private readonly OutputHost _output;
    private readonly Func<MirrorArea?> _area;
    private readonly DispatcherTimer _timer;
    private readonly DwmMirror _mirror = new();

    /// <param name="area">Riquadro del Program in pixel dell'area client della regia; null se non è visibile
    /// (regia ridotta a icona o riquadro nascosto). Chiamato sul thread UI.</param>
    public OutputMirrorService(OutputHost output, Dispatcher dispatcher, Func<MirrorArea?> area)
    {
        _output = output;
        _area = area;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(IntervalMs)
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();

    // Il timer serve a seguire layout, finestre sorgente e modalità: le miniature in sé sono aggiornate da Windows in
    // tempo reale, non a 5 fps.
    private void Tick()
    {
        try
        {
            if (_output.Mode != OutputMode.Simulation)
            {
                _mirror.Clear();
                return;
            }

            if (_area() is not { } area || area.Rect.Width < 16 || area.Rect.Height < 16 || _output.CaptureRect is not { } output)
            {
                _mirror.Hide();
                return;
            }

            // Le proporzioni sono quelle dell'uscita simulata: bande nere nel riquadro, mai deformazioni.
            var (x, y, width, height) = AspectFit.Fit(area.Rect.Width, area.Rect.Height, output.Width, output.Height);
            var rect = new PixelRect(area.Rect.X + x, area.Rect.Y + y, width, height);

            _mirror.Show(area.DestinationWindow, rect, [_output.MirrorBaseWindow, _output.MirrorTopWindow, _output.MirrorTimerWindow]);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Errore nello specchio dell'output (simulazione)");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _mirror.Dispose();
    }
}
