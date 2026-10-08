using Regia.Core.Media;
using Regia.Core.Show;
using Regia.Core.Timer;

namespace Regia.Tests;

public sealed class SpeakerTimerTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Remaining_ScalaConIlTempo()
    {
        var timer = new SpeakerTimer();
        timer.Start(600, T0);

        Assert.Equal(TimeSpan.FromSeconds(600), timer.Remaining(At(0)));
        Assert.Equal(TimeSpan.FromSeconds(420), timer.Remaining(At(180)));
    }

    [Fact]
    public void Remaining_NonAvviato_EZero()
    {
        Assert.Equal(TimeSpan.Zero, new SpeakerTimer().Remaining(At(5)));
        Assert.False(new SpeakerTimer().IsActive);
    }

    [Fact]
    public void Remaining_DopoLoZero_ContinuaInNegativo()
    {
        var timer = new SpeakerTimer();
        timer.Start(10, T0);

        Assert.Equal(TimeSpan.FromSeconds(-15), timer.Remaining(At(25)));
    }

    [Fact]
    public void Pause_FermaIlTempoERiprendeDaDoveEra()
    {
        var timer = new SpeakerTimer();
        timer.Start(600, T0);
        timer.Pause(At(100));

        Assert.True(timer.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(500), timer.Remaining(At(300)));

        timer.Resume(At(300));
        Assert.Equal(TimeSpan.FromSeconds(450), timer.Remaining(At(350)));
    }

    [Fact]
    public void AddSeconds_AggiungeEToglieUnMinuto()
    {
        var timer = new SpeakerTimer();
        timer.Start(300, T0);

        timer.AddSeconds(60, At(100));
        Assert.Equal(TimeSpan.FromSeconds(260), timer.Remaining(At(100)));

        timer.AddSeconds(-60, At(100));
        Assert.Equal(TimeSpan.FromSeconds(200), timer.Remaining(At(100)));
    }

    [Fact]
    public void AddSeconds_ANonAvviato_NonFaNulla()
    {
        var timer = new SpeakerTimer();
        timer.AddSeconds(60, T0);

        Assert.False(timer.IsActive);
        Assert.Equal(TimeSpan.Zero, timer.Remaining(T0));
    }

    [Fact]
    public void Tick_SegnalaLoZeroUnaSolaVolta()
    {
        var timer = new SpeakerTimer();
        timer.Start(10, T0);

        Assert.False(timer.Tick(At(5)));
        Assert.True(timer.Tick(At(10.1)));
        Assert.False(timer.Tick(At(11)));
        Assert.False(timer.Tick(At(60)));
    }

    [Fact]
    public void Tick_DopoAggiuntaDiTempo_SegnalaDiNuovoAlloZeroSuccessivo()
    {
        var timer = new SpeakerTimer();
        timer.Start(10, T0);
        Assert.True(timer.Tick(At(10.5)));

        // +1 minuto riporta sopra zero: lo zero successivo suona di nuovo.
        timer.AddSeconds(60, At(12));
        Assert.False(timer.Tick(At(30)));
        Assert.True(timer.Tick(At(72.5)));
    }

    [Fact]
    public void Tick_SeAncoraSottoZeroDopoAggiunta_NonRisuona()
    {
        var timer = new SpeakerTimer();
        timer.Start(10, T0);
        Assert.True(timer.Tick(At(10.5)));

        // Sforato di 80 s: +1 minuto non basta a tornare sopra zero.
        timer.AddSeconds(60, At(90));
        Assert.False(timer.Tick(At(91)));
    }

    [Fact]
    public void Tick_InPausa_NonSegnala()
    {
        var timer = new SpeakerTimer();
        timer.Start(10, T0);
        timer.Pause(At(5));

        Assert.False(timer.Tick(At(100)));
    }

    [Fact]
    public void Restart_RipartiDallaDurataIniziale_ESuonaDiNuovo()
    {
        var timer = new SpeakerTimer();
        timer.Start(10, T0);
        Assert.True(timer.Tick(At(11)));

        timer.Restart(At(20));
        Assert.Equal(TimeSpan.FromSeconds(10), timer.Remaining(At(20)));
        Assert.True(timer.Tick(At(31)));
    }

    [Fact]
    public void Stop_Disattiva()
    {
        var timer = new SpeakerTimer();
        timer.Start(10, T0);
        timer.Stop();

        Assert.False(timer.IsActive);
        Assert.False(timer.Tick(At(50)));
    }

    [Theory]
    [InlineData(600, "10:00")]
    [InlineData(522.4, "08:43")]
    [InlineData(59.2, "01:00")]
    [InlineData(1, "00:01")]
    [InlineData(0.4, "00:01")]
    [InlineData(0, "00:00")]
    [InlineData(-0.5, "00:00")]
    [InlineData(-1, "-00:01")]
    [InlineData(-15.9, "-00:15")]
    [InlineData(-62, "-01:02")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-3723, "-1:02:03")]
    public void Format_MostraMinutiESecondi(double seconds, string expected)
    {
        Assert.Equal(expected, SpeakerTimer.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(300, TimerLevel.Normal)]
    [InlineData(61, TimerLevel.Normal)]
    [InlineData(59, TimerLevel.Warning)]
    [InlineData(1, TimerLevel.Warning)]
    [InlineData(0, TimerLevel.Over)]
    [InlineData(-20, TimerLevel.Over)]
    public void Level_SecondoIlTempoCheResta(double remainingSeconds, TimerLevel expected)
    {
        var timer = new SpeakerTimer();
        timer.Start((int)Math.Ceiling(remainingSeconds), T0);
        if (remainingSeconds < 0)
            timer.Start(0, T0);

        var at = remainingSeconds < 0 ? At(-remainingSeconds) : T0;
        Assert.Equal(expected, timer.Level(at));
    }
}

