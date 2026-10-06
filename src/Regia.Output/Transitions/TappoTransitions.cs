using Regia.Core.Settings;
using Regia.Core.Wave;

namespace Regia.Output.Transitions;

/// <summary>Adatta <see cref="TappoFader"/> all'interfaccia dell'orchestratore, leggendo durata e taglio secco dalle impostazioni.</summary>
public sealed class TappoTransitions : ITappoTransitions
{
    private readonly TappoFader _fader;

    public TappoTransitions(OutputHost output, AppSettings settings)
    {
        _fader = output.Fader;
        Settings = settings;
    }

    /// <summary>Impostazioni correnti: vanno aggiornate quando l'operatore le cambia.</summary>
    public AppSettings Settings { get; set; }

    public Task<bool> RevealAsync() => _fader.FadeOutAsync(Settings.FadeDurationMs, Settings.HardCut);

    public Task<bool> CoverAsync() => _fader.FadeInAsync(Settings.FadeDurationMs, Settings.HardCut);

    public void CoverNow() => _fader.Panic();
}
