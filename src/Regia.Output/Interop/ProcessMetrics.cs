using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Regia.Output.Interop;

/// <summary>Misure di risorse del processo della regia (stress test: servono a scoprire perdite di memoria, handle, oggetti GDI).</summary>
public sealed record ProcessSnapshot(double PrivateMemoryMb, int Handles, int Threads, int GdiObjects, int UserObjects, TimeSpan CpuTime);

public static class ProcessMetrics
{
    public static ProcessSnapshot Take()
    {
        using var process = Process.GetCurrentProcess();
        var handle = new HANDLE(process.Handle);

        return new ProcessSnapshot(
            process.PrivateMemorySize64 / (1024.0 * 1024.0),
            process.HandleCount,
            process.Threads.Count,
            (int)PInvoke.GetGuiResources(handle, GET_GUI_RESOURCES_FLAGS.GR_GDIOBJECTS),
            (int)PInvoke.GetGuiResources(handle, GET_GUI_RESOURCES_FLAGS.GR_USEROBJECTS),
            process.TotalProcessorTime);
    }

    /// <summary>Quanti dei nostri processi figli (PptHost, POWERPNT) sono vivi, dai PID registrati.</summary>
    public static int CountAlive(params int[] pids)
    {
        var alive = 0;
        foreach (var pid in pids.Where(p => p > 0))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    alive++;
            }
            catch (ArgumentException)
            {
                // Processo non più esistente.
            }
        }

        return alive;
    }
}
