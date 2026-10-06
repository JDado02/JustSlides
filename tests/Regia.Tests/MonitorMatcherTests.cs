using Regia.Core.Monitors;

namespace Regia.Tests;

public sealed class MonitorMatcherTests
{
    private static MonitorInfo Mon(string path, string name = "Huawei", string man = "HWP", int prod = 1234) =>
        new(path, name, man, prod, @"\\.\DISPLAY2", 3000, 0, 1920, 1080, false, 96);

    [Fact]
    public void Find_PerDevicePath_TrovaIlMonitor()
    {
        var a = Mon("path-a", "Altro", "DEL", 1);
        var b = Mon("path-b");

        var found = MonitorMatcher.Find(b.ToId(), [a, b]);

        Assert.Same(b, found);
    }

    [Fact]
    public void Find_DevicePathCambiato_FallbackSuEdidUnivoco()
    {
        var saved = Mon("path-vecchio").ToId();
        var nuovo = Mon("path-nuovo");
        var altro = Mon("path-altro", "Altro", "DEL", 1);

        var found = MonitorMatcher.Find(saved, [altro, nuovo]);

        Assert.Same(nuovo, found);
    }

    [Fact]
    public void Find_EdidDuplicato_RestituisceNull()
    {
        var saved = Mon("path-vecchio").ToId();

        var found = MonitorMatcher.Find(saved, [Mon("path-1"), Mon("path-2")]);

        Assert.Null(found);
    }

    [Fact]
    public void Find_MonitorAssente_RestituisceNull()
    {
        var saved = Mon("path-a").ToId();

        var found = MonitorMatcher.Find(saved, [Mon("path-x", "Altro", "DEL", 1)]);

        Assert.Null(found);
    }

    [Fact]
    public void Find_NessunMonitorSalvato_RestituisceNull()
    {
        Assert.Null(MonitorMatcher.Find(null, [Mon("path-a")]));
    }
}
