using System.Text.Json;
using Regia.Core.Ppt;

namespace Regia.Tests;

public class PptProtocolTests
{
    [Fact]
    public void Request_RoundTrips_WithArgs()
    {
        var line = PptProtocol.Serialize(PptProtocol.Request(7, PptCommands.Open, new OpenArgs(@"C:\show\a.pptx")));

        Assert.DoesNotContain('\n', line);
        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.Equal(PptMessageKind.Request, message.Kind);
        Assert.Equal(7, message.Id);
        Assert.Equal(PptCommands.Open, message.Name);
        Assert.Equal(@"C:\show\a.pptx", PptProtocol.ReadData<OpenArgs>(message)!.Path);
    }

    [Fact]
    public void Response_Success_CarriesData()
    {
        var line = PptProtocol.Serialize(PptProtocol.Success(3, new OpenResult(12, 960, 540)));

        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.Equal(PptMessageKind.Response, message.Kind);
        Assert.True(message.Ok);
        Assert.Equal(new OpenResult(12, 960, 540), PptProtocol.ReadData<OpenResult>(message));
    }

    [Fact]
    public void Response_Failure_CarriesError()
    {
        var line = PptProtocol.Serialize(PptProtocol.Failure(4, PptErrors.ForeignInstance));

        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.False(message.Ok);
        Assert.Equal(PptErrors.ForeignInstance, message.Code);
        Assert.Equal(PptErrors.ForeignInstance, message.Error);
        Assert.Null(PptProtocol.ReadData<OpenResult>(message));
    }

    [Fact]
    public void Response_Failure_KeepsCodeAndMessageApart()
    {
        var line = PptProtocol.Serialize(PptProtocol.Failure(5, PptErrors.FileNotFound, "File mancante: a.pptx"));

        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.Equal(PptErrors.FileNotFound, message.Code);
        Assert.Equal("File mancante: a.pptx", message.Error);
    }

    [Fact]
    public void StartShowResult_CarriesWindowHandle()
    {
        var line = PptProtocol.Serialize(PptProtocol.Success(9, new StartShowResult(1, 12, 0x230916)));

        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.Equal(new StartShowResult(1, 12, 0x230916), PptProtocol.ReadData<StartShowResult>(message));
    }

    [Fact]
    public void ShowEnded_Faulted_RoundTrips()
    {
        var line = PptProtocol.Serialize(PptProtocol.Event(PptEvents.ShowEnded, new ShowEndedData(true, "PowerPoint terminato")));

        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.Equal(new ShowEndedData(true, "PowerPoint terminato"), PptProtocol.ReadData<ShowEndedData>(message));
    }

