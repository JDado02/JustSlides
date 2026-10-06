using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Regia.Core.Wave;
using Regia.Output.Windows;
using Serilog;

namespace Regia.Output.Content;

/// <summary>
/// Immagine JPG/PNG a tutto schermo con letterbox/pillarbox. Decodifica in background, già ridotta
/// alla risoluzione dell'output (una foto da 50 MP non deve restare in RAM) e con rotazione EXIF applicata.
/// </summary>
public sealed class ImagePresenter : IContentPresenter
{
    private readonly ContentWindow _window;
    private readonly string _path;
    private readonly OutputSize _size;
    private Image? _view;
    private bool _closed;

    public ImagePresenter(ContentWindow window, string path, OutputSize size)
    {
        _window = window;
        _path = path;
        _size = size;
    }

    public PageInfo? Page => null;

    public event Action? PageChanged
    {
        add { }
        remove { }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException("File non trovato", _path);

        var bitmap = await Task.Run(() => Decode(_path, _size), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Chiuso (PANIC) mentre decodificava: non si tocca la finestra.
        if (_closed)
            return;

        _view = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both
        };
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);

        _window.SetContent(_view);
        await RenderWait.NextFramesAsync(cancellationToken);

        Log.Information("Immagine pronta: {Path} ({W}x{H} decodificata)", _path, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    public bool Next() => false;

    public bool Previous() => false;

    public void Close()
    {
        if (_closed)
            return;

        _closed = true;
        if (_view is not null)
        {
            _window.ClearContent(_view);
            _view.Source = null;
        }
    }

    private static BitmapSource Decode(string path, OutputSize size)
    {
        var orientation = 1;
        int sourceWidth, sourceHeight;

        // Prima lettura: solo dimensioni e orientamento EXIF.
        using (var stream = File.OpenRead(path))
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            sourceWidth = frame.PixelWidth;
            sourceHeight = frame.PixelHeight;

            try
            {
                if (frame.Metadata is BitmapMetadata metadata
                    && metadata.ContainsQuery("System.Photo.Orientation")
                    && metadata.GetQuery("System.Photo.Orientation") is ushort value
                    && value is >= 1 and <= 8)
                {
                    orientation = value;
                }
            }
            catch (Exception ex)
            {
                // Metadati assenti o non leggibili (es. PNG): si assume orientamento normale.
                Log.Debug(ex, "Orientamento EXIF non leggibile: {Path}", path);
            }
        }

        // Dimensioni a video dopo la rotazione; si riduce solo se l'immagine è più grande dell'output.
        var swapped = orientation >= 5;
        var shownWidth = swapped ? sourceHeight : sourceWidth;
        var shownHeight = swapped ? sourceWidth : sourceHeight;
        var scale = Math.Min(1.0, Math.Min((double)size.Width / shownWidth, (double)size.Height / shownHeight));

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (scale < 1.0)
            image.DecodePixelWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        image.EndInit();
        image.Freeze();

        BitmapSource result = image;

        // Orientamenti EXIF: 2,4,5,7 hanno uno specchio, poi la rotazione.
        if (orientation is 2 or 5 or 7)
            result = Transform(result, new ScaleTransform(-1, 1));
        if (orientation == 4)
            result = Transform(result, new ScaleTransform(1, -1));

        var angle = orientation switch
        {
            3 => 180,
            5 => 270,
            6 => 90,
            7 => 90,
            8 => 270,
            _ => 0
        };
        if (angle != 0)
            result = Transform(result, new RotateTransform(angle));

        if (!result.IsFrozen)
            result.Freeze();

        return result;
    }

    private static BitmapSource Transform(BitmapSource source, System.Windows.Media.Transform transform)
    {
        var transformed = new TransformedBitmap(source, transform);
        transformed.Freeze();
        return transformed;
    }
}
