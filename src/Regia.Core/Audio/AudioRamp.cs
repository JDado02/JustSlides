namespace Regia.Core.Audio;

/// <summary>Calcoli puri del volume (rampa di uscita e volume effettivo), separati dal CoreAudio per poterli testare.</summary>
public static class AudioRamp
{
    /// <summary>
    /// Fattore 0..1 della rampa lineare a zero: parte da <paramref name="start"/> e arriva a 0 dopo <paramref name="duration"/>.
    /// Durata nulla o negativa = subito zero.
    /// </summary>
    public static double FadeOutScale(double start, TimeSpan elapsed, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return 0;

        var k = Math.Clamp(elapsed / duration, 0.0, 1.0);
        return Math.Clamp(start, 0.0, 1.0) * (1.0 - k);
    }

    /// <summary>Volume effettivo 0..1 da volume utente (0-100), Mute e fattore di fade.</summary>
    public static float Effective(int volume, bool muted, double fadeScale)
    {
        if (muted)
            return 0f;

        var v = Math.Clamp(volume, 0, 100) / 100.0;
        return (float)Math.Clamp(v * Math.Clamp(fadeScale, 0.0, 1.0), 0.0, 1.0);
    }
}
