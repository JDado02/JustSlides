using System.IO;
using System.Windows.Media.Imaging;
using Regia.Output.Windows;
using Serilog;

namespace Regia.Output.Tappo;

/// <summary>Tappo a immagine fissa, decodificata in background.</summary>
public sealed class ImageTappoSource : ITappoSource
{
    private readonly string _path;

    public ImageTappoSource(string path)
    {
        _path = path;
    }

    public async Task AttachAsync(TappoWindow window)
    {
        var bitmap = await Task.Run(() =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.GetFullPath(_path));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        });

        window.Frame = bitmap;
        Log.Information("Tappo immagine caricato: {Path} ({W}x{H})", _path, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    public void Freeze()
    {
    }

    public void Resume()
    {
    }

    public void Dispose()
    {
    }
}
