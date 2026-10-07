using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Regia.Output.Preflight;

/// <summary>Codifica e lettura delle miniature della cache (JPEG, larghezza massima <see cref="Width"/>).</summary>
public static class Thumbnails
{
    /// <summary>Larghezza massima delle miniature salvate: bastano per la lista e per la Preview.</summary>
    public const int Width = 480;

    public static byte[] EncodeJpeg(BitmapSource source)
    {
        if (!source.IsFrozen && source is System.Windows.Freezable freezable && freezable.CanFreeze)
            freezable.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Ridimensiona (mai ingrandisce) a <see cref="Width"/> di larghezza al massimo.</summary>
    public static BitmapSource Fit(BitmapSource source)
    {
        if (source.PixelWidth <= Width)
            return source;

        var scale = (double)Width / source.PixelWidth;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>Carica un'immagine dalla cache senza tenere il file aperto (si può sostituire mentre è mostrata).</summary>
    public static BitmapImage? Load(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            if (decodeWidth > 0)
                image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
