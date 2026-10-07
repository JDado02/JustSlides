using System.Text.RegularExpressions;

namespace Regia.Core.Ppt;

/// <summary>Gravità dell'esito di "Verifica PowerPoint": verde = pronto, ambra = utilizzabile ma da controllare, rosso = non utilizzabile.</summary>
public enum PptCheckLevel
{
    Ok,
    Warning,
    Error
}

/// <summary>Stato di attivazione di Office come lo riporta OSPP.VBS (non esiste un'API documentata più diretta).</summary>
public enum PptLicenseState
{
    /// <summary>Non leggibile (OSPP assente, versione dello Store, output non riconosciuto).</summary>
    Unknown,

    Licensed,

    /// <summary>Periodo di tolleranza (OOB/OOT/esteso): funziona, ma sta per scadere.</summary>
    Grace,

    /// <summary>Non attivato o notifiche di licenza: Office passa in "funzionalità ridotta" e può mostrare richieste di accesso.</summary>
    NotLicensed
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

/// <summary>Un prodotto Office riportato da <c>OSPP.VBS /dstatus</c>.</summary>
public sealed record OsppEntry(string Name, string Status, string? ErrorDescription);

/// <summary>Tutto ciò che la verifica ha raccolto; <see cref="PptReadiness.Evaluate"/> lo trasforma in un esito per l'operatore.</summary>
public sealed record PptCheckFacts
{
    public PptInstallInfo? Install { get; init; }

    public bool ForeignInstance { get; init; }

    public PptLicenseState License { get; init; }

    /// <summary>Nome del prodotto e dettaglio dell'attivazione, se leggibili.</summary>
    public string? LicenseDetail { get; init; }

    public PptTestOutcome Test { get; init; }

    public PptSelfTestResult? SelfTest { get; init; }

    /// <summary>Messaggio di errore quando la prova non è arrivata a un esito (timeout, host caduto).</summary>
    public string? TestError { get; init; }
}

public sealed record PptCheckReport(PptCheckLevel Level, string Summary, IReadOnlyList<string> Lines);

/// <summary>
/// Regole di "Verifica PowerPoint". La prova tecnica (avvio, creazione, salvataggio, apertura) è la verità sul fatto che la regia
/// possa lavorare con PowerPoint; lo stato di attivazione è un'informazione a parte, perché Office in "funzionalità ridotta"
/// può passare la prova tecnica e poi mostrare richieste di accesso in onda.
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

        AddLicenseLine(facts, lines);

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

        return facts.License switch
        {
            PptLicenseState.NotLicensed => new PptCheckReport(PptCheckLevel.Warning,
                "PowerPoint funziona, ma Office segnala che NON è attivato. In questo stato può mostrare richieste di accesso o attivazione che bloccano la regia in onda: attivalo prima dell'evento.",
                lines),

            PptLicenseState.Grace => new PptCheckReport(PptCheckLevel.Warning,
                "PowerPoint funziona, ma l'attivazione è in periodo di tolleranza e sta per scadere: rinnovala prima dell'evento.",
                lines),

            PptLicenseState.Licensed => new PptCheckReport(PptCheckLevel.Ok,
                $"PowerPoint {version} è installato, attivato e pronto per l'uso.", lines),

            _ => new PptCheckReport(PptCheckLevel.Ok,
                $"PowerPoint {version} è installato e risponde correttamente. Lo stato di attivazione non è leggibile da questo PC.", lines)
        };
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

    private static void AddLicenseLine(PptCheckFacts facts, List<string> lines)
    {
        var state = facts.License switch
        {
            PptLicenseState.Licensed => "attivato",
            PptLicenseState.Grace => "periodo di tolleranza",
            PptLicenseState.NotLicensed => "NON attivato",
            _ => "non leggibile"
        };

        lines.Add("Attivazione: " + state + (string.IsNullOrWhiteSpace(facts.LicenseDetail) ? "" : " — " + facts.LicenseDetail));
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

/// <summary>
/// Legge l'output di <c>OSPP.VBS /dstatus</c> (script di Office). Le etichette dell'output sono in inglese qualunque sia la lingua di
/// Windows; se non si riconoscono, lo stato resta <see cref="PptLicenseState.Unknown"/> (mai un falso "attivato").
/// </summary>
public static partial class OsppParser
{
    private static readonly Regex Relevant = RelevantRegex();

    public static IReadOnlyList<OsppEntry> Parse(string? output)
    {
        var entries = new List<OsppEntry>();
        if (string.IsNullOrWhiteSpace(output))
            return entries;

        string? name = null, status = null, error = null;

        void Flush()
        {
            if (name is not null && status is not null)
                entries.Add(new OsppEntry(name, status, error));
            name = status = error = null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("LICENSE NAME:", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                name = line["LICENSE NAME:".Length..].Trim();
            }
            else if (line.StartsWith("LICENSE STATUS:", StringComparison.OrdinalIgnoreCase))
            {
                status = line["LICENSE STATUS:".Length..].Trim().Trim('-').Trim();
            }
            else if (line.StartsWith("ERROR DESCRIPTION:", StringComparison.OrdinalIgnoreCase))
            {
                error = line["ERROR DESCRIPTION:".Length..].Trim();
            }
        }

        Flush();
        return entries;
    }

    /// <summary>
    /// Sintesi per PowerPoint: contano i prodotti che sembrano la suite Office (o PowerPoint); se nessuno lo sembra, tutti.
    /// Vince il caso peggiore (un'altra applicazione non attivata nella stessa suite rende la suite non affidabile).
    /// </summary>
    public static (PptLicenseState State, string? Detail) Summarize(IReadOnlyList<OsppEntry> entries)
    {
        if (entries.Count == 0)
            return (PptLicenseState.Unknown, null);

        var relevant = entries.Where(e => Relevant.IsMatch(e.Name)).ToList();
        if (relevant.Count == 0)
            relevant = [.. entries];

        var worst = relevant.OrderByDescending(e => Severity(e.Status)).First();
        var state = Classify(worst.Status);
        if (state == PptLicenseState.Unknown)
            return (state, null);

        var detail = worst.Name + " (" + worst.Status.ToLowerInvariant() + ")";
        if (state != PptLicenseState.Licensed && !string.IsNullOrWhiteSpace(worst.ErrorDescription))
            detail += ": " + worst.ErrorDescription;

        return (state, detail);
    }

    public static PptLicenseState Classify(string? status)
    {
        var s = (status ?? "").Trim().Trim('-').Trim().ToUpperInvariant();
        return s switch
        {
            "LICENSED" => PptLicenseState.Licensed,
            "OOB_GRACE" or "OOT_GRACE" or "EXTENDED_GRACE" => PptLicenseState.Grace,
            "UNLICENSED" or "NOTIFICATIONS" or "NONGENUINE_GRACE" => PptLicenseState.NotLicensed,
            _ => PptLicenseState.Unknown
        };
    }

    private static int Severity(string status) => Classify(status) switch
    {
        PptLicenseState.NotLicensed => 3,
        PptLicenseState.Grace => 2,
        PptLicenseState.Licensed => 1,
        _ => 0
    };

    [GeneratedRegex("office|powerpoint|o365|microsoft 365", RegexOptions.IgnoreCase)]
    private static partial Regex RelevantRegex();
}
