using Regia.Core.Media;
using Regia.Core.Wave;

namespace Regia.Tests;

public class WaveControllerTests
{
    private sealed class FakeTappo : ITappoTransitions
    {
        public TaskCompletionSource<bool>? RevealGate { get; set; }
        public TaskCompletionSource<bool>? CoverGate { get; set; }
        public List<string> Calls { get; } = [];

        public Task<bool> RevealAsync()
        {
            Calls.Add("reveal");
            return RevealGate?.Task ?? Task.FromResult(true);
        }

        public Task<bool> CoverAsync()
        {
            Calls.Add("cover");
            return CoverGate?.Task ?? Task.FromResult(true);
        }

        public void CoverNow() => Calls.Add("coverNow");

        public TimeSpan FadeDuration { get; set; } = TimeSpan.FromMilliseconds(500);
    }

    private sealed class FakeVideo(List<string> log, string name) : IPlaybackContent
    {
        public int CloseCount { get; private set; }
        public int BeginCount { get; private set; }
        public bool Paused { get; private set; }
        public int Volume { get; private set; } = -1;
        public bool Muted { get; private set; }
        public Exception? LoadError { get; set; }
        public List<TimeSpan> Fades { get; } = [];
        public PlaybackProgress Progress { get; set; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
        public PageInfo? Page => null;
        public bool IsPaused => Paused;

        public event Action? PageChanged
        {
            add { }
            remove { }
        }

        public event Action? ProgressChanged;
        public event Action? EndRequested;
        public event Action<Exception>? Faulted;

        public Task LoadAsync(CancellationToken cancellationToken) =>
            LoadError is null ? Task.CompletedTask : Task.FromException(LoadError);

        public void BeginPlayback()
        {
            BeginCount++;
            log.Add("begin:" + name);
        }

        public void TogglePause() => Paused = !Paused;

        public void SetVolume(int volume) => Volume = volume;

        public void SetMuted(bool muted) => Muted = muted;

        public Task FadeAudioOutAsync(TimeSpan duration)
        {
            Fades.Add(duration);
            log.Add("audioFade");
            return Task.CompletedTask;
        }

        public bool Next() => false;

        public bool Previous() => false;

        public void Close()
        {
            CloseCount++;
            log.Add("close:" + name);
        }

        public void RaiseEnd() => EndRequested?.Invoke();

        public void RaiseFault(Exception ex) => Faulted?.Invoke(ex);

        public void RaiseProgress() => ProgressChanged?.Invoke();
    }

    private sealed class FakePresenter(List<string> log, string name) : IContentPresenter
    {
        public TaskCompletionSource? LoadGate { get; set; }
        public Exception? LoadError { get; set; }
        public Exception? NextError { get; set; }
        public bool NextResult { get; set; } = true;
        public int CloseCount { get; private set; }
        public CancellationToken LoadToken { get; private set; }
        public PageInfo? Page { get; set; }
        public event Action? PageChanged;

        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            LoadToken = cancellationToken;
            if (LoadGate is not null)
                await LoadGate.Task.WaitAsync(cancellationToken);
            if (LoadError is not null)
                throw LoadError;
        }

        public bool Next()
        {
            if (NextError is not null)
                throw NextError;
            PageChanged?.Invoke();
            return NextResult;
        }

        public bool Previous() => NextResult;

        public void Close()
        {
            CloseCount++;
            log.Add("close:" + name);
        }
    }

    private sealed class FakeFactory(FakeTappo tappo) : IContentPresenterFactory
    {
        public Dictionary<string, FakePresenter> Presenters { get; } = [];
        public Dictionary<string, FakeVideo> Videos { get; } = [];
        public Action<FakePresenter>? Configure { get; set; }
        public Action<FakeVideo>? ConfigureVideo { get; set; }

        public IContentPresenter Create(MediaItem item)
        {
            if (item.Kind == MediaKind.Video)
            {
                var v = new FakeVideo(tappo.Calls, item.DisplayName);
                ConfigureVideo?.Invoke(v);
                Videos[item.DisplayName] = v;
                return v;
            }

            if (item.Kind == MediaKind.Ppt)
                throw new NotSupportedException("PowerPoint non ancora gestito");

            var p = new FakePresenter(tappo.Calls, item.DisplayName);
            Configure?.Invoke(p);
            Presenters[item.DisplayName] = p;
            return p;
        }
    }

