using Serilog;

namespace Regia.Core.Logging;

public static class LogSetup
{
    /// <summary>
    /// Configura Serilog: file a rotazione giornaliera, 30 giorni di storico. Il file è condiviso con PptHost
    /// (<c>shared: true</c>): <paramref name="processTag"/> (es. "PptHost") distingue le righe dei due processi.
    /// </summary>
    public static ILogger Configure(string logDirectory, string? processTag = null)
    {
        Directory.CreateDirectory(logDirectory);
        var tag = string.IsNullOrEmpty(processTag) ? "" : $" [{processTag}]";

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                System.IO.Path.Combine(logDirectory, "regia-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}]" + tag + " {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Logger = logger;
        return logger;
    }
}
