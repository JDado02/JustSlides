using Regia.Core.Audio;

namespace Regia.Tests;

public class AudioTests
{
    [Fact]
    public void FadeOutScale_GoesLinearlyFromStartToZero()
    {
        var d = TimeSpan.FromMilliseconds(500);

        Assert.Equal(1.0, AudioRamp.FadeOutScale(1.0, TimeSpan.Zero, d), 6);
        Assert.Equal(0.5, AudioRamp.FadeOutScale(1.0, TimeSpan.FromMilliseconds(250), d), 6);
        Assert.Equal(0.0, AudioRamp.FadeOutScale(1.0, d, d), 6);
        Assert.Equal(0.0, AudioRamp.FadeOutScale(1.0, TimeSpan.FromSeconds(9), d), 6);
    }

    [Fact]
    public void FadeOutScale_StartsFromTheCurrentScale()
    {
        Assert.Equal(0.25, AudioRamp.FadeOutScale(0.5, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)), 6);
    }

    [Fact]
    public void FadeOutScale_ZeroDuration_IsImmediatelySilent()
    {
        Assert.Equal(0.0, AudioRamp.FadeOutScale(1.0, TimeSpan.Zero, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(100, false, 1.0, 1f)]
    [InlineData(50, false, 1.0, 0.5f)]
    [InlineData(50, false, 0.5, 0.25f)]
    [InlineData(80, true, 1.0, 0f)]
    [InlineData(250, false, 1.0, 1f)]
    [InlineData(-5, false, 1.0, 0f)]
    [InlineData(100, false, 0.0, 0f)]
    public void Effective_CombinesVolumeMuteAndFade(int volume, bool muted, double fade, float expected)
    {
        Assert.Equal(expected, AudioRamp.Effective(volume, muted, fade), 4);
    }

    [Fact]
    public void Warning_NoPpt_NoWarning()
    {
        Assert.Null(AudioDeviceWarning.Evaluate("dev-b", "Casse sala", "dev-a", hasPpt: false));
    }

    [Fact]
    public void Warning_WindowsDefaultChosen_NoWarning()
    {
        Assert.Null(AudioDeviceWarning.Evaluate("", "", "dev-a", hasPpt: true));
    }

    [Fact]
    public void Warning_ChosenIsTheDefault_NoWarning()
    {
        Assert.Null(AudioDeviceWarning.Evaluate("DEV-A", "Casse sala", "dev-a", hasPpt: true));
    }

    [Fact]
    public void Warning_ChosenIsNotTheDefault_NamesTheDevice()
    {
        var text = AudioDeviceWarning.Evaluate("dev-b", "Casse sala", "dev-a", hasPpt: true);

        Assert.NotNull(text);
        Assert.Contains("Casse sala", text);
    }

    [Fact]
    public void Warning_NoDefaultDevice_WarnsWhenOneIsChosen()
    {
        Assert.NotNull(AudioDeviceWarning.Evaluate("dev-b", "Casse sala", null, hasPpt: true));
    }
}
