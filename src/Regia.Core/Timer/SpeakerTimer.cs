namespace Regia.Core.Timer;

/// <summary>Come va colorato il timer sull'output.</summary>
public enum TimerLevel
{
    Normal,

    /// <summary>Manca meno di un minuto.</summary>
    Warning,

    /// <summary>Arrivato a zero o oltre (tempo negativo).</summary>
    Over
}

/// <summary>
/// Timer a scalare del relatore. Logica pura: l'orologio lo passa chiamante (come <c>WatchdogPolicy</c>), quindi si
/// testa senza aspettare. Dopo lo zero continua in negativo. <see cref="Tick"/> segnala una sola volta l'arrivo a zero;
/// se con ±1 minuto si torna sopra zero, alla discesa successiva lo segnala di nuovo.
/// </summary>
public sealed class SpeakerTimer
{
    public const int WarningSeconds = 60;

    private TimeSpan _remainingAtAnchor;
    private DateTime _anchor;
    private bool _running;
    private bool _active;
    private bool _zeroReported;

    /// <summary>Il timer è avviato (anche se in pausa o nascosto): ha un tempo da mostrare.</summary>
    public bool IsActive => _active;

    public bool IsPaused => _active && !_running;

    /// <summary>Durata con cui è partito (per "Riavvia").</summary>
    public int StartSeconds { get; private set; }

    public void Start(int seconds, DateTime now)
    {
        StartSeconds = Math.Max(0, seconds);
        _remainingAtAnchor = TimeSpan.FromSeconds(StartSeconds);
        _anchor = now;
        _running = true;
        _active = true;
        _zeroReported = false;
    }

    public void Restart(DateTime now) => Start(StartSeconds, now);

    public void Stop()
    {
        _active = false;
        _running = false;
        _zeroReported = false;
    }

    public void Pause(DateTime now)
    {
        if (!_active || !_running)
            return;

        _remainingAtAnchor = Remaining(now);
        _anchor = now;
        _running = false;
    }

    public void Resume(DateTime now)
    {
        if (!_active || _running)
            return;

        _anchor = now;
        _running = true;
    }

    /// <summary>Aggiunge (o toglie, se negativo) secondi al tempo che resta.</summary>
    public void AddSeconds(int seconds, DateTime now)
    {
        if (!_active)
            return;

        _remainingAtAnchor = Remaining(now) + TimeSpan.FromSeconds(seconds);
        _anchor = now;

        // Tornati sopra zero: lo zero successivo va segnalato di nuovo.
        if (_remainingAtAnchor > TimeSpan.Zero)
            _zeroReported = false;
    }

    /// <summary>Tempo che resta; negativo dopo lo zero.</summary>
    public TimeSpan Remaining(DateTime now) =>
        !_active ? TimeSpan.Zero : _running ? _remainingAtAnchor - (now - _anchor) : _remainingAtAnchor;

    /// <summary>true UNA volta quando il timer in corsa arriva a zero.</summary>
    public bool Tick(DateTime now)
    {
        if (!_active || !_running || _zeroReported || Remaining(now) > TimeSpan.Zero)
            return false;

        _zeroReported = true;
        return true;
    }

    public TimerLevel Level(DateTime now)
    {
        var seconds = Remaining(now).TotalSeconds;
        return seconds <= 0 ? TimerLevel.Over : seconds < WarningSeconds ? TimerLevel.Warning : TimerLevel.Normal;
    }

    /// <summary>
    /// Testo da mostrare: <c>08:42</c>, <c>-00:15</c>, <c>1:02:03</c> oltre l'ora. I secondi si arrotondano per eccesso
    /// (mostra 00:01 finché manca anche solo un decimo), così "00:00" compare davvero quando è zero.
    /// </summary>
    public static string Format(TimeSpan remaining)
    {
        var negative = remaining <= TimeSpan.Zero;
        var total = (long)Math.Ceiling(Math.Abs(remaining.TotalSeconds));
        if (negative)
            total = (long)Math.Floor(Math.Abs(remaining.TotalSeconds));

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        var sign = negative && total > 0 ? "-" : "";

        return hours > 0
            ? $"{sign}{hours}:{minutes:00}:{secs:00}"
            : $"{sign}{minutes:00}:{secs:00}";
    }
}
