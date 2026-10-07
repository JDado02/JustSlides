using Regia.Core.Monitors;

namespace Regia.Tests;

public sealed class OutputPresenceTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly MonitorInfo Primary =
        new("path-regia", "Regia", "DEL", 1, @"\\.\DISPLAY1", 0, 0, 3000, 2000, true, 192);

    private static readonly MonitorInfo Output =
        new("path-out", "Proiettore", "EPS", 7, @"\\.\DISPLAY2", 3000, 0, 1920, 1080, false, 96);

    private static readonly MonitorId Wanted = Output.ToId();

    private static PresenceChange Eval(OutputPresenceTracker tracker, IReadOnlyList<MonitorInfo> monitors, double seconds = 0, bool simulation = false) =>
        tracker.Evaluate(Wanted, simulation, monitors, T0.AddSeconds(seconds));

    [Fact]
    public void PrimaValutazione_NonSegnalaNulla()
    {
        Assert.Equal(PresenceChange.None, Eval(new OutputPresenceTracker(), [Primary, Output]));
        Assert.Equal(PresenceChange.None, Eval(new OutputPresenceTracker(), [Primary]));
    }

    [Fact]
    public void Sparito_SegnalaLostSubito_UnaVolta()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);

        Assert.Equal(PresenceChange.Lost, Eval(tracker, [Primary], 1));
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary], 2));
    }

    [Fact]
    public void Tornato_ConfermatoSoloDopoLaStabilita()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);
        Eval(tracker, [Primary], 1);

        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, Output], 2));
        Assert.True(tracker.Pending);
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, Output], 3.0)); // 1 s: ancora presto
        Assert.Equal(PresenceChange.Returned, Eval(tracker, [Primary, Output], 3.6)); // 1,6 s: stabile
        Assert.False(tracker.Pending);
    }

    [Fact]
    public void Rimbalzo_RipartePerIlConteggio_NessunRitornoPrematuro()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);
        Eval(tracker, [Primary], 1);

        Eval(tracker, [Primary, Output], 2);          // torna
        Eval(tracker, [Primary], 3);                  // sparisce di nuovo (la matrice cambia sorgente)
        Assert.False(tracker.Pending);

        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, Output], 4));   // riparte da qui
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, Output], 5));   // solo 1 s
        Assert.Equal(PresenceChange.Returned, Eval(tracker, [Primary, Output], 5.6));
    }

    [Fact]
    public void RimbalzoDopoIlRitorno_NonRiSegnalaLost_SeSpariscePoiTorna()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);
        Eval(tracker, [Primary], 1);
        Eval(tracker, [Primary, Output], 2);
        Eval(tracker, [Primary, Output], 4);          // Returned

        Assert.Equal(PresenceChange.Lost, Eval(tracker, [Primary], 5));
    }

    [Fact]
    public void CambioRisoluzione_SegnalaChanged_UnaVolta()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);

        var resized = Output with { Width = 1280, Height = 720 };

        Assert.Equal(PresenceChange.Changed, Eval(tracker, [Primary, resized], 1));
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, resized], 2));
    }

    [Fact]
    public void CambioPosizioneODpi_SegnalaChanged()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);

        Assert.Equal(PresenceChange.Changed, Eval(tracker, [Primary, Output with { X = -1920 }], 1));
        Assert.Equal(PresenceChange.Changed, Eval(tracker, [Primary, Output with { X = -1920, Dpi = 144 }], 2));
    }

    [Fact]
    public void Simulazione_EDevicePathCambiato_NonFaNulla()
    {
        var tracker = new OutputPresenceTracker();

        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, Output], simulation: true));
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary], 1, simulation: true));
    }

    [Fact]
    public void NessunMonitorScelto_NonFaNulla()
    {
        var tracker = new OutputPresenceTracker();

        Assert.Equal(PresenceChange.None, tracker.Evaluate(null, false, [Primary, Output], T0));
        Assert.Equal(PresenceChange.None, tracker.Evaluate(null, false, [Primary], T0.AddSeconds(1)));
    }

    [Fact]
    public void RilevamentoFallito_ListaVuota_NonESparizione()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);

        Assert.Equal(PresenceChange.None, Eval(tracker, [], 1));
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, Output], 2)); // ancora lì: nessun cambiamento
    }

    [Fact]
    public void OutputDiventatoPrimario_ConsideratoAssente()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);

        // Windows rende primario il proiettore: la regia non lo può usare come uscita.
        Assert.Equal(PresenceChange.Lost, Eval(tracker, [Output with { IsPrimary = true }], 1));
    }

    [Fact]
    public void AvvioConOutputScollegato_PoiCollegato_SegnalaReturned()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary]);

        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, Output], 10));
        Assert.Equal(PresenceChange.Returned, Eval(tracker, [Primary, Output], 12));
    }

    [Fact]
    public void Reset_RipartePulito()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);
        tracker.Reset();

        // Dopo il reset la prima valutazione stabilisce lo stato di partenza, anche se l'output manca.
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary], 1));
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary], 2));
    }

    [Fact]
    public void DevicePathCambiato_MaStessoEdid_TrovatoComePresente()
    {
        var tracker = new OutputPresenceTracker();
        Eval(tracker, [Primary, Output]);

        // Dopo un cambio di porta il device path può cambiare: l'EDID univoco lo riconosce, nessuna sparizione.
        var other = Output with { DevicePath = "path-nuovo" };
        Assert.Equal(PresenceChange.None, Eval(tracker, [Primary, other], 1));
    }
}
