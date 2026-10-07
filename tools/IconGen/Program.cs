using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Disegna l'icona di JustSlides (tassello blu, slide bianca con una "J", puntino rosso "in onda" come il tally broadcast)
/// direttamente a ogni dimensione, così le piccole restano nitide, e scrive JustSlides.ico (PNG a 32 bit) più un'anteprima 512 px.
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        // Sotto i 256 px le immagini dell'ico sono bitmap classici (DIB 32 bit + maschera): è il formato che ogni parte di Windows legge;
        // solo la 256 è un PNG.
        var frames = Sizes.Select(size => (Size: size, Data: size >= 256 ? Encode(Render(size)) : EncodeDib(Render(size)))).ToList();
        WriteIco(Path.Combine(outDir, "JustSlides.ico"), frames);
        File.WriteAllBytes(Path.Combine(outDir, "JustSlides-512.png"), Encode(Render(512)));

        Console.WriteLine($"Scritti JustSlides.ico ({frames.Count} dimensioni, {frames.Sum(f => f.Data.Length)} byte) e JustSlides-512.png in {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Draw(dc, size);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Disegno su una griglia 256×256. Sotto i 32 px si toglie ciò che non si leggerebbe (slide dietro, ombra) e si ingrossa il resto.</summary>
    private static void Draw(DrawingContext dc, int size)
    {
        var tiny = size <= 24;
        var small = size <= 32;

        // Tassello: blu del Tappo, in diagonale.
        var tile = new LinearGradientBrush(Color.FromRgb(0x3F, 0x8A, 0xEC), Color.FromRgb(0x16, 0x3E, 0x7C), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(tile, null, new Rect(8, 8, 240, 240), 56, 56);

        if (!small)
        {
            // Riflesso sottile sul bordo alto, per un po' di profondità senza decorazioni.
            var rim = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), 2.5);
            dc.DrawRoundedRectangle(null, rim, new Rect(9.5, 9.5, 237, 237), 54.5, 54.5);
        }

        // Slide (16:9). Piccolissime: più grande e centrata.
        var card = tiny ? new Rect(26, 82, 204, 114) : small ? new Rect(30, 80, 196, 110) : new Rect(34, 92, 176, 99);
        var radius = tiny ? 16 : small ? 14 : 12;

        if (!small)
        {
            // Seconda slide dietro (le "slides" al plurale) e ombra morbida della prima.
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), null,
                new Rect(card.X + 16, card.Y - 16, card.Width, card.Height), radius, radius);

            for (var i = 3; i >= 1; i--)
            {
                var spread = i * 3;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x16, 0x00, 0x10, 0x30)), null,
                    new Rect(card.X - spread, card.Y - spread + 8, card.Width + spread * 2, card.Height + spread * 2), radius + spread, radius + spread);
            }
        }

        dc.DrawRoundedRectangle(Brushes.White, null, card, radius, radius);

        // "J": un tratto solo, terminali arrotondati.
        var blue = new SolidColorBrush(Color.FromRgb(0x17, 0x40, 0x80));
        var stroke = tiny ? 30 : small ? 26 : 15;
        var jx = card.X + card.Width * (tiny ? 0.46 : 0.50);
        var top = card.Y + card.Height * 0.20;
        var bottom = card.Y + card.Height * 0.80;
        var hook = new PathGeometry([
            new PathFigure(new Point(jx, top), [
                new LineSegment(new Point(jx, bottom - card.Height * 0.26), true),
                new BezierSegment(
                    new Point(jx, bottom - card.Height * 0.05),
                    new Point(jx - card.Width * 0.07, bottom),
                    new Point(jx - card.Width * 0.13, bottom), true),
                new BezierSegment(
                    new Point(jx - card.Width * 0.19, bottom),
                    new Point(jx - card.Width * 0.24, bottom - card.Height * 0.04),
                    new Point(jx - card.Width * 0.26, bottom - card.Height * 0.12), true)
            ], false)
        ]);
        dc.DrawGeometry(null, new Pen(blue, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, hook);

        // Puntino rosso "in onda" (tally) in alto a destra della slide.
        var dotRadius = tiny ? 20 : small ? 17 : 11;
        var dotCentre = new Point(card.Right - card.Width * 0.16, card.Y + card.Height * 0.27);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x37, 0x2A)), null, dotCentre, dotRadius, dotRadius);
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Immagine di un'icona come DIB: intestazione BITMAPINFOHEADER (altezza doppia), pixel BGRA dal basso verso l'alto, maschera AND vuota.</summary>
    private static byte[] EncodeDib(BitmapSource bitmap)
    {
        var size = bitmap.PixelWidth;
        var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        straight.CopyPixels(pixels, stride, 0);

        var maskStride = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);                       // biSize
        writer.Write(size);                     // biWidth
        writer.Write(size * 2);                 // biHeight: immagine + maschera
        writer.Write((short)1);                 // biPlanes
        writer.Write((short)32);                // biBitCount
        writer.Write(0);                        // biCompression = BI_RGB
        writer.Write(stride * size + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var y = size - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, stride);

        writer.Write(new byte[maskStride * size]);
        return stream.ToArray();
    }

    /// <summary>ICO: bitmap classici per le dimensioni piccole, PNG per la 256.</summary>
    private static void WriteIco(string path, List<(int Size, byte[] Data)> frames)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write((short)0);               // riservato
        writer.Write((short)1);               // tipo: icona
        writer.Write((short)frames.Count);

        var offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // colori in tavolozza
            writer.Write((byte)0);            // riservato
            writer.Write((short)1);           // piani
            writer.Write((short)32);          // bit per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in frames)
            writer.Write(data);
    }
}
