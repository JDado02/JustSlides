using Regia.Core.Ppt;

namespace Regia.Tests;

public sealed class PptReadinessTests
{
    private static readonly PptInstallInfo Install =
        new(@"C:\Program Files\Microsoft Office\Root\Office16\POWERPNT.EXE", "16.0.20430.20092", "x64", "O365BusinessRetail");

    private static PptSelfTestResult Passed(int slides = 1) =>
        new(null, null, "16.0", "20430", slides, null, 4200);

    private static PptCheckFacts Facts(PptLicenseState license = PptLicenseState.Licensed, PptSelfTestResult? test = null,
        PptTestOutcome outcome = PptTestOutcome.Passed) =>
        new() { Install = Install, License = license, SelfTest = test ?? Passed(), Test = outcome };

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
        Assert.Contains("attivato e pronto", report.Summary);
    }

    [Fact]
    public void ProvaRiuscitaMaNonAttivato_Ambra_NonVerde()
    {
        var report = PptReadiness.Evaluate(Facts(PptLicenseState.NotLicensed));

        Assert.Equal(PptCheckLevel.Warning, report.Level);
        Assert.Contains("risulta NON attiva", report.Summary);
    }

    [Fact]
    public void PeriodoDiTolleranza_Ambra()
    {
        var report = PptReadiness.Evaluate(Facts(PptLicenseState.Grace));

        Assert.Equal(PptCheckLevel.Warning, report.Level);
        Assert.Contains("tolleranza", report.Summary);
    }

    [Fact]
    public void AttivazioneNonLeggibile_Verde_MaLoDice()
    {
        var report = PptReadiness.Evaluate(Facts(PptLicenseState.Unknown));

        Assert.Equal(PptCheckLevel.Ok, report.Level);
        Assert.Contains("non è leggibile", report.Summary);
        Assert.Contains(report.Lines, l => l.StartsWith("Attivazione: non leggibile"));
    }

    [Fact]
    public void DialogoCheBloccaPowerPoint_Rosso_ConIlTitolo()
    {
        var blocked = new PptSelfTestResult(PptSelfTestSteps.Dialog, null, null, null, 0, "Accedi a Microsoft 365", 0);
        var report = PptReadiness.Evaluate(Facts(PptLicenseState.NotLicensed, blocked, PptTestOutcome.Blocked));

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
        var report = PptReadiness.Evaluate(Facts(test: failed, outcome: PptTestOutcome.Failed));

        Assert.Equal(PptCheckLevel.Error, report.Level);
        Assert.Contains(name, report.Summary);
    }

    [Fact]
    public void SalvataggioFallito_Ambra_PerchePuoEssereLaModalitaRidotta()
    {
        var failed = new PptSelfTestResult(PptSelfTestSteps.Save, "Impossibile salvare", "16.0", null, 0, null, 100);
        var report = PptReadiness.Evaluate(Facts(PptLicenseState.NotLicensed, failed, PptTestOutcome.Failed));

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
    public void LeRigheDiDettaglio_RiportanoInstallazioneVersioneEAttivazione()
    {
        var report = PptReadiness.Evaluate(Facts(PptLicenseState.NotLicensed) with { LicenseDetail = "Office 16 (notifications)" });

        Assert.Contains(report.Lines, l => l.StartsWith("Installazione: C:\\Program Files"));
        Assert.Contains(report.Lines, l => l.Contains("16.0.20430.20092") && l.Contains("x64") && l.Contains("O365BusinessRetail"));
        Assert.Contains(report.Lines, l => l.StartsWith("Attivazione: NON attivato — Office 16"));
    }

    // --- OSPP.VBS ------------------------------------------------------------------------------------------------------

    /// <summary>Output reale di <c>cscript OSPP.VBS /dstatus</c> (PC di sviluppo, chiave omessa).</summary>
    private const string RealOutput = """
        ---Processing--------------------------
        ---------------------------------------
        PRODUCT ID: 00265-60000-00004-AA906
        SKU ID: 3d0631e3-1091-416d-92a5-42f84a86d868
        LICENSE NAME: Office 16, Office16O365BusinessR_Grace edition
        LICENSE DESCRIPTION: Office 16, RETAIL(Grace) channel
        BETA EXPIRATION: 01/01/1601
        LICENSE STATUS:  ---NOTIFICATIONS---
        ERROR CODE: 0xC004F009
        ERROR DESCRIPTION: The Software Licensing Service reported that the grace period expired.
        Last 5 characters of installed product key: XXXXX
        ---------------------------------------
        ---------------------------------------
        ---Exiting-----------------------------
        """;

    // SKU della voce di tolleranza elencata da OSPP e SKU che PowerPoint usa davvero (registro dell'utente), sul PC di sviluppo.
    private const string GraceSku = "3d0631e3-1091-416d-92a5-42f84a86d868";
    private const string SubscriptionSku = "6337137e-7c07-4197-8986-bece6a76fc33";

    [Fact]
    public void Regressione_AbbonamentoLegatoAllAccount_NonEDaSegnalareComeNonAttivato()
    {
        // Segnalato dall'utente: la voce OSPP è "grace period expired", ma PowerPoint usa un altro SKU (abbonamento attivo).
        var entries = OsppParser.Parse(RealOutput);

        var (state, detail) = OsppParser.Resolve(entries, [SubscriptionSku + ","]);

        Assert.Equal(PptLicenseState.LicensedPerUser, state);
        Assert.Contains("OSPP", detail);
        Assert.DoesNotContain("grace period expired", detail);
    }

    [Fact]
    public void LicenzaInUsoUgualeAllaVoceScaduta_NonAttivato()
    {
        var entries = OsppParser.Parse(RealOutput);

        var (state, detail) = OsppParser.Resolve(entries, ["{" + GraceSku.ToUpperInvariant() + "}"]);

        Assert.Equal(PptLicenseState.NotLicensed, state);
        Assert.Contains("grace period expired", detail);
    }

    [Fact]
    public void SenzaSkuInUso_SiRicadeSullaSolaSintesiOspp()
    {
        var (state, _) = OsppParser.Resolve(OsppParser.Parse(RealOutput), []);

        Assert.Equal(PptLicenseState.NotLicensed, state);
    }

    [Fact]
    public void SkuInUsoMaOsppVuoto_Sconosciuto()
    {
        var (state, _) = OsppParser.Resolve([], [SubscriptionSku]);

        Assert.Equal(PptLicenseState.Unknown, state);
    }

    [Fact]
    public void AbbonamentoPerUtente_Verde_ConLaNota()
    {
        var report = PptReadiness.Evaluate(Facts(PptLicenseState.LicensedPerUser) with { LicenseDetail = "legata all'account" });

        Assert.Equal(PptCheckLevel.Ok, report.Level);
        Assert.Contains("legata all'account", report.Summary);
        Assert.Contains(report.Lines, l => l.StartsWith("Attivazione: abbonamento legato all'account"));
    }

    [Fact]
    public void Ospp_PiuProdotti_OgnunoHaIlSuoSku()
    {
        const string output = """
            PRODUCT ID: 1
            SKU ID: {AAAAAAAA-0000-0000-0000-000000000001}
            LICENSE NAME: Office 16, Office16ProPlusVL_KMS_Client edition
            LICENSE STATUS:  ---LICENSED---
            PRODUCT ID: 2
            SKU ID: bbbbbbbb-0000-0000-0000-000000000002
            LICENSE NAME: Office 16, Office16VisioPro edition
            LICENSE STATUS:  ---UNLICENSED---
            """;

        var entries = OsppParser.Parse(output);

        Assert.Equal(2, entries.Count);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", entries[0].SkuId);
        Assert.Equal("bbbbbbbb-0000-0000-0000-000000000002", entries[1].SkuId);

        // PowerPoint usa il primo (licenziato): il Visio non attivato non deve contare.
        var (state, _) = OsppParser.Resolve(entries, ["AAAAAAAA-0000-0000-0000-000000000001"]);
        Assert.Equal(PptLicenseState.Licensed, state);
    }

    [Fact]
    public void Ospp_OutputReale_NotificationsComeNonAttivato()
    {
        var entries = OsppParser.Parse(RealOutput);

        var entry = Assert.Single(entries);
        Assert.Equal(GraceSku, entry.SkuId);
        Assert.Equal("NOTIFICATIONS", entry.Status);
        Assert.Contains("grace period expired", entry.ErrorDescription);

        var (state, detail) = OsppParser.Summarize(entries);
        Assert.Equal(PptLicenseState.NotLicensed, state);
        Assert.Contains("grace period expired", detail);
    }

    [Fact]
    public void Ospp_Licensed_ConA_CapoWindows()
    {
        const string output = "LICENSE NAME: Office 16, Office16ProPlusVL_KMS_Client edition\r\nLICENSE STATUS:  ---LICENSED---\r\n";

        var (state, detail) = OsppParser.Summarize(OsppParser.Parse(output));

        Assert.Equal(PptLicenseState.Licensed, state);
        Assert.Contains("licensed", detail);
    }

    [Fact]
    public void Ospp_ProdottiMisti_VinceIlPeggiore()
    {
        const string output = """
            LICENSE NAME: Office 16, Office16ProPlusVL_KMS_Client edition
            LICENSE STATUS:  ---LICENSED---
            LICENSE NAME: Office 16, Office16VisioPro edition
            LICENSE STATUS:  ---OOB_GRACE---
            """;

        var (state, _) = OsppParser.Summarize(OsppParser.Parse(output));

        Assert.Equal(PptLicenseState.Grace, state);
    }

    [Theory]
    [InlineData("LICENSED", PptLicenseState.Licensed)]
    [InlineData("OOB_GRACE", PptLicenseState.Grace)]
    [InlineData("OOT_GRACE", PptLicenseState.Grace)]
    [InlineData("EXTENDED_GRACE", PptLicenseState.Grace)]
    [InlineData("NOTIFICATIONS", PptLicenseState.NotLicensed)]
    [InlineData("UNLICENSED", PptLicenseState.NotLicensed)]
    [InlineData("NONGENUINE_GRACE", PptLicenseState.NotLicensed)]
    [InlineData("---LICENSED---", PptLicenseState.Licensed)]
    [InlineData("qualcosa di nuovo", PptLicenseState.Unknown)]
    [InlineData("", PptLicenseState.Unknown)]
    public void Ospp_Classifica(string status, PptLicenseState expected) =>
        Assert.Equal(expected, OsppParser.Classify(status));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("testo che non c'entra nulla\r\nriga due")]
    public void Ospp_OutputVuotoOSconosciuto_NonDiventaMaiAttivato(string? output)
    {
        var (state, detail) = OsppParser.Summarize(OsppParser.Parse(output));

        Assert.Equal(PptLicenseState.Unknown, state);
        Assert.Null(detail);
    }

    [Fact]
    public void Ospp_StatoNuovoNonRiconosciuto_RestaSconosciuto()
    {
        const string output = "LICENSE NAME: Office 16 Qualcosa\r\nLICENSE STATUS:  ---STATO_FUTURO---\r\n";

        var (state, _) = OsppParser.Summarize(OsppParser.Parse(output));

        Assert.Equal(PptLicenseState.Unknown, state);
    }

    [Fact]
    public void Ospp_SoloProdottiNonOffice_SiUsanoTutti()
    {
        const string output = "LICENSE NAME: Qualcosa di strano\r\nLICENSE STATUS:  ---UNLICENSED---\r\n";

        var (state, _) = OsppParser.Summarize(OsppParser.Parse(output));

        Assert.Equal(PptLicenseState.NotLicensed, state);
    }
}
