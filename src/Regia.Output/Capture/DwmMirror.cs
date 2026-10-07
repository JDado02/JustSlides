using System.Runtime.InteropServices;
using Regia.Output.Interop;
using Serilog;

namespace Regia.Output.Capture;

/// <summary>
/// Miniature DWM (<c>DwmRegisterThumbnail</c>) di finestre vive dentro un'altra finestra: Windows le compone da solo, in
/// tempo reale e sulla GPU, anche se la finestra sorgente è coperta da altre o fuori schermo. Servono alla modalità
/// simulazione: una cattura di una zona dello schermo mostrerebbe qualunque finestra ci stia sopra.
/// Le miniature sono disegnate sopra l'area client della finestra di destinazione, nell'ordine di registrazione
/// (l'ultima registrata sta sopra): prima la finestra di sotto, poi il Tappo.
/// </summary>
internal sealed class DwmMirror : IDisposable
{
    private const uint RectDestination = 0x1;
    private const uint Opacity = 0x4;
    private const uint Visible = 0x8;
    private const uint SourceClientAreaOnly = 0x10;

    private readonly List<(nint Source, nint Thumbnail)> _layers = [];
    private nint _destination;
    private bool _failureLogged;

    /// <summary>
    /// Mostra le <paramref name="sources"/> (dal basso verso l'alto) nel rettangolo <paramref name="rect"/> (pixel dell'area
    /// client di <paramref name="destination"/>). Se destinazione o sorgenti sono cambiate, le miniature si ricreano.
    /// </summary>
    public void Show(nint destination, PixelRect rect, IReadOnlyList<nint> sources)
    {
        var wanted = sources.Where(s => s != 0).ToList();
        if (destination != _destination || !wanted.SequenceEqual(_layers.Select(l => l.Source)))
            Rebuild(destination, wanted);

        foreach (var (_, thumbnail) in _layers)
            Update(thumbnail, rect, visible: true);
    }

    /// <summary>Nasconde le miniature (area non visibile, regia ridotta a icona) senza distruggerle.</summary>
    public void Hide()
    {
        foreach (var (_, thumbnail) in _layers)
            Update(thumbnail, default, visible: false);
    }

    /// <summary>Toglie tutte le miniature (si esce dalla simulazione).</summary>
    public void Clear()
    {
        foreach (var (_, thumbnail) in _layers)
            DwmUnregisterThumbnail(thumbnail);

        _layers.Clear();
        _destination = 0;
    }

    private void Rebuild(nint destination, List<nint> sources)
    {
        Clear();
        _destination = destination;

        foreach (var source in sources)
        {
            var hr = DwmRegisterThumbnail(destination, source, out var thumbnail);
            if (hr == 0 && thumbnail != 0)
            {
                _layers.Add((source, thumbnail));
            }
            else if (!_failureLogged)
            {
                // Es. finestra non ancora creata o già distrutta: si riprova al giro dopo, senza riempire il log.
                _failureLogged = true;
                Log.Debug("DwmRegisterThumbnail non riuscito per 0x{Source:X} (HRESULT 0x{Hr:X8})", source, hr);
            }
        }

        // Se una sorgente manca si ricostruisce al prossimo giro (l'elenco non coincide).
    }

    private static void Update(nint thumbnail, PixelRect rect, bool visible)
    {
        var properties = new ThumbnailProperties
        {
            Flags = RectDestination | Opacity | Visible | SourceClientAreaOnly,
            Destination = new NativeRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height),
            Opacity = 255,
            Visible = visible ? 1 : 0,
            SourceClientAreaOnly = 1
        };

        DwmUpdateThumbnailProperties(thumbnail, ref properties);
    }

    public void Dispose() => Clear();

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        public int Visible;
        public int SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
}