    private static (WaveController c, FakeTappo t, FakeFactory f) Create()
    {
        var t = new FakeTappo();
        var f = new FakeFactory(t);
        return (new WaveController(new WaveStateMachine(), t, f), t, f);
    }

    private static readonly MediaItem A = new("a.png", MediaKind.Image);
    private static readonly MediaItem B = new("b.pdf", MediaKind.Pdf);

    [Fact]
    public async Task Go_FullCycle_EndsInOnda()
    {
        var (c, t, _) = Create();

        await c.GoAsync(A);

        Assert.Equal(WaveState.InOnda, c.State);
        Assert.Equal(A, c.CurrentItem);
        Assert.Equal(["reveal"], t.Calls);
    }

    [Fact]
    public async Task Stop_ClosesMediaOnlyAfterCover()
    {
        var (c, t, f) = Create();
        await c.GoAsync(A);
        t.CoverGate = new TaskCompletionSource<bool>();

        var stop = c.StopAsync();
        Assert.Equal(WaveState.InTransizioneOut, c.State);
        Assert.Equal(0, f.Presenters["a.png"].CloseCount);

        t.CoverGate.SetResult(true);
        await stop;

        Assert.Equal(WaveState.Tappo, c.State);
        Assert.Equal(1, f.Presenters["a.png"].CloseCount);
        Assert.True(t.Calls.IndexOf("cover") < t.Calls.IndexOf("close:a.png"));
    }

    [Fact]
    public async Task Panic_DuringLoad_CancelsAndClosesMedia()
    {
        var (c, t, f) = Create();
        f.Configure = p => p.LoadGate = new TaskCompletionSource();

        var go = c.GoAsync(A);
        Assert.Equal(WaveState.Caricamento, c.State);

        c.Panic();
        Assert.Equal(WaveState.Tappo, c.State);
        Assert.True(f.Presenters["a.png"].LoadToken.IsCancellationRequested);
        await go;

        Assert.Equal(WaveState.Tappo, c.State);
        Assert.Contains("coverNow", t.Calls);
        Assert.DoesNotContain("reveal", t.Calls);
        Assert.True(f.Presenters["a.png"].CloseCount >= 1);
    }

    [Fact]
    public async Task Panic_DuringReveal_StaysOnTappo()
    {
        var (c, t, f) = Create();
        t.RevealGate = new TaskCompletionSource<bool>();

        var go = c.GoAsync(A);
        Assert.Equal(WaveState.InTransizioneIn, c.State);

        c.Panic();
        t.RevealGate.SetResult(false); // la dissolvenza viene interrotta
        await go;

        Assert.Equal(WaveState.Tappo, c.State);
        Assert.Null(c.CurrentItem);
        Assert.Equal(1, f.Presenters["a.png"].CloseCount);
    }

    [Fact]
    public async Task LoadFailure_GoesToErrore_WithTappoAndMediaClosed()
    {
        var (c, t, f) = Create();
        f.Configure = p => p.LoadError = new InvalidOperationException("file corrotto");
        string? message = null;
        c.ErrorOccurred += m => message = m;

        await c.GoAsync(A);

        Assert.Equal(WaveState.Errore, c.State);
        Assert.Contains("coverNow", t.Calls);
        Assert.DoesNotContain("reveal", t.Calls);
        Assert.Equal(1, f.Presenters["a.png"].CloseCount);
        Assert.Contains("file corrotto", message);
    }

    [Fact]
    public async Task UnsupportedKind_GoesToErrore()
    {
        var (c, _, _) = Create();

        await c.GoAsync(new MediaItem("p.pptx", MediaKind.Ppt));

        Assert.Equal(WaveState.Errore, c.State);
    }

    private static readonly MediaItem V = new("v.mp4", MediaKind.Video);

    [Fact]
    public async Task Video_StartsPlaybackBeforeReveal()
    {
        var (c, t, f) = Create();

        await c.GoAsync(V);

        Assert.Equal(WaveState.InOnda, c.State);
        Assert.Equal(["begin:v.mp4", "reveal"], t.Calls);
        Assert.Equal(1, f.Videos["v.mp4"].BeginCount);
    }

