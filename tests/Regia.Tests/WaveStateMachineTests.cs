using Regia.Core.Wave;

namespace Regia.Tests;

public class WaveStateMachineTests
{
    public static IEnumerable<object[]> AllPairs()
    {
        foreach (var state in Enum.GetValues<WaveState>())
            foreach (var trigger in Enum.GetValues<WaveTrigger>())
                yield return [state, trigger];
    }

    private static WaveStateMachine In(WaveState state)
    {
        // Porta la macchina nello stato voluto passando solo da transizioni valide.
        var m = new WaveStateMachine();
        switch (state)
        {
            case WaveState.Tappo:
                break;
            case WaveState.Caricamento:
                m.Fire(WaveTrigger.Go);
                break;
            case WaveState.InTransizioneIn:
                m.Fire(WaveTrigger.Go);
                m.Fire(WaveTrigger.ContentReady);
                break;
            case WaveState.InOnda:
                m.Fire(WaveTrigger.Go);
                m.Fire(WaveTrigger.ContentReady);
                m.Fire(WaveTrigger.FadeCompleted);
                break;
            case WaveState.InTransizioneOut:
                m.Fire(WaveTrigger.Go);
                m.Fire(WaveTrigger.ContentReady);
                m.Fire(WaveTrigger.FadeCompleted);
                m.Fire(WaveTrigger.Stop);
                break;
            case WaveState.Errore:
                m.Fire(WaveTrigger.Fail);
                break;
        }

        Assert.Equal(state, m.State);
        return m;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Fire_FollowsTable(WaveState state, WaveTrigger trigger)
    {
        var m = In(state);
        var valid = WaveStateMachine.Table.TryGetValue((state, trigger), out var expected);

        var result = m.Fire(trigger);

        Assert.Equal(valid, result);
        Assert.Equal(valid ? expected : state, m.State);
    }

    [Theory]
    [InlineData(WaveState.Tappo)]
    [InlineData(WaveState.Caricamento)]
    [InlineData(WaveState.InTransizioneIn)]
    [InlineData(WaveState.InOnda)]
    [InlineData(WaveState.InTransizioneOut)]
    [InlineData(WaveState.Errore)]
    public void Panic_AlwaysReturnsToTappo(WaveState state)
    {
        var m = In(state);
        Assert.True(m.Fire(WaveTrigger.Panic));
        Assert.Equal(WaveState.Tappo, m.State);
    }

    [Fact]
    public void HappyPath_GoThenStop()
    {
        var m = new WaveStateMachine();
        var seen = new List<WaveState>();
        m.StateChanged += (_, now) => seen.Add(now);

        m.Fire(WaveTrigger.Go);
        m.Fire(WaveTrigger.ContentReady);
        m.Fire(WaveTrigger.FadeCompleted);
        m.Fire(WaveTrigger.Navigate);
        m.Fire(WaveTrigger.Stop);
        m.Fire(WaveTrigger.FadeCompleted);

        Assert.Equal(
            [WaveState.Caricamento, WaveState.InTransizioneIn, WaveState.InOnda, WaveState.InTransizioneOut, WaveState.Tappo],
            seen);
    }

    [Fact]
    public void Go_WhileLoading_IsIgnored()
    {
        var m = In(WaveState.Caricamento);
        Assert.False(m.Fire(WaveTrigger.Go));
        Assert.Equal(WaveState.Caricamento, m.State);
    }

    [Fact]
    public void Go_FromErrore_Retries()
    {
        var m = In(WaveState.Errore);
        Assert.True(m.Fire(WaveTrigger.Go));
        Assert.Equal(WaveState.Caricamento, m.State);
    }
}