    [Fact]
    public void Event_RoundTrips()
    {
        var line = PptProtocol.Serialize(PptProtocol.Event(PptEvents.SlideChanged, new SlideChangedData(3, 10)));

        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.Equal(PptMessageKind.Event, message.Kind);
        Assert.Equal(PptEvents.SlideChanged, message.Name);
        Assert.Equal(new SlideChangedData(3, 10), PptProtocol.ReadData<SlideChangedData>(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non e' json")]
    [InlineData("{\"kind\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void MalformedLine_ReturnsFalse_NeverThrows(string? line)
    {
        Assert.False(PptProtocol.TryParse(line, out _));
    }

    [Fact]
    public void ReadData_WithWrongShape_ReturnsNullOrDefaults_NeverThrows()
    {
        var message = PptProtocol.Success(1, new { Slides = "non un numero" });

        Assert.Null(PptProtocol.ReadData<OpenResult>(message));
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        const string line = """{"kind":"Event","name":"slide","ok":true,"extra":42,"data":{"slide":2,"total":9,"nuovo":true}}""";

        Assert.True(PptProtocol.TryParse(line, out var message));
        Assert.Equal(new SlideChangedData(2, 9), PptProtocol.ReadData<SlideChangedData>(message));
    }

    [Fact]
    public void Data_IsJsonElement()
    {
        var message = PptProtocol.Success(1, new PingResult("open", 1200));

        Assert.Equal(JsonValueKind.Object, message.Data!.Value.ValueKind);
    }
}

public class SlideNavigatorTests
{
    [Theory]
    [InlineData(5, 5, 0, SlideMove.AtEnd)]       // ultima slide, nessuna animazione rimasta
    [InlineData(5, 5, 2, SlideMove.Advance)]     // ultima slide ma restano click di animazione
    [InlineData(4, 5, 0, SlideMove.Advance)]
    [InlineData(1, 1, 0, SlideMove.AtEnd)]       // presentazione di una sola slide
    [InlineData(1, 1, 1, SlideMove.Advance)]
    [InlineData(6, 5, 0, SlideMove.AtEnd)]       // posizione oltre il totale: mai Next
    public void DecideNext(int position, int total, int clicksRemaining, SlideMove expected)
    {
        Assert.Equal(expected, SlideNavigator.DecideNext(position, total, clicksRemaining));
    }

    [Theory]
    [InlineData(1, SlideMove.AtStart)]
    [InlineData(0, SlideMove.AtStart)]
    [InlineData(2, SlideMove.Advance)]
    public void DecidePrevious(int position, SlideMove expected)
    {
        Assert.Equal(expected, SlideNavigator.DecidePrevious(position));
    }
}

public class WatchdogPolicyTests
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    private static TimeSpan OperationTimeout(string operation) =>
        operation == PptCommands.Open ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5);

    private sealed class Clock
    {
        public TimeSpan Now { get; set; }
    }

    private static (WatchdogPolicy policy, Clock clock) Create()
    {
        var clock = new Clock();
        return (new WatchdogPolicy(PingTimeout, OperationTimeout, () => clock.Now), clock);
    }

    [Fact]
    public void PongInTime_IsHealthy()
    {
        var (policy, clock) = Create();

        clock.Now = TimeSpan.FromSeconds(1);
        policy.RecordPong(null, TimeSpan.Zero);
        clock.Now = TimeSpan.FromSeconds(3.5);

        Assert.Equal(WatchdogVerdict.Healthy, policy.Evaluate());
    }

    [Fact]
    public void MissingPong_IsPingTimeout()
    {
        var (policy, clock) = Create();

        clock.Now = TimeSpan.FromSeconds(3.1);

        Assert.Equal(WatchdogVerdict.PingTimeout, policy.Evaluate());
    }

    [Fact]
    public void ExactlyAtTimeout_IsStillHealthy()
    {
        var (policy, clock) = Create();

        clock.Now = PingTimeout;

        Assert.Equal(WatchdogVerdict.Healthy, policy.Evaluate());
    }

    [Fact]
    public void LongOpen_WithinOpenTimeout_IsHealthy()
    {
        var (policy, clock) = Create();

        clock.Now = TimeSpan.FromSeconds(1);
        policy.RecordPong(PptCommands.Open, TimeSpan.FromSeconds(20));

        Assert.Equal(WatchdogVerdict.Healthy, policy.Evaluate());
    }

    [Fact]
    public void LongOpen_BeyondOpenTimeout_IsBusyTooLong()
    {
        var (policy, clock) = Create();

        clock.Now = TimeSpan.FromSeconds(1);
        policy.RecordPong(PptCommands.Open, TimeSpan.FromSeconds(31));

        Assert.Equal(WatchdogVerdict.BusyTooLong, policy.Evaluate());
    }

    [Fact]
    public void SlowNavigation_UsesShortTimeout()
    {
        var (policy, clock) = Create();

        clock.Now = TimeSpan.FromSeconds(1);
        policy.RecordPong(PptCommands.Next, TimeSpan.FromSeconds(6));

        Assert.Equal(WatchdogVerdict.BusyTooLong, policy.Evaluate());
    }

    [Fact]
    public void BusyTimeGrows_BetweenPongs()
    {
        var (policy, clock) = Create();

        clock.Now = TimeSpan.FromSeconds(1);
        policy.RecordPong(PptCommands.Next, TimeSpan.FromSeconds(4));
        clock.Now = TimeSpan.FromSeconds(2.5); // 1.5 s dopo il pong: 4 + 1.5 > 5

        Assert.Equal(WatchdogVerdict.BusyTooLong, policy.Evaluate());
    }

    [Fact]
    public void Reset_StartsFromNow()
    {
        var (policy, clock) = Create();
        clock.Now = TimeSpan.FromSeconds(10);
        Assert.Equal(WatchdogVerdict.PingTimeout, policy.Evaluate());

        policy.Reset();

        Assert.Equal(WatchdogVerdict.Healthy, policy.Evaluate());
    }

    [Fact]
    public void IdlePong_ClearsBusyState()
    {
        var (policy, clock) = Create();
        clock.Now = TimeSpan.FromSeconds(1);
        policy.RecordPong(PptCommands.Next, TimeSpan.FromSeconds(4.9));
        clock.Now = TimeSpan.FromSeconds(2);

        policy.RecordPong(null, TimeSpan.Zero);
        clock.Now = TimeSpan.FromSeconds(3);

        Assert.Equal(WatchdogVerdict.Healthy, policy.Evaluate());
    }
}

public class OwnedProcessTests
{
    private static readonly DateTime Start = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SamePidAndStart_IsOurs()
    {
        var record = new OwnedProcess(1234, Start, "PptHost");

        Assert.True(record.IsSameProcess(1234, Start.AddMilliseconds(400)));
    }

    [Fact]
    public void SamePid_DifferentStartTime_IsNotOurs()
    {
        // PID riusato da Windows, o PowerPoint avviato dall'utente: non va mai terminato.
        var record = new OwnedProcess(1234, Start, "PowerPoint");

        Assert.False(record.IsSameProcess(1234, Start.AddMinutes(5)));
    }

    [Fact]
    public void DifferentPid_IsNotOurs()
    {
        var record = new OwnedProcess(1234, Start, "PptHost");

        Assert.False(record.IsSameProcess(4321, Start));
    }

    [Fact]
    public void Store_RoundTrips_AndMissingFileMeansEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "regia-test-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new OwnedProcessStore(path);
        try
        {
            Assert.Empty(store.Load());

            store.Save([new OwnedProcess(10, Start, "PptHost"), new OwnedProcess(11, Start, "PowerPoint")]);

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("PowerPoint", loaded[1].Role);
            Assert.Equal(Start, loaded[0].StartTimeUtc);

            store.Clear();
            Assert.Empty(store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_CorruptFile_MeansEmpty_NeverThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), "regia-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ rotto");

            Assert.Empty(new OwnedProcessStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
