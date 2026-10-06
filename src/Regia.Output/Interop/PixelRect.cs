using Regia.Core.Monitors;

namespace Regia.Output.Interop;

/// <summary>Rettangolo in pixel fisici (non DIP).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect FromMonitor(MonitorInfo m) => new(m.X, m.Y, m.Width, m.Height);
}
