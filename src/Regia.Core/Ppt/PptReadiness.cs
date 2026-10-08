namespace Regia.Core.Ppt;

/// <summary>Gravità dell'esito di "Verifica PowerPoint": verde = pronto, ambra = utilizzabile ma da controllare, rosso = non utilizzabile.</summary>
public enum PptCheckLevel
{
    Ok,
    Warning,
    Error
}

/// <summary>Come è andata la prova tecnica con PowerPoint.</summary>
public enum PptTestOutcome
{
    NotRun,
    Passed,

    /// <summary>La prova è partita ma un passo è fallito (vedi <see cref="PptSelfTestResult.FailedStep"/>).</summary>
    Failed,

    /// <summary>Un dialogo di PowerPoint ha bloccato le chiamate COM.</summary>
    Blocked,

    /// <summary>Nessuna risposta nel tempo previsto.</summary>
    TimedOut,

    /// <summary>PptHost o PowerPoint sono caduti durante la prova.</summary>
    HostFailed
}

/// <summary>Cosa risulta installato (da file e registro, senza avviare PowerPoint).</summary>
public sealed record PptInstallInfo(string ExePath, string? FileVersion, string? Platform, string? ProductIds);

/// <summary>Tutto ciò che la verifica ha raccolto; <see cref="PptReadiness.Evaluate"/> lo trasforma in un esito per l'operatore.</summary>
public sealed record PptCheckFacts
{
    public PptInstallInfo? Install { get; init; }

    public bool ForeignInstance { get; init; }

    public PptTestOutcome Test { get; init; }

    public PptSelfTestResult? SelfTest { get; init; }

    /// <summary>Messaggio di errore quando la prova non è arrivata a un esito (timeout, host caduto).</summary>
    public string? TestError { get; init; }
}

public sealed record PptCheckReport(PptCheckLevel Level, string Summary, IReadOnlyList<string> Lines);

/// <summary>
/// Regole di "Verifica PowerPoint". La prova tecnica (avvio, creazione, salvataggio, apertura) è la verità sul fatto che la regia
/// possa lavorare con PowerPoint. La licenza non si legge: non esiste un'API documentata e dava falsi allarmi; se Office non è
/// attivato lo si vede comunque dalla prova (salvataggio che fallisce, finestre di accesso che bloccano).
/// </summary>
public static class PptReadiness
{
    public static PptCheckReport Evaluate(PptCheckFacts facts)
    {
        var lines = new List<string>();
        AddInstallLines(facts, lines);

        if (facts.Install is null)
        {
            return new PptCheckReport(PptCheckLevel.Error,
                "PowerPoint non risulta installato: le presentazioni (.pptx) non si potranno mandare in onda. Video, audio, PDF e immagini funzionano normalmente.",
                lines);
        }

        if (facts.ForeignInstance)
        {
            return new PptCheckReport(PptCheckLevel.Error,
                "PowerPoint è già aperto (non dalla regia): chiudilo e ripeti la verifica. La regia non si aggancia a un'istanza non sua.",
                lines);
        }

        var test = facts.SelfTest;
        switch (facts.Test)
        {
            case PptTestOutcome.Blocked:
                lines.Add("Prova tecnica: bloccata da una finestra di PowerPoint" +
                          (string.IsNullOrWhiteSpace(test?.DialogTitle) ? "." : $" (\"{test!.DialogTitle}\")."));
                return new PptCheckReport(PptCheckLevel.Error,
                    "PowerPoint mostra una finestra che blocca l'automazione (accesso, attivazione o avviso): in onda la regia andrebbe in Tappo. " +
                    "Aprilo a mano, completa l'accesso o l'attivazione e ripeti la verifica.",
                    lines);

            case PptTestOutcome.TimedOut:
            case PptTestOutcome.HostFailed:
                lines.Add("Prova tecnica: " + (facts.TestError ?? "PowerPoint non ha risposto."));
                return new PptCheckReport(PptCheckLevel.Error,
                    "PowerPoint non risponde correttamente alla regia: non è affidabile per l'onda.",
                    lines);

            case PptTestOutcome.NotRun:
                return new PptCheckReport(PptCheckLevel.Warning, "La prova tecnica non è stata eseguita.", lines);
        }

        if (test is null)
            return new PptCheckReport(PptCheckLevel.Error, "La prova tecnica non ha restituito alcun esito.", lines);

        var version = string.IsNullOrWhiteSpace(test.Version) ? "?" : test.Version;
        var build = string.IsNullOrWhiteSpace(test.Build) ? "" : $", build {test.Build}";

        if (test.FailedStep is { } failed && failed != PptSelfTestSteps.Save)
        {
            lines.Add($"Prova tecnica: fallita al passo \"{StepName(failed)}\"" + (test.Error is null ? "." : $": {test.Error}"));
            return new PptCheckReport(PptCheckLevel.Error,
                $"PowerPoint {version} si avvia ma la prova non è riuscita (passo: {StepName(failed)}): non è affidabile per l'onda.",
                lines);
        }

        if (test.FailedStep == PptSelfTestSteps.Save)
        {
            lines.Add($"Prova tecnica: PowerPoint {version}{build} si avvia e crea una presentazione, ma non riesce a salvare il file di prova" +
                      (test.Error is null ? "." : $" ({test.Error})."));
            return new PptCheckReport(PptCheckLevel.Warning,
                "PowerPoint si avvia ma non riesce a salvare: può essere la \"funzionalità ridotta\" di Office non attivato. " +
                "L'apertura di una presentazione vera non è stata provata.",
                lines);
        }

        if (test.Slides != 1)
        {
            lines.Add($"Prova tecnica: la presentazione di prova riaperta ha {test.Slides} slide invece di 1.");
            return new PptCheckReport(PptCheckLevel.Error, "PowerPoint ha riaperto la presentazione di prova in modo scorretto.", lines);
        }

        lines.Add($"Prova tecnica: PowerPoint {version}{build} ha avviato, creato, salvato e riaperto in sola lettura una presentazione di prova in {test.ElapsedMs / 1000.0:0.0} s, senza finestre di dialogo.");
        lines.Add("Non verifica lo slideshow a schermo intero né i file veri dell'evento (usa il pre-flight della scaletta).");

        return new PptCheckReport(PptCheckLevel.Ok, $"PowerPoint {version} è installato e funziona: la prova è riuscita.", lines);
    }

    private static void AddInstallLines(PptCheckFacts facts, List<string> lines)
    {
        if (facts.Install is not { } install)
        {
            lines.Add("Installazione: PowerPoint non trovato.");
            return;
        }

        lines.Add("Installazione: " + install.ExePath);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(install.FileVersion))
            parts.Add(install.FileVersion!);
        if (!string.IsNullOrWhiteSpace(install.Platform))
            parts.Add(install.Platform!);
        if (!string.IsNullOrWhiteSpace(install.ProductIds))
            parts.Add(install.ProductIds!);
        if (parts.Count > 0)
            lines.Add("Versione: " + string.Join(" · ", parts));
    }

    private static string StepName(string step) => step switch
    {
        PptSelfTestSteps.Launch => "avvio",
        PptSelfTestSteps.Create => "creazione",
        PptSelfTestSteps.Save => "salvataggio",
        PptSelfTestSteps.Open => "apertura",
        PptSelfTestSteps.Dialog => "finestra di dialogo",
        _ => step
    };
}
