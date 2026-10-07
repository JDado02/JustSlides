using Regia.Core.Input;
using Regia.Core.Media;
using Regia.Core.Stress;

namespace Regia.Tests;

public class StressPlanTests
{
    private static readonly StressTarget[] Mixed =
    [
        new("a.jpg", MediaKind.Image),
        new("b.pdf", MediaKind.Pdf),
        new("c.mp4", MediaKind.Video),
        new("d.pptx", MediaKind.Ppt)
    ];

    [Fact]
    public void SameSeedGivesSameSequence()
    {
        var a = StressPlan.Build(Mixed, 50, seed: 7, faults: true);
        var b = StressPlan.Build(Mixed, 50, seed: 7, faults: true);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Target, b[i].Target);
            Assert.Equal(a[i].Fault, b[i].Fault);
            Assert.Equal(a[i].Exit, b[i].Exit);
            Assert.Equal(a[i].Actions, b[i].Actions);
        }
    }

    [Fact]
    public void ProducesRequestedNumberOfCyclesNumberedFromOne()
    {
        var plan = StressPlan.Build(Mixed, 100, seed: 1, faults: false);

        Assert.Equal(100, plan.Count);
        Assert.Equal(Enumerable.Range(1, 100), plan.Select(c => c.Index));
    }

    [Fact]
    public void EmptyListOrZeroCyclesGivesEmptyPlan()
    {
        Assert.Empty(StressPlan.Build([], 10, 1, false));
        Assert.Empty(StressPlan.Build(Mixed, 0, 1, false));
    }

    [Fact]
    public void EveryFileIsExercised()
    {
        var plan = StressPlan.Build(Mixed, 100, seed: 3, faults: false);

        foreach (var target in Mixed)
            Assert.Contains(plan, c => c.Target == target);
    }

    [Fact]
    public void WithoutFaultsNoFaultAndNoRudeCommands()
    {
        var plan = StressPlan.Build(Mixed, 200, seed: 5, faults: false);

        Assert.All(plan, c => Assert.Equal(StressFault.None, c.Fault));
        Assert.All(plan, c => Assert.DoesNotContain(c.Actions, a => a is StressAction.RudeDoubleGo or StressAction.RudeNextDuringFade));
    }

    [Fact]
    public void WithFaultsSomeAreInjected()
    {
        var plan = StressPlan.Build(Mixed, 200, seed: 5, faults: true);

        Assert.Contains(plan, c => c.Fault != StressFault.None);
    }

    [Fact]
    public void PowerPointKillsOnlyOnPowerPointCycles()
    {
        var plan = StressPlan.Build(Mixed, 500, seed: 11, faults: true);

        var kills = plan.Where(c => c.Fault is StressFault.KillPowerPoint or StressFault.KillPptHost).ToList();
        Assert.NotEmpty(kills);
        Assert.All(kills, c => Assert.Equal(MediaKind.Ppt, c.Target.Kind));
    }

    [Fact]
    public void WithoutPowerPointNoKillsAreScheduled()
    {
        var plan = StressPlan.Build(Mixed.Where(t => t.Kind != MediaKind.Ppt).ToList(), 500, seed: 11, faults: true);

        Assert.DoesNotContain(plan, c => c.Fault is StressFault.KillPowerPoint or StressFault.KillPptHost);
    }

    [Fact]
    public void SwitchFileNeverUsedWithASingleFile()
    {
        var plan = StressPlan.Build([Mixed[0]], 100, seed: 2, faults: false);

        Assert.DoesNotContain(plan, c => c.Exit == StressExit.SwitchFile);
    }

    [Fact]
    public void ActionsMatchFileKind()
    {
        var plan = StressPlan.Build(Mixed, 200, seed: 9, faults: false);

        Assert.All(plan.Where(c => c.Target.Kind == MediaKind.Image), c => Assert.Empty(c.Actions));
        Assert.All(plan.Where(c => c.Target.Kind is MediaKind.Ppt or MediaKind.Pdf), c => Assert.Contains(StressAction.Next, c.Actions));
        Assert.All(plan.Where(c => c.Target.Kind == MediaKind.Video), c => Assert.Contains(StressAction.PlayPause, c.Actions));
    }
}

