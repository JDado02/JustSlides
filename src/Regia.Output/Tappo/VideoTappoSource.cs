using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Regia.Output.Windows;
using Serilog;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Regia.Output.Tappo;

/// <summary>
/// Tappo a video in loop. Niente VideoView (HWND figlio, incompatibile con la finestra
/// trasparente del Tappo): LibVLC scrive i fotogrammi in un buffer con le video callbacks
/// e noi li copiamo in un WriteableBitmap mostrato da un normale controllo Image.
/// Audio sempre disattivato.
/// </summary>
public sealed class VideoTappoSource : ITappoSource
{
    private const uint MaxWidth = 1920;
    private const uint MaxHeight = 1080;

    private readonly VlcService _vlc;
    private readonly string _path;
    private readonly object _bufferGate = new();

    // Le callback devono restare referenziate finché il player esiste.
    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    private TappoWindow? _window;
    private Media? _media;
    private MediaPlayer? _player;
    private IntPtr _buffer;
    private uint _width;
    private uint _height;
    private uint _pitch;
    private uint _lines;
    private WriteableBitmap? _bitmap;
    private int _renderPending;
    private volatile bool _disposed;

    public VideoTappoSource(VlcService vlc, string path)
    {
        _vlc = vlc;
        _path = path;
    }

    public Task AttachAsync(TappoWindow window)
    {
        _window = window;

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;

        _media = new Media(_vlc.Instance, new Uri(Path.GetFullPath(_path)));
        _media.AddOption(":input-repeat=65535");
        _media.AddOption(":no-audio");

        _player = new MediaPlayer(_media);
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
        _player.Play();

        Log.Information("Tappo video avviato: {Path}", _path);
        return Task.CompletedTask;
    }

    public void Freeze()
    {
        // L'ultimo fotogramma resta nel WriteableBitmap.
        if (_player is { IsPlaying: true })
            _player.SetPause(true);
    }

    public void Resume()
    {
        if (_player is not null && !_player.IsPlaying)
            _player.SetPause(false);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Limito la risoluzione di decodifica per tenere leggera la copia CPU.
        if (width > MaxWidth || height > MaxHeight)
        {
            var scale = Math.Min((double)MaxWidth / width, (double)MaxHeight / height);
            width = (uint)(width * scale) & ~1u;
            height = (uint)(height * scale) & ~1u;
        }

        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);

        _width = width;
        _height = height;
        _pitch = width * 4;
        _lines = height;
        pitches = _pitch;
        lines = _lines;

        lock (_bufferGate)
        {
            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)(_pitch * _lines));
        }

        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_bufferGate)
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
        Monitor.Enter(_bufferGate);
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        Monitor.Exit(_bufferGate);
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_disposed || _window is null)
            return;

        // Un solo rendering in coda alla volta: se la UI è indietro salto il fotogramma.
        if (Interlocked.Exchange(ref _renderPending, 1) == 0)
            _window.Dispatcher.BeginInvoke(Render, DispatcherPriority.Render);
    }

    private void Render()
    {
        Interlocked.Exchange(ref _renderPending, 0);

        if (_disposed || _window is null)
            return;

        lock (_bufferGate)
        {
            if (_buffer == IntPtr.Zero)
                return;

            if (_bitmap is null)
            {
                _bitmap = new WriteableBitmap((int)_width, (int)_height, 96, 96, PixelFormats.Bgr32, null);
                _window.Frame = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, (int)_width, (int)_height), _buffer, (int)(_pitch * _lines), (int)_pitch);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var player = _player;
        var media = _media;
        _player = null;
        _media = null;

        // Stop/Dispose di LibVLC vanno fuori dal thread UI per non bloccare la regia.
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
                Log.Warning(ex, "Errore nella chiusura del Tappo video");
            }
        });
    }
}