public sealed class SpeakerTimerSettingsTests
{
    [Fact]
    public void Default_ESpento()
    {
        var settings = SpeakerTimerSettings.Default;

        Assert.False(settings.Enabled);
        Assert.False(settings.IsRunnable);
        Assert.Equal(TimerCorner.BottomRight, settings.Corner);
    }

    [Fact]
    public void IsRunnable_SoloSeAttivoEConDurata()
    {
        Assert.True(new SpeakerTimerSettings { Enabled = true, DurationSeconds = 30 }.IsRunnable);
        Assert.False(new SpeakerTimerSettings { Enabled = true, DurationSeconds = 0 }.IsRunnable);
        Assert.False(new SpeakerTimerSettings { Enabled = false, DurationSeconds = 30 }.IsRunnable);
    }

    [Fact]
    public void MinutiESecondi_SiScompongono()
    {
        var settings = new SpeakerTimerSettings { DurationSeconds = 12 * 60 + 5 };

        Assert.Equal(12, settings.Minutes);
        Assert.Equal(5, settings.Seconds);
    }

    [Fact]
    public void Normalize_LimitaDurataEAngolo()
    {
        var settings = new SpeakerTimerSettings { DurationSeconds = 999_999, Corner = (TimerCorner)42 }.Normalize();

        Assert.Equal(SpeakerTimerSettings.MaxSeconds, settings.DurationSeconds);
        Assert.Equal(TimerCorner.BottomRight, settings.Corner);
        Assert.Equal(0, new SpeakerTimerSettings { DurationSeconds = -5 }.Normalize().DurationSeconds);
    }

    [Fact]
    public void MediaItem_Timer_SiRicordaENotificaIlCambio()
    {
        var item = new MediaItem("a.pdf", MediaKind.Pdf);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Timer = new SpeakerTimerSettings { Enabled = true, DurationSeconds = 90, Corner = TimerCorner.TopLeft, Sound = true };

        Assert.Contains(nameof(MediaItem.Timer), changed);
        Assert.Equal(90, item.Timer.DurationSeconds);

        // Stesso valore (uguaglianza per valore): nessuna notifica, quindi nessun salvataggio inutile.
        changed.Clear();
        item.Timer = item.Timer with { };
        Assert.Empty(changed);
    }

    [Fact]
    public void MediaItem_Timer_NullDiventaDefault()
    {
        var item = new MediaItem("a.pdf", MediaKind.Pdf) { Timer = new SpeakerTimerSettings { Enabled = true } };
        item.Timer = null!;

        Assert.False(item.Timer.Enabled);
    }
}

public sealed class SpeakerTimerShowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RegiaTimerTests_" + Guid.NewGuid().ToString("N"));

    private string ShowPath => Path.Combine(_dir, "show.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ShowSalvato_ConservaIlTimerDiOgniVoce()
    {
        var store = new ShowStore(ShowPath);
        var timer = new SpeakerTimerSettings { Enabled = true, DurationSeconds = 17 * 60 + 30, Corner = TimerCorner.TopRight, Sound = true };
        store.Save(new ShowDocument
        {
            Items =
            [
                new ShowItemDto { RelativePath = "a.pptx", Timer = timer },
                new ShowItemDto { RelativePath = "b.pdf" }
            ]
        });

        var loaded = store.Load();

        Assert.Equal(timer, loaded.Items[0].Timer);
        Assert.False(loaded.Items[1].Timer.Enabled);
    }

    [Fact]
    public void ShowVecchio_SenzaTimer_SiApreConTimerSpento()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ShowPath, """
            { "Version": 1, "EventId": "abc", "Items": [ { "RelativePath": "a.mp4", "Volume": 70 } ] }
            """);

        var loaded = new ShowStore(ShowPath).Load();

        Assert.Equal(70, loaded.Items[0].Volume);
        Assert.False(loaded.Items[0].Timer.Enabled);
        Assert.Equal(TimerCorner.BottomRight, loaded.Items[0].Timer.Corner);
    }

    [Fact]
    public void Scaletta_ToDtosECreateItem_FannoIlGiroDelTimer()
    {
        var item = Scaletta.CreateItem(
            new ShowItemDto { RelativePath = "a.pdf", Timer = new SpeakerTimerSettings { Enabled = true, DurationSeconds = 45 } }, _dir);

        Assert.True(item.Timer.Enabled);
        Assert.Equal(45, item.Timer.DurationSeconds);

        var scaletta = new Scaletta();
        scaletta.Items.Add(item);
        Assert.Equal(45, scaletta.ToDtos().Single().Timer.DurationSeconds);
    }
}
