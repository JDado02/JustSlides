using Regia.Core.Media;

namespace Regia.Core.Stress;

/// <summary>Un file della scaletta che lo stress test può mandare in onda.</summary>
public sealed record StressTarget(string Name, MediaKind Kind);

/// <summary>Azione fatta mentre il file è in onda.</summary>
public enum StressAction
{
    Next,
    Previous,
    PlayPause,
    SeekForward,
    SeekBack,

    /// <summary>GO mentre il file è già in onda (cambio file via Tappo) subito ripetuto: il secondo deve essere ignorato.</summary>
    RudeDoubleGo,

    /// <summary>Avanti durante la dissolvenza in entrata: va ignorato o gestito, mai bloccare.</summary>
    RudeNextDuringFade
}

/// <summary>Come si esce dall'onda.</summary>
public enum StressExit
{
    /// <summary>Torna al Tappo con dissolvenza (tasto T).</summary>
    BackToTappo,

    /// <summary>PANIC (Esc): Tappo immediato.</summary>
    Panic,

    /// <summary>GO su un altro file: cambio file passando dal Tappo (il ciclo dopo parte già in onda).</summary>
    SwitchFile
}

/// <summary>Guasto iniettato durante l'onda (solo con <c>--faults</c>).</summary>
public enum StressFault
{
    None,
    KillPowerPoint,
    KillPptHost,
    SimulatedFail,
    OutputLostAndReturned
}

/// <summary>Un ciclo dello stress test: messa in onda, azioni, eventuale guasto, uscita.</summary>
public sealed record StressCycle(
    int Index,
    StressTarget Target,
    IReadOnlyList<StressAction> Actions,
    StressFault Fault,
    StressExit Exit);

/// <summary>
/// Genera la sequenza dello stress test. Logica pura e deterministica (stesso seme = stessa sequenza),
/// così si può testare e rifare identica una sequenza che ha dato problemi.
/// </summary>
public static class StressPlan
{
    /// <summary>Ogni quanti cicli, in media, si inietta un guasto (con <c>--faults</c>).</summary>
    public const int FaultEvery = 10;

    public static IReadOnlyList<StressCycle> Build(IReadOnlyList<StressTarget> targets, int cycles, int seed, bool faults)
    {
        if (targets.Count == 0 || cycles <= 0)
            return [];

        var random = new Random(seed);
        var plan = new List<StressCycle>(cycles);

        for (var i = 0; i < cycles; i++)
        {
            // Giro regolare sui file (ognuno viene provato), ma con un po' di disordine.
            var target = random.Next(4) == 0
                ? targets[random.Next(targets.Count)]
                : targets[i % targets.Count];

            var fault = faults && random.Next(FaultEvery) == 0 ? PickFault(random, target) : StressFault.None;

            plan.Add(new StressCycle(
                i + 1,
                target,
                PickActions(random, target.Kind, faults),
                fault,
                PickExit(random, fault, targets.Count)));
        }

        return plan;
    }

    private static StressFault PickFault(Random random, StressTarget target)
    {
        // I kill di PowerPoint/PptHost hanno senso solo con un PowerPoint in onda.
        var options = target.Kind == MediaKind.Ppt
            ? new[] { StressFault.KillPowerPoint, StressFault.KillPptHost, StressFault.SimulatedFail, StressFault.OutputLostAndReturned }
            : [StressFault.SimulatedFail, StressFault.OutputLostAndReturned];

        return options[random.Next(options.Length)];
    }

    private static List<StressAction> PickActions(Random random, MediaKind kind, bool faults)
    {
        var actions = new List<StressAction>();
        switch (kind)
        {
            case MediaKind.Ppt or MediaKind.Pdf:
                for (var n = random.Next(2, 4); n > 0; n--)
                    actions.Add(StressAction.Next);
                actions.Add(StressAction.Previous);
                break;

            case MediaKind.Video:
                actions.Add(StressAction.PlayPause);
                actions.Add(StressAction.PlayPause);
                actions.Add(random.Next(2) == 0 ? StressAction.SeekForward : StressAction.SeekBack);
                break;
        }

        if (faults && random.Next(3) == 0)
            actions.Add(random.Next(2) == 0 ? StressAction.RudeDoubleGo : StressAction.RudeNextDuringFade);

        return actions;
    }

    private static StressExit PickExit(Random random, StressFault fault, int targetCount)
    {
        // Un guasto che già porta al Tappo non ha bisogno di un'uscita "normale" particolare.
        if (fault != StressFault.None)
            return StressExit.BackToTappo;

        return random.Next(3) switch
        {
            0 => StressExit.BackToTappo,
            1 => StressExit.Panic,
            _ => targetCount > 1 ? StressExit.SwitchFile : StressExit.BackToTappo
        };
    }
}
