using Serilog;

namespace Regia.Core.Logging;

public static class LogSetup
{
    /// <summary>Configura Serilog: file a rotazione giornaliera, 30 giorni di storico.</summary>
    public static ILogger Configure(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                System.IO.Path.Combine(logDirectory, "regia-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Logger = logger;
        return logger;
    }
}
