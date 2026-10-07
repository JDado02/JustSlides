using System.IO;
using System.Diagnostics;
using System.Windows.Threading;
using Regia.Core.Logging;
using Regia.Core.Settings;
using Serilog;

namespace Regia.PptHost;

internal static class Program
{
    /// <summary>Uso: JustSlides.PptHost.exe --pipe &lt;nome&gt; --parent &lt;pid della regia&gt;</summary>
    private static int Main(string[] args)
    {
        var pipe = ArgValue(args, "--pipe");
        var parent = ArgValue(args, "--parent");
        if (pipe is null)
            return 2;

        LogSetup.Configure(Path.Combine(SettingsStore.DefaultDirectory, "logs"), "PptHost");
        Log.Information("=== Avvio PptHost (pid {Pid}) ===", Environment.ProcessId);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Eccezione non gestita in PptHost");
            Log.CloseAndFlush();
        };

        // Se la regia sparisce, PptHost non deve restare in giro.
        if (int.TryParse(parent, out var parentPid))
            WatchParent(parentPid);

        Dispatcher? sta = null;
        using var ready = new ManualResetEventSlim();
        var staThread = new Thread(() =>
        {
            sta = Dispatcher.CurrentDispatcher;
            MessageFilter.Register();
            ready.Set();
            Dispatcher.Run();
            MessageFilter.Revoke();
        })
        {
            Name = "PowerPoint STA",
            IsBackground = true
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        ready.Wait();

        var busy = new BusyTracker();
        var exitCode = 0;
        try
        {
            var server = new HostServer(pipe, sta!, busy, () => new PowerPointDriver(busy));
            server.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PptHost terminato per un errore");
            exitCode = 1;
        }

        sta!.InvokeShutdown();
        Log.Information("=== Chiusura PptHost ===");
        Log.CloseAndFlush();
        return exitCode;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void WatchParent(int parentPid)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var process = Process.GetProcessById(parentPid);
                process.WaitForExit();
            }
            catch (ArgumentException)
            {
                // La regia è già sparita.
            }

            Log.Warning("La regia (pid {Pid}) è terminata: PptHost esce", parentPid);
            Log.CloseAndFlush();
            Environment.Exit(3);
        })
        {
            Name = "Parent watch",
            IsBackground = true
        };
        thread.Start();
    }
}