    [Fact]
    public async Task Video_EndRequested_FadesBackToTappo()
    {
        var (c, t, f) = Create();
        await c.GoAsync(V);
        t.Calls.Clear();

        f.Videos["v.mp4"].RaiseEnd();
        await Task.Yield();

        Assert.Equal(WaveState.Tappo, c.State);
        Assert.Equal(["audioFade", "cover", "close:v.mp4"], t.Calls);
    }

    [Fact]
    public async Task Video_EndDuringFadeIn_StopsAfterFadeIn()
    {
        var (c, t, f) = Create();
        t.RevealGate = new TaskCompletionSource<bool>();

        var go = c.GoAsync(V);
        Assert.Equal(WaveState.InTransizioneIn, c.State);

        f.Videos["v.mp4"].RaiseEnd(); // video più corto della dissolvenza
        Assert.Equal(WaveState.InTransizioneIn, c.State);

        t.RevealGate.SetResult(true);
        await go;

        Assert.Equal(WaveState.Tappo, c.State);
        Assert.Equal(1, f.Videos["v.mp4"].CloseCount);
        Assert.Contains("cover", t.Calls);
    }

    [Fact]
    public async Task Video_Fault_GoesToErrore()
    {
        var (c, t, f) = Create();
        string? message = null;
        c.ErrorOccurred += m => message = m;
        await c.GoAsync(V);

        f.Videos["v.mp4"].RaiseFault(new InvalidOperationException("decoder morto"));

        Assert.Equal(WaveState.Errore, c.State);
        Assert.Contains("coverNow", t.Calls);
        Assert.Equal(1, f.Videos["v.mp4"].CloseCount);
        Assert.Contains("decoder morto", message);
    }

    [Fact]
    public async Task Video_StopFadesAudioWithTappoDuration()
    {
        var (c, t, f) = Create();
        t.FadeDuration = TimeSpan.FromMilliseconds(800);
        await c.GoAsync(V);

        await c.StopAsync();

        Assert.Equal([TimeSpan.FromMilliseconds(800)], f.Videos["v.mp4"].Fades);
        Assert.True(t.Calls.IndexOf("audioFade") < t.Calls.IndexOf("cover"));
    }

    [Fact]
    public async Task Video_SwitchFile_FadesAudioToo()
    {
        var (c, _, f) = Create();
        await c.GoAsync(V);

        await c.GoAsync(A);

        Assert.Single(f.Videos["v.mp4"].Fades);
        Assert.Equal(WaveState.InOnda, c.State);
    }

    [Fact]
    public async Task Video_Panic_ClosesWithoutAudioFade()
    {
        var (c, t, f) = Create();
        await c.GoAsync(V);

        c.Panic();

        Assert.Equal(WaveState.Tappo, c.State);
        Assert.Empty(f.Videos["v.mp4"].Fades);
        Assert.Contains("coverNow", t.Calls);
        Assert.Equal(1, f.Videos["v.mp4"].CloseCount);
        Assert.False(c.IsPaused);
        Assert.Null(c.Progress);
    }

    [Fact]
    public async Task Video_LoadFailure_GoesToErrore()
    {
        var (c, _, f) = Create();
        f.ConfigureVideo = v => v.LoadError = new TimeoutException("non si apre");

        await c.GoAsync(V);

        Assert.Equal(WaveState.Errore, c.State);
        Assert.Equal(1, f.Videos["v.mp4"].CloseCount);
    }

    [Fact]
    public async Task TogglePause_OnVideo_TogglesAndNotifies()
    {
        var (c, _, f) = Create();
        await c.GoAsync(V);
        var notified = 0;
        c.PlaybackChanged += () => notified++;

        Assert.True(c.TogglePause());
        Assert.True(c.IsPaused);
        Assert.True(f.Videos["v.mp4"].Paused);
        Assert.True(c.TogglePause());
        Assert.False(c.IsPaused);
        Assert.True(notified >= 2);
    }

    [Fact]
    public async Task TogglePause_IgnoredOutsideVideoOnAir()
    {
        var (c, _, _) = Create();
        Assert.False(c.TogglePause()); // Tappo

        await c.GoAsync(A);
        Assert.False(c.TogglePause()); // immagine in onda
        Assert.Equal(WaveState.InOnda, c.State);
    }

