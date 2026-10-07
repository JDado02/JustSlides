namespace Regia.Core.Ppt;

public enum WatchdogVerdict
{
    Healthy,

    /// <summary>PptHost non risponde al Ping entro il timeout (processo bloccato o morto).</summary>
    PingTimeout,

    /// <summary>PptHost risponde, ma il suo thread STA è fermo nella stessa operazione COM da troppo tempo.</summary>
    BusyTooLong
}

/// <summary>
/// Politica del watchdog regia ↔ PptHost, pura e con orologio iniettabile. Durante una <c>Open</c> lunga il thread STA
/// è legittimamente occupato: il Ping risponde dal thread della pipe riportando l'operazione in corso e da quanto,
/// e il guasto scatta solo oltre il timeout di <i>quella</i> operazione.
/// </summary>
public sealed class WatchdogPolicy
{
    private readonly TimeSpan _pingTimeout;
    private readonly Func<string, TimeSpan> _operationTimeout;
    private readonly Func<TimeSpan> _clock;

    private TimeSpan _lastPong;
    private string? _busyOperation;
    private TimeSpan _busyFor;

    public WatchdogPolicy(TimeSpan pingTimeout, Func<string, TimeSpan> operationTimeout, Func<TimeSpan> clock)
    {
        _pingTimeout = pingTimeout;
        _operationTimeout = operationTimeout;
        _clock = clock;
        Reset();
    }

    /// <summary>Riparte da zero (host appena avviato).</summary>
    public void Reset()
    {
        _lastPong = _clock();
        _busyOperation = null;
        _busyFor = TimeSpan.Zero;
    }

    public void RecordPong(string? busyOperation, TimeSpan busyFor)
    {
        _lastPong = _clock();
        _busyOperation = busyOperation;
        _busyFor = busyFor;
    }

    public WatchdogVerdict Evaluate()
    {
        var sinceLastPong = _clock() - _lastPong;
        if (sinceLastPong > _pingTimeout)
            return WatchdogVerdict.PingTimeout;

        if (_busyOperation is not null && _busyFor + sinceLastPong > _operationTimeout(_busyOperation))
            return WatchdogVerdict.BusyTooLong;

        return WatchdogVerdict.Healthy;
    }
}
