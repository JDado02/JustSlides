using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Regia.Output.Interop;
using Serilog;

namespace Regia.Output.Capture;

/// <summary>
/// SOLO monitor reale: cattura a bassa frequenza (~5 fps) di ciò che è DAVVERO sullo schermo di output, per il pannello
/// Program: Tappo, slideshow di PowerPoint, video, tutto com'è composto da Windows (notifiche di altre app comprese).
/// In simulazione l'anteprima è lo specchio DWM, vedi <see cref="OutputMirrorService"/>. Solo informazione per l'operatore: gli errori si
/// loggano e il riquadro resta sull'ultimo fotogramma, mai un PANIC per colpa della cattura.
/// Il timer gira sul thread UI (legge la posizione delle finestre), la copia dei pixel in un thread del pool, e un solo
/// fotogramma alla volta: se Windows ci mette troppo il tick successivo viene saltato.
/// </summary>
public sealed class OutputCaptureService : IDisposable
{
    /// <summary>Larghezza del fotogramma catturato (l'altezza segue l'aspect dell'uscita).</summary>
    public const int FrameWidth = 480;

    private const int CaptureIntervalMs = 200;
    private const uint Srccopy = 0x00CC0020;
    private const uint Captureblt = 0x40000000;
    private const int Halftone = 4;

    /// <summary>Dopo tanti errori di fila si smette di scrivere nel log (uno ogni tanto), ma si continua a provare.</summary>
    private const int LogEveryNFailures = 50;

    private readonly OutputHost _output;
    private readonly DispatcherTimer _timer;
    private int _busy;
    private int _failures;

    public OutputCaptureService(OutputHost output, Dispatcher dispatcher)
    {
        _output = output;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(CaptureIntervalMs)
        };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Ultimo fotogramma (immagine già "frozen", utilizzabile dal thread UI). Null finché non ce n'è uno.</summary>
    public ImageSource? Frame { get; private set; }

    /// <summary>Un nuovo fotogramma è disponibile in <see cref="Frame"/> (sul thread UI).</summary>
    public event Action? FrameChanged;

    /// <summary>In pausa (regia ridotta a icona, output non mostrato): nessuna cattura, ultimo fotogramma tenuto.</summary>
    public bool Paused { get; set; }

    public void Start() => _timer.Start();

    private void Tick()
    {
        try
        {
            // In simulazione l'anteprima è lo specchio DWM (OutputMirrorService): la cattura dello schermo mostrerebbe
            // anche le finestre che coprono la cornice. Qui si lavora solo sul monitor reale.
            if (_output.Mode != OutputMode.Real)
            {
                if (Frame is not null)
                {
                    Frame = null;
                    FrameChanged?.Invoke();
                }

                return;
            }

            if (Paused || _output.CaptureRect is not { } rect || rect.Width < 16 || rect.Height < 16)
                return;

            // Un fotogramma alla volta: se il precedente non è ancora finito si salta questo giro.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return;

            var dispatcher = _timer.Dispatcher;
            _ = Task.Run(() =>
            {
                try
                {
                    var frame = Grab(rect);
                    _failures = 0;
                    dispatcher.BeginInvoke(() =>
                    {
                        Frame = frame;
                        FrameChanged?.Invoke();
                    });
                }
                catch (Exception ex)
                {
                    if (_failures++ % LogEveryNFailures == 0)
                        Log.Warning(ex, "Cattura dell'output non riuscita (la anteprima Program resta sull'ultimo fotogramma)");
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Errore nel timer di cattura dell'output");
        }
    }

    /// <summary>Copia l'area dallo schermo in un bitmap ridotto (StretchBlt HALFTONE, qualità ok per un'anteprima).</summary>
    private static BitmapSource Grab(PixelRect rect)
    {
        var width = FrameWidth;
        var height = Math.Max(1, (int)Math.Round(width * (double)rect.Height / rect.Width));

        var screen = GetDC(0);
        if (screen == 0)
            throw new InvalidOperationException("GetDC dello schermo non riuscito");

        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // negativo: righe dall'alto in basso
                Planes = 1,
                BitCount = 32
            };

            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0)
                throw new InvalidOperationException("CreateDIBSection non riuscito");

            previous = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, Halftone);
            SetBrushOrgEx(memory, 0, 0, 0);

            if (!StretchBlt(memory, 0, 0, width, height, screen, rect.X, rect.Y, rect.Width, rect.Height, Srccopy | Captureblt))
                throw new InvalidOperationException("StretchBlt non riuscito (errore Win32 " + Marshal.GetLastWin32Error() + ")");

            var stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            image.Freeze();
            return image;
        }
        finally
        {
            if (previous != 0)
                SelectObject(memory, previous);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (memory != 0)
                DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    public void Dispose() => _timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(nint dest, int xDest, int yDest, int wDest, int hDest, nint src, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);
}
