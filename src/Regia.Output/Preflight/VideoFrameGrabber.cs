using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Regia.Output.Tappo;
using Serilog;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Regia.Output.Preflight;

/// <summary>
/// Cattura un fotogramma di un video con un player LibVLC senza finestra (video callbacks, come il Tappo video) e
/// senza audio. Serve per la miniatura e la Preview. Mai sul player della messa in onda.
/// </summary>
internal sealed class VideoFrameGrabber : IDisposable
{
    private const uint MaxWidth = 640;
    private const int TimeoutMs = 8000;

    private readonly VlcService _vlc;
    private readonly string _path;
    private readonly object _gate = new();

    // Le callback devono restare referenziate finché il player esiste.
    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    private Media? _media;
    private MediaPlayer? _player;
    private IntPtr _buffer;
    private uint _width;
    private uint _height;
    private uint _pitch;
    private int _frames;
    private bool _disposed;

    public VideoFrameGrabber(VlcService vlc, string path)
    {
        _vlc = vlc;
        _path = path;
    }

    /// <summary>Il fotogramma a circa il 10% del video (al massimo a 10 s); <c>null</c> se non si riesce entro il timeout.</summary>
    public async Task<BitmapSource?> GrabAsync(TimeSpan duration, CancellationToken token)
    {
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;

        _media = new Media(_vlc.Instance, new Uri(Path.GetFullPath(_path)));
        _media.AddOption(":no-audio");
        _media.AddOption(":no-sub-autodetect-file");

        _player = new MediaPlayer(_media);
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
        _player.Volume = 0;
        _player.Play();

        var deadline = Environment.TickCount64 + TimeoutMs;

        if (!await WaitFramesAsync(1, deadline, token))
            return null;

        // Un fotogramma "dentro" il video è più significativo del primo (spesso nero).
        var target = TimeSpan.FromMilliseconds(Math.Min(duration.TotalMilliseconds * 0.1, 10_000));
        if (target > TimeSpan.FromSeconds(1))
        {
            var before = Volatile.Read(ref _frames);
            _player.Time = (long)target.TotalMilliseconds;
            await WaitFramesAsync(before + 3, deadline, token);
        }

        return Snapshot();
    }

    private async Task<bool> WaitFramesAsync(int count, long deadline, CancellationToken token)
    {
        while (Volatile.Read(ref _frames) < count)
        {
            token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
                return Volatile.Read(ref _frames) > 0;

            await Task.Delay(40, token);
        }

        return true;
    }

    private BitmapSource? Snapshot()
    {
        lock (_gate)
        {
            if (_buffer == IntPtr.Zero || _width == 0 || _height == 0)
                return null;

            var bitmap = BitmapSource.Create((int)_width, (int)_height, 96, 96, PixelFormats.Bgr32, null,
                _buffer, (int)(_pitch * _height), (int)_pitch);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        if (width > MaxWidth)
        {
            var scale = (double)MaxWidth / width;
            width = (uint)(width * scale) & ~1u;
            height = (uint)(height * scale) & ~1u;
        }

        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);

        _width = width;
        _height = height;
        _pitch = width * 4;
        pitches = _pitch;
        lines = height;

        lock (_gate)
        {
            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)(_pitch * height));
        }

        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_gate)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_gate);
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(_gate);

    private void OnDisplay(IntPtr opaque, IntPtr picture) => Interlocked.Increment(ref _frames);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var player = _player;
        var media = _media;
        _player = null;
        _media = null;

        // Stop/Dispose di LibVLC vanno fuori dal thread chiamante per non bloccarlo.
        Task.Run(() =>
        {
            try
            {
                player?.Stop();
                player?.Dispose();
                media?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Errore nella chiusura del player delle miniature");
            }
        });
    }
}
