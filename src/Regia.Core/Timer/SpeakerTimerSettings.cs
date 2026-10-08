namespace Regia.Core.Timer;

/// <summary>Angolo dell'output in cui compare il timer del relatore.</summary>
public enum TimerCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Timer a scalare di un contenuto, come salvato nel file show. Immutabile (record): cambiarlo = <c>with</c>,
/// così l'uguaglianza per valore resta valida e la voce in lista notifica il cambio.
/// </summary>
public sealed record SpeakerTimerSettings
{
    public const int MaxMinutes = 599;

    public const int MaxSeconds = MaxMinutes * 60 + 59;

    /// <summary>Timer attivo per questo contenuto (facoltativo: spento di default).</summary>
    public bool Enabled { get; init; }

    /// <summary>Durata in secondi (minuti e secondi digitati dall'operatore).</summary>
    public int DurationSeconds { get; init; } = 10 * 60;

    public TimerCorner Corner { get; init; } = TimerCorner.BottomRight;

    /// <summary>Un suono quando il timer arriva a zero.</summary>
    public bool Sound { get; init; }

    /// <summary>Si avvia solo se attivo e con una durata maggiore di zero.</summary>
    public bool IsRunnable => Enabled && DurationSeconds > 0;

    public int Minutes => DurationSeconds / 60;

    public int Seconds => DurationSeconds % 60;

    public SpeakerTimerSettings Normalize() => this with
    {
        DurationSeconds = Math.Clamp(DurationSeconds, 0, MaxSeconds),
        Corner = Enum.IsDefined(Corner) ? Corner : TimerCorner.BottomRight
    };

    public static SpeakerTimerSettings Default { get; } = new();
}
