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
        public Action<FakePresenter>? Configure { get; set; }

        public IContentPresenter Create(MediaItem item)
        {
            if (item.Kind == MediaKind.Video)
                throw new NotSupportedException("Video non ancora gestito");

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

        await c.GoAsync(new MediaItem("v.mp4", MediaKind.Video));

        Assert.Equal(WaveState.Errore, c.State);
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
