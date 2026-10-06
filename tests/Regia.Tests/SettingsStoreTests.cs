using Regia.Core.Monitors;
using Regia.Core.Settings;

namespace Regia.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RegiaTests_" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_FileAssente_RestituisceDefault()
    {
        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(500, settings.FadeDurationMs);
        Assert.False(settings.HardCut);
        Assert.Null(settings.OutputMonitor);
    }

    [Fact]
    public void SaveLoad_Roundtrip_ConservaTuttiICampi()
    {
        var store = new SettingsStore(FilePath);
        var original = new AppSettings
        {
            OutputMonitor = new MonitorId(@"\\?\DISPLAY#HWP#1", "Huawei", "HWP", 1234),
            SimulationMode = true,
            Tappo = new TappoSettings { Kind = TappoKind.Video, Path = @"C:\tappo.mp4" },
            FadeDurationMs = 700,
            HardCut = true
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void Load_FileCorrotto_RestituisceDefaultECreaBak()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ questo non è json");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(new AppSettings().FadeDurationMs, settings.FadeDurationMs);
        Assert.True(File.Exists(FilePath + ".bak"));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(299, 300)]
    [InlineData(300, 300)]
    [InlineData(650, 650)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]
    public void Normalize_LimitaLaDurataDellaDissolvenza(int input, int expected)
    {
        var settings = new AppSettings { FadeDurationMs = input }.Normalize();

        Assert.Equal(expected, settings.FadeDurationMs);
    }

    [Fact]
    public void Load_DurataFuoriRange_VieneLimitata()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "FadeDurationMs": 50 }""");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(300, settings.FadeDurationMs);
    }
}
