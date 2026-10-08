using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Regia.Core.Settings;
using Regia.Output.Windows;
using Serilog;

namespace Regia.Output.Tappo;

/// <summary>
/// Tappo PowerPoint: le slide già esportate in PNG (vedi <c>TappoSlidesExporter</c>). Qui PowerPoint non c'è: solo immagini
/// nella finestra Tappo. Loop (scorrono da sole, con crossfade) oppure fermo su una slide cambiata solo da <see cref="StepAsync"/>.
/// Va usato dal thread UI.
/// </summary>
public sealed class SlidesTappoSource : ITappoSource
{
    private static readonly TimeSpan CrossfadeDuration = TimeSpan.FromMilliseconds(300);

    private readonly IReadOnlyList<string> _files;
    private readonly TappoSlidesMode _mode;
    private readonly DispatcherTimer _timer;
    private TappoWindow? _window;
    private int _index;          // 1-based
    private int _version;
    private bool _frozen;
    private bool _disposed;
    private readonly string _label;

    public SlidesTappoSource(IReadOnlyList<string> files, TappoSlidesMode mode, int secondsPerSlide, int startIndex, string label = "Tappo PowerPoint")
    {
        _label = label;
        _files = files;
        _mode = mode;
        _index = Math.Clamp(startIndex, 1, Math.Max(files.Count, 1));
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(secondsPerSlide, TappoSettings.MinSlideSeconds, TappoSettings.MaxSlideSeconds))
        };
        _timer.Tick += async (_, _) => await AdvanceAsync();
    }

    public int Count => _files.Count;

    /// <summary>Slide mostrata (1-based).</summary>
    public int Index => _index;

    public bool IsHold => _mode == TappoSlidesMode.Hold;

    /// <summary>La slide mostrata è cambiata (solo fermo: cambio manuale).</summary>
    public event Action? IndexChanged;

    public async Task AttachAsync(TappoWindow window)
    {
        _window = window;
        if (_files.Count == 0)
            throw new InvalidOperationException("Nessuna slide esportata per il Tappo.");

        var first = await LoadAsync(_index);
        if (_disposed)
            return;

        window.Frame = first;
        Log.Information("{Label} caricato: {Count} immagini, {Mode}, n. {Index}", _label, _files.Count, _mode, _index);

        if (_mode == TappoSlidesMode.Loop && _files.Count > 1)
            _timer.Start();
    }

    /// <summary>Solo fermo: sposta di <paramref name="delta"/> slide (ai bordi si ferma). Restituisce true se è cambiata.</summary>
    public async Task<bool> StepAsync(int delta)
    {
        if (_mode != TappoSlidesMode.Hold || _window is null || _disposed)
            return false;

        var target = TappoSlidesCache.Step(_index, delta, _files.Count);
        if (target == _index)
            return false;

        var version = ++_version;
        var bitmap = await LoadAsync(target);
        if (_disposed || version != _version)
            return false;

        _index = target;
        _window.Frame = bitmap;
        Log.Information("{Label}: {Index}/{Count}", _label, _index, _files.Count);
        IndexChanged?.Invoke();
        return true;
    }

    private async Task AdvanceAsync()
    {
        if (_frozen || _disposed || _window is null || _files.Count < 2)
            return;

        _timer.Stop();
        var version = ++_version;
        try
        {
            var target = _index % _files.Count + 1;
            var bitmap = await LoadAsync(target);
            if (_disposed || _frozen || version != _version)
                return;

            _index = target;
            await _window.CrossfadeToAsync(bitmap, CrossfadeDuration);
        }
        catch (Exception ex)
        {
            // Una slide illeggibile non deve fermare il Tappo: si salta e si prosegue.
            Log.Warning(ex, "{Label}: immagine {Index} non caricata", _label, _index);
        }
        finally
        {
            if (!_disposed && !_frozen && _mode == TappoSlidesMode.Loop)
                _timer.Start();
        }
    }

    private Task<BitmapImage> LoadAsync(int index)
    {
        var file = _files[Math.Clamp(index, 1, _files.Count) - 1];
        return Task.Run(() =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.GetFullPath(file));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        });
    }

    /// <summary>Durante la dissolvenza (e finché il Tappo è coperto) il loop sta fermo.</summary>
    public void Freeze()
    {
        _frozen = true;
        _timer.Stop();
        _version++;
    }

    public void Resume()
    {
        _frozen = false;
        if (_mode == TappoSlidesMode.Loop && _files.Count > 1 && !_disposed)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _version++;
        _window?.CancelCrossfade();
    }
}
