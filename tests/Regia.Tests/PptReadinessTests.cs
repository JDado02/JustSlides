using Regia.Core.Ppt;

namespace Regia.Tests;

public sealed class PptReadinessTests
{
    private static readonly PptInstallInfo Install =
        new(@"C:\Program Files\Microsoft Office\Root\Office16\POWERPNT.EXE", "16.0.20430.20092", "x64", "O365BusinessRetail");

    private static PptSelfTestResult Passed(int slides = 1) =>
        new(null, null, "16.0", "20430", slides, null, 4200);

    private static PptCheckFacts Facts(PptSelfTestResult? test = null, PptTestOutcome outcome = PptTestOutcome.Passed) =>
        new() { Install = Install, SelfTest = test ?? Passed(), Test = outcome };

    // --- Valutazione ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NonInstallato_Rosso_ENonPromettePptMaTuttoIlResto()
    {
        var report = PptReadiness.Evaluate(new PptCheckFacts());

        Assert.Equal(PptCheckLevel.Error, report.Level);
        Assert.Contains("non risulta installato", report.Summary);
        Assert.Contains("Video, audio, PDF e immagini funzionano", report.Summary);
    }

    [Fact]
    public void IstanzaDellUtenteAperta_Rosso_SenzaProvaTecnica()
    {
        var report = PptReadiness.Evaluate(new PptCheckFacts { Install = Install, ForeignInstance = true });

        Assert.Equal(PptCheckLevel.Error, report.Level);
        Assert.Contains("già aperto", report.Summary);
    }

    [Fact]
    public void TuttoOk_Verde()
    {
        var report = PptReadiness.Evaluate(Facts());

        Assert.Equal(PptCheckLevel.Ok, report.Level);
        Assert.Contains("installato e funziona", report.Summary);
    }

    [Fact]
    public void DialogoCheBloccaPowerPoint_Rosso_ConIlTitolo()
    {
        var blocked = new PptSelfTestResult(PptSelfTestSteps.Dialog, null, null, null, 0, "Accedi a Microsoft 365", 0);
        var report = PptReadiness.Evaluate(Facts(blocked, PptTestOutcome.Blocked));

        Assert.Equal(PptCheckLevel.Error, report.Level);
        Assert.Contains("blocca l'automazione", report.Summary);
        Assert.Contains(report.Lines, l => l.Contains("Accedi a Microsoft 365"));
    }

    [Theory]
    [InlineData(PptTestOutcome.TimedOut)]
    [InlineData(PptTestOutcome.HostFailed)]
    public void NessunaRispostaODostCaduto_Rosso(PptTestOutcome outcome)
    {
        var report = PptReadiness.Evaluate(new PptCheckFacts
        {
            Install = Install,
            Test = outcome,
            TestError = "nessuna risposta"
        });

        Assert.Equal(PptCheckLevel.Error, report.Level);
        Assert.Contains(report.Lines, l => l.Contains("nessuna risposta"));
    }

    [Theory]
    [InlineData(PptSelfTestSteps.Launch, "avvio")]
    [InlineData(PptSelfTestSteps.Create, "creazione")]
    [InlineData(PptSelfTestSteps.Open, "apertura")]
    public void PassoFallito_Rosso_ConIlNomeDelPasso(string step, string name)
    {
        var failed = new PptSelfTestResult(step, "errore COM", "16.0", null, 0, null, 100);
        var report = PptReadiness.Evaluate(Facts(failed, PptTestOutcome.Failed));

        Assert.Equal(PptCheckLevel.Error, report.Level);
        Assert.Contains(name, report.Summary);
    }

    [Fact]
    public void SalvataggioFallito_Ambra_PerchePuoEssereLaModalitaRidotta()
    {
        var failed = new PptSelfTestResult(PptSelfTestSteps.Save, "Impossibile salvare", "16.0", null, 0, null, 100);
        var report = PptReadiness.Evaluate(Facts(failed, PptTestOutcome.Failed));

        Assert.Equal(PptCheckLevel.Warning, report.Level);
        Assert.Contains("non riesce a salvare", report.Summary);
    }

    [Fact]
    public void SlideDiversaDaUna_Rosso()
    {
        var report = PptReadiness.Evaluate(Facts(test: Passed(slides: 3)));

        Assert.Equal(PptCheckLevel.Error, report.Level);
    }

    [Fact]
    public void ProvaNonEseguita_Ambra()
    {
        var report = PptReadiness.Evaluate(new PptCheckFacts { Install = Install, Test = PptTestOutcome.NotRun });

        Assert.Equal(PptCheckLevel.Warning, report.Level);
    }

    [Fact]
    public void LeRigheDiDettaglio_RiportanoInstallazioneEVersione()
    {
        var report = PptReadiness.Evaluate(Facts());

        Assert.Contains(report.Lines, l => l.StartsWith("Installazione: C:\\Program Files"));
        Assert.Contains(report.Lines, l => l.Contains("16.0.20430.20092") && l.Contains("x64") && l.Contains("O365BusinessRetail"));
    }
}