public class StressReportTests
{
    private static StressSample Sample(int cycle, double memoryMb = 500, int handles = 1000, StressOutcome outcome = StressOutcome.Ok,
        StressFault fault = StressFault.None, int loadMs = 800, int gdi = 100) =>
        new(cycle, "file;x.pdf", "Pdf", fault, outcome, loadMs, "Tappo", memoryMb, handles, 40, gdi, 50, 2.5, 0, "");

    [Fact]
    public void EmptyReportSummarizesToZero()
    {
        var summary = new StressReport().Summarize();

        Assert.Equal(0, summary.Cycles);
        Assert.False(summary.LooksLeaky);
    }

    [Fact]
    public void CountsFailuresAndRecoveredFaults()
    {
        var report = new StressReport();
        report.Add(Sample(1));
        report.Add(Sample(2, outcome: StressOutcome.Failed));
        report.Add(Sample(3, outcome: StressOutcome.FaultRecovered, fault: StressFault.KillPptHost));

        var summary = report.Summarize();

        Assert.Equal(3, summary.Cycles);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(1, summary.FaultsInjected);
    }

    [Fact]
    public void LoadTimesIgnoreCyclesWithoutLoad()
    {
        var report = new StressReport();
        report.Add(Sample(1, loadMs: 400));
        report.Add(Sample(2, loadMs: 0));
        report.Add(Sample(3, loadMs: 1200));

        var summary = report.Summarize();

        Assert.Equal(400, summary.LoadMinMs);
        Assert.Equal(1200, summary.LoadMaxMs);
        Assert.Equal(800, summary.LoadAvgMs);
    }

    [Fact]
    public void FlatResourcesAreNotLeaky()
    {
        var report = new StressReport();
        for (var i = 1; i <= 100; i++)
            report.Add(Sample(i, memoryMb: 500 + (i % 3), handles: 1000 + (i % 5)));

        Assert.False(report.Summarize().LooksLeaky);
    }

    [Fact]
    public void GrowingMemoryIsFlagged()
    {
        var report = new StressReport();
        for (var i = 1; i <= 100; i++)
            report.Add(Sample(i, memoryMb: 500 + i * 3));

        var summary = report.Summarize();

        Assert.True(summary.MemoryGrowthMb > StressSummary.MemoryLeakSuspectMb);
        Assert.True(summary.LooksLeaky);
    }

    [Fact]
    public void GrowingHandlesAreFlagged()
    {
        var report = new StressReport();
        for (var i = 1; i <= 100; i++)
            report.Add(Sample(i, handles: 1000 + i * 10));

        Assert.True(report.Summarize().LooksLeaky);
    }

    [Fact]
    public void CsvHasHeaderAndOneLinePerSampleAndEscapesSeparator()
    {
        var report = new StressReport();
        report.Add(Sample(1));
        report.Add(Sample(2));

        var lines = report.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("ciclo;file;", lines[0]);
        Assert.Contains("file,x.pdf", lines[1]);
        Assert.All(lines, l => Assert.Equal(14, l.Count(ch => ch == ';')));
    }

    [Fact]
    public void CsvUsesInvariantDecimalPoint()
    {
        var report = new StressReport();
        report.Add(Sample(1, memoryMb: 512.5));

        Assert.Contains(";512.5;", report.ToCsv());
    }
}

public class KeyActionRepeatTests
{
    [Theory]
    [InlineData(KeyAction.Next, true)]
    [InlineData(KeyAction.Previous, true)]
    [InlineData(KeyAction.SelectUp, true)]
    [InlineData(KeyAction.SelectDown, true)]
    [InlineData(KeyAction.Go, false)]
    [InlineData(KeyAction.Panic, false)]
    [InlineData(KeyAction.BackToTappo, false)]
    [InlineData(KeyAction.PlayPause, false)]
    [InlineData(KeyAction.Mute, false)]
    public void OnlyMovementsRepeat(KeyAction action, bool expected) =>
        Assert.Equal(expected, KeyActionInfo.IsRepeatable(action));

    [Fact]
    public void EveryActionHasADecision()
    {
        // Una nuova azione aggiunta all'enum deve far scegliere esplicitamente se si ripete (qui: non si ripete finché non lo si decide).
        foreach (var action in Enum.GetValues<KeyAction>())
            _ = KeyActionInfo.IsRepeatable(action);
    }
}
