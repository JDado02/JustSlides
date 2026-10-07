using System.Diagnostics;

namespace Regia.PptHost;

/// <summary>
/// Ricorda quale operazione COM sta eseguendo il thread STA e da quanto. Il Ping, che risponde dal thread della pipe,
/// la riporta alla regia: così il watchdog distingue un'apertura lunga ma viva da un PowerPoint bloccato.
/// </summary>
internal sealed class BusyTracker
{
    private readonly object _lock = new();
    private string? _operation;
    private long _startTicks;

    public void Begin(string operation)
    {
        lock (_lock)
        {
            _operation = operation;
            _startTicks = Stopwatch.GetTimestamp();
        }
    }

    public void End()
    {
        lock (_lock)
            _operation = null;
    }

    public (string? Operation, long BusyMs) Snapshot()
    {
        lock (_lock)
        {
            return _operation is null
                ? (null, 0)
                : (_operation, (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds);
        }
    }
}

/// <summary>Errore atteso con un codice del protocollo (<see cref="Regia.Core.Ppt.PptErrors"/>).</summary>
internal sealed class PptHostException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