    [Fact]
    public async Task TogglePause_IgnoredWhileFadingIn()
    {
        var (c, t, f) = Create();
        t.RevealGate = new TaskCompletionSource<bool>();
        var go = c.GoAsync(V);

        Assert.False(c.TogglePause());
        Assert.False(f.Videos["v.mp4"].Paused);

        t.RevealGate.SetResult(true);
        await go;
    }

    [Fact]
    public async Task VolumeAndMute_AreAppliedToNextVideo()
    {
        var (c, _, f) = Create();
        c.SetVolume(40);
        c.SetMuted(true);

        await c.GoAsync(V);

        Assert.Equal(40, f.Videos["v.mp4"].Volume);
        Assert.True(f.Videos["v.mp4"].Muted);

        c.SetVolume(250); // oltre il limite: si riporta a 100
        Assert.Equal(100, f.Videos["v.mp4"].Volume);
    }

    [Fact]
    public async Task Progress_ExposedOnlyForVideo()
    {
        var (c, _, f) = Create();
        await c.GoAsync(A);
        Assert.Null(c.Progress);

        await c.GoAsync(V);
        Assert.Equal(TimeSpan.FromSeconds(7), c.Progress!.Value.Remaining);

        var count = 0;
        c.PlaybackChanged += () => count++;
        f.Videos["v.mp4"].RaiseProgress();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Go_WhileOnAir_SwitchesViaTappo()
    {
        var (c, t, f) = Create();
        await c.GoAsync(A);
        t.Calls.Clear();

        await c.GoAsync(B);

        Assert.Equal(WaveState.InOnda, c.State);
        Assert.Equal(B, c.CurrentItem);
        Assert.Equal(["cover", "close:a.png", "reveal"], t.Calls);
        Assert.Equal(1, f.Presenters["a.png"].CloseCount);
    }

    [Fact]
    public async Task Go_WhileLoading_IsIgnored()
    {
        var (c, _, f) = Create();
        f.Configure = p => p.LoadGate = new TaskCompletionSource();

        var go = c.GoAsync(A);
        await c.GoAsync(B);

        Assert.DoesNotContain("b.pdf", f.Presenters.Keys);
        c.Panic();
        await go;
    }

    [Fact]
    public void Navigate_IgnoredOutsideOnda()
    {
        var (c, _, _) = Create();

        Assert.False(c.Next());
        Assert.False(c.Previous());
        Assert.Equal(WaveState.Tappo, c.State);
    }

    [Fact]
    public async Task Navigate_WhenPresenterRefuses_ReturnsFalseAndStaysOnAir()
    {
        var (c, _, f) = Create();
        await c.GoAsync(A);
        f.Presenters["a.png"].NextResult = false;

        Assert.False(c.Next());
        Assert.Equal(WaveState.InOnda, c.State);
    }

    [Fact]
    public async Task Navigate_Exception_GoesToErrore()
    {
        var (c, t, f) = Create();
        await c.GoAsync(A);
        f.Presenters["a.png"].NextError = new InvalidOperationException("boom");

        Assert.False(c.Next());

        Assert.Equal(WaveState.Errore, c.State);
        Assert.Contains("coverNow", t.Calls);
        Assert.Equal(1, f.Presenters["a.png"].CloseCount);
    }

    [Fact]
    public async Task PageChanged_IsForwarded()
    {
        var (c, _, _) = Create();
        await c.GoAsync(B);
        var count = 0;
        c.PageChanged += () => count++;

        c.Next();

        Assert.True(count >= 1);
    }

    [Fact]
    public async Task Fail_FromAnyState_GoesToErrore()
    {
        var (c, _, f) = Create();
        await c.GoAsync(A);

        c.Fail("test", new InvalidOperationException("x"));

        Assert.Equal(WaveState.Errore, c.State);
        Assert.Equal(1, f.Presenters["a.png"].CloseCount);
    }

    [Fact]
    public async Task Go_FromErrore_Works()
    {
        var (c, _, _) = Create();
        c.Fail("test", new InvalidOperationException("x"));

        await c.GoAsync(A);

        Assert.Equal(WaveState.InOnda, c.State);
    }
}
