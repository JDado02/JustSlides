using Regia.Core.Media;
using Regia.Core.Monitors;
using Regia.Core.Settings;
using Regia.Core.Show;

namespace Regia.Tests;

public sealed class ShowStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RegiaShowTests_" + Guid.NewGuid().ToString("N"));

    private string ShowPath => Path.Combine(_dir, "show.json");

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_FileAssente_RestituisceShowVuotoConEventId()
    {
        var show = new ShowStore(ShowPath).Load();

        Assert.Empty(show.Items);
        Assert.False(string.IsNullOrEmpty(show.EventId));
        Assert.Equal("", show.Settings.SourceFolder);
    }

    [Fact]
    public void SaveLoad_Roundtrip_ConservaScalettaEImpostazioni()
    {
        var store = new ShowStore(ShowPath);
        var original = new ShowDocument
        {
            Settings = new AppSettings
            {
                OutputMonitor = new MonitorId(@"\\?\DISPLAY#HWP#1", "Huawei", "HWP", 1234),
                SourceFolder = @"\\nas\congresso",
                FadeDurationMs = 800
            },
            Items =
            [
                new ShowItemDto
                {
                    RelativePath = @"Sala A\Rossi.pptx", Session = "Sala A", Speaker = "Mario Rossi", Volume = 60,
                    SourceSize = 1234, SourceMtimeUtc = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc)
                },
                new ShowItemDto { RelativePath = "video.mp4", VideoEnd = VideoEndAction.Loop, Excluded = true }
            ],
            SelectedPath = "video.mp4"
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original.EventId, loaded.EventId);
        Assert.Equal(original.Settings, loaded.Settings);
        Assert.Equal(original.Items, loaded.Items);
        Assert.Equal("video.mp4", loaded.SelectedPath);
    }

    [Fact]
    public void Save_ScriveGliEnumComeStringhe()
    {
        var store = new ShowStore(ShowPath);
        store.Save(new ShowDocument { Items = [new ShowItemDto { RelativePath = "a.mp4", VideoEnd = VideoEndAction.HoldLastFrame }] });

        Assert.Contains("\"HoldLastFrame\"", File.ReadAllText(ShowPath));
    }

    [Fact]
    public void Load_FileCorrotto_RestituisceShowVuotoECreaBak()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ShowPath, "{ non è json");

        var show = new ShowStore(ShowPath).Load();

        Assert.Empty(show.Items);
        Assert.True(File.Exists(ShowPath + ".bak"));
    }

    [Fact]
    public void Load_ShowAssenteMaVecchiSettings_MigraLeImpostazioni()
    {
        new SettingsStore(SettingsPath).Save(new AppSettings
        {
            Tappo = new TappoSettings { Kind = TappoKind.Video, Path = @"C:\tappo.mp4" },
            FadeDurationMs = 700,
            AudioDeviceName = "Roland"
        });

        var show = new ShowStore(ShowPath, SettingsPath).Load();

        Assert.Equal(700, show.Settings.FadeDurationMs);
        Assert.Equal(@"C:\tappo.mp4", show.Settings.Tappo.Path);
        Assert.Equal("Roland", show.Settings.AudioDeviceName);
        Assert.Empty(show.Items);
    }

    [Fact]
    public void Load_ShowEsistente_NonMigraDaiVecchiSettings()
    {
        new SettingsStore(SettingsPath).Save(new AppSettings { FadeDurationMs = 700 });
        var store = new ShowStore(ShowPath, SettingsPath);
        store.Save(new ShowDocument { Settings = new AppSettings { FadeDurationMs = 400 } });

        Assert.Equal(400, store.Load().Settings.FadeDurationMs);
    }

    [Fact]
    public void Archive_SpostaLoShowInShows()
    {
        var store = new ShowStore(ShowPath);
        store.Save(new ShowDocument());

        var archived = store.Archive(new DateTime(2026, 10, 7, 12, 30, 5));

        Assert.NotNull(archived);
        Assert.True(File.Exists(archived));
        Assert.EndsWith("show-20261007-123005.json", archived);
        Assert.False(File.Exists(ShowPath));
    }

    [Fact]
    public void Archive_SenzaShow_RestituisceNull()
    {
        Assert.Null(new ShowStore(ShowPath).Archive(DateTime.Now));
    }
}

public sealed class SourceTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    private static SourceFile F(string path, long size = 100, int minutes = 0) => new(path, size, T0.AddMinutes(minutes));

    [Fact]
    public void FileNuovo_ConfermatoSoloDalSecondoGiroUguale()
    {
        var tracker = new SourceTracker();

        var first = tracker.Apply([F("a.pdf")]);
        var second = tracker.Apply([F("a.pdf")]);

        Assert.Empty(first.Added);
        Assert.Single(second.Added);
        Assert.Equal("a.pdf", second.Added[0].RelativePath);
    }

    [Fact]
    public void FileNuovo_CheCambiaDimensione_AspettaCheSiStabilizzi()
    {
        var tracker = new SourceTracker();

        tracker.Apply([F("a.mp4", 100)]);
        var growing = tracker.Apply([F("a.mp4", 500)]);
        var stable = tracker.Apply([F("a.mp4", 500)]);

        Assert.Empty(growing.Added);
        Assert.Single(stable.Added);
        Assert.Equal(500, stable.Added[0].Size);
    }

    [Fact]
    public void FileGiaNoto_Invariato_NonDaNulla()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pdf")]);

        Assert.True(tracker.Apply([F("a.pdf")]).IsEmpty);
        Assert.True(tracker.Apply([F("a.pdf")]).IsEmpty);
    }

    [Fact]
    public void FileModificato_ConfermatoDopoDueGiriUguali()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pptx", 100, 0)]);

        var first = tracker.Apply([F("a.pptx", 150, 5)]);
        var second = tracker.Apply([F("a.pptx", 150, 5)]);
        var third = tracker.Apply([F("a.pptx", 150, 5)]);

        Assert.Empty(first.Modified);
        Assert.Single(second.Modified);
        Assert.True(third.IsEmpty);
    }

    [Fact]
    public void FileModificatoPoiRipristinato_NonSegnalaNulla()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pptx", 100, 0)]);

        tracker.Apply([F("a.pptx", 150, 5)]);
        var back = tracker.Apply([F("a.pptx", 100, 0)]);

        Assert.True(back.IsEmpty);
        Assert.True(tracker.Apply([F("a.pptx", 100, 0)]).IsEmpty);
    }

    [Fact]
    public void FileSparito_RimossoSoloDopoDueScansioni()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pdf"), F("b.pdf")]);

        var first = tracker.Apply([F("b.pdf")]);
        var second = tracker.Apply([F("b.pdf")]);

        Assert.Empty(first.Removed);
        Assert.Equal(["a.pdf"], second.Removed);
    }

    [Fact]
    public void FileSparitoPerUnGiro_ChePoiRicompare_NonVieneRimosso()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pdf")]);

        tracker.Apply([]);
        tracker.Apply([F("a.pdf")]);
        var later = tracker.Apply([]);

        Assert.Empty(later.Removed);
    }

    [Fact]
    public void CartellaIrraggiungibile_NonRimuoveNulla_NemmenoDopoMoltiGiri()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pdf"), F("b.pdf")]);

        for (var i = 0; i < 5; i++)
        {
            var result = tracker.Apply(null);
            Assert.True(result.Unreachable);
            Assert.Empty(result.Removed);
        }

        // Tornata raggiungibile con gli stessi file: nessuna rimozione.
        Assert.True(tracker.Apply([F("a.pdf"), F("b.pdf")]).IsEmpty);
    }

    [Fact]
    public void CartellaIrraggiungibile_NonAzzeraIlConteggioDeiMancanti()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pdf")]);

        tracker.Apply([]);
        tracker.Apply(null);
        var result = tracker.Apply([]);

        Assert.Equal(["a.pdf"], result.Removed);
    }

    [Theory]
    [InlineData("~$Rossi.pptx")]
    [InlineData("Sala A/~$Rossi.pptx")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("copia.tmp")]
    [InlineData("video.mp4.part")]
    [InlineData(".nascosto.pdf")]
    public void FileDiServizio_SiIgnorano(string path)
    {
        var tracker = new SourceTracker();

        tracker.Apply([F(path)]);
        var result = tracker.Apply([F(path)]);

        Assert.True(result.IsEmpty);
        Assert.True(SourceTracker.IsIgnored(path));
    }

    [Fact]
    public void Rinomina_StessaDimensioneEData_SiRiconosce()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F(@"Sala A\vecchio.pptx", 900, 3)]);

        tracker.Apply([F(@"Sala B\nuovo.pptx", 900, 3)]);
        var result = tracker.Apply([F(@"Sala B\nuovo.pptx", 900, 3)]);

        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        var rename = Assert.Single(result.Renamed);
        Assert.Equal(@"Sala A\vecchio.pptx", rename.OldPath);
        Assert.Equal(@"Sala B\nuovo.pptx", rename.New.RelativePath);
    }

    [Fact]
    public void Rinomina_DiversaDimensione_EAggiuntaPiuRimozione()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pdf", 100)]);

        tracker.Apply([F("b.pdf", 200)]);
        var result = tracker.Apply([F("b.pdf", 200)]);

        Assert.Empty(result.Renamed);
        Assert.Single(result.Added);
        Assert.Equal(["a.pdf"], result.Removed);
    }

    [Fact]
    public void ConfrontoPercorsi_NonDistingueMaiuscole()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("Rossi.PDF")]);

        Assert.True(tracker.Apply([F("rossi.pdf")]).IsEmpty);
    }

    [Fact]
    public void Forget_FaTrattareIlFileComeNuovo()
    {
        var tracker = new SourceTracker();
        tracker.Seed([F("a.pdf")]);

        tracker.Forget("a.pdf");
        tracker.Apply([F("a.pdf")]);
        var result = tracker.Apply([F("a.pdf")]);

        Assert.Single(result.Added);
    }
}

public sealed class ScalettaTests
{
    private static MediaItem Item(string rel, string session = "") =>
        new(@"C:\cache\" + rel, MediaKindDetector.FromPath(rel)) { RelativePath = rel, Session = session };

    [Theory]
    [InlineData("file.pdf", "")]
    [InlineData(@"Sala A\file.pdf", "Sala A")]
    [InlineData("Sala A/file.pdf", "Sala A")]
    [InlineData(@"Giorno 1\Sala A\file.pdf", "Giorno 1 / Sala A")]
    public void SessionFromRelativePath_UsaLaSottocartella(string path, string expected)
    {
        Assert.Equal(expected, Scaletta.SessionFromRelativePath(path));
    }

    [Fact]
    public void Nuova_HaSoloLaSchermataDiProva()
    {
        var scaletta = new Scaletta();

        Assert.Single(scaletta.Items);
        Assert.True(scaletta.Items[0].IsFixed);
    }

    [Fact]
    public void Insert_DopoL_UltimaDellaStessaSessione()
    {
        var scaletta = new Scaletta();
        var a1 = Item(@"A\1.pdf", "A");
        var b1 = Item(@"B\1.pdf", "B");
        var a2 = Item(@"A\2.pdf", "A");
        scaletta.Insert(a1);
        scaletta.Insert(b1);

        scaletta.Insert(a2);

        Assert.Equal([MediaItem.TestPattern, a1, a2, b1], scaletta.Items);
    }

    [Fact]
    public void Insert_SessioneNuovaOVuota_InFondo()
    {
        var scaletta = new Scaletta();
        var a = Item(@"A\1.pdf", "A");
        var root = Item("r.pdf");
        var c = Item(@"C\1.pdf", "C");

        scaletta.Insert(a);
        scaletta.Insert(root);
        scaletta.Insert(c);

        Assert.Equal([MediaItem.TestPattern, a, root, c], scaletta.Items);
    }

    [Fact]
    public void Move_PrimaEDopoUnaVoce()
    {
        var scaletta = new Scaletta();
        var a = Item("a.pdf");
        var b = Item("b.pdf");
        var c = Item("c.pdf");
        scaletta.Insert(a);
        scaletta.Insert(b);
        scaletta.Insert(c);

        Assert.True(scaletta.Move(c, a, after: false));
        Assert.Equal([MediaItem.TestPattern, c, a, b], scaletta.Items);

        Assert.True(scaletta.Move(c, b, after: true));
        Assert.Equal([MediaItem.TestPattern, a, b, c], scaletta.Items);
    }

    [Fact]
    public void Move_MaiSopraLaVoceFissaNéLaVoceFissa()
    {
        var scaletta = new Scaletta();
        var a = Item("a.pdf");
        var b = Item("b.pdf");
        scaletta.Insert(a);
        scaletta.Insert(b);

        Assert.True(scaletta.Move(b, MediaItem.TestPattern, after: false));
        Assert.Equal([MediaItem.TestPattern, b, a], scaletta.Items);
        Assert.False(scaletta.Move(MediaItem.TestPattern, a, after: true));
        Assert.False(scaletta.Move(a, a, after: true));
    }

    [Fact]
    public void Remove_VoceNonInOnda_SiTogliSubito()
    {
        var scaletta = new Scaletta();
        var a = Item("a.pdf");
        scaletta.Insert(a);

        Assert.True(scaletta.Remove(a, live: null));
        Assert.DoesNotContain(a, scaletta.Items);
    }

    [Fact]
    public void Remove_VoceInOnda_RestaFinoAlTappo()
    {
        var scaletta = new Scaletta();
        var a = Item("a.pdf");
        scaletta.Insert(a);

        Assert.False(scaletta.Remove(a, live: a));
        Assert.Contains(a, scaletta.Items);
        Assert.True(a.RemovedFromSource);

        Assert.Empty(scaletta.FlushPending(live: a));
        Assert.Contains(a, scaletta.Items);

        var gone = scaletta.FlushPending(live: null);
        Assert.Equal([a], gone);
        Assert.DoesNotContain(a, scaletta.Items);
    }

    [Fact]
    public void PendingUpdates_EscludeLaVoceInOnda()
    {
        var scaletta = new Scaletta();
        var a = Item("a.pptx");
        var b = Item("b.pptx");
        a.PendingUpdate = true;
        b.PendingUpdate = true;
        scaletta.Insert(a);
        scaletta.Insert(b);

        Assert.Equal([b], scaletta.PendingUpdates(live: a));
        Assert.Equal(2, scaletta.PendingUpdates(live: null).Count);
    }

    [Fact]
    public void DtoRoundtrip_ConservaOrdineEImpostazioni_SenzaVoceFissaNeRimosse()
    {
        var scaletta = new Scaletta();
        var a = Item("a.mp4", "S1");
        a.Volume = 40;
        a.VideoEnd = VideoEndAction.HoldLastFrame;
        a.Speaker = "Rossi";
        var b = Item("b.pdf");
        var gone = Item("c.pdf");
        gone.RemovedFromSource = true;
        scaletta.Insert(a);
        scaletta.Insert(b);
        scaletta.Insert(gone);

        var dtos = scaletta.ToDtos();
        Assert.Equal(["a.mp4", "b.pdf"], dtos.Select(d => d.RelativePath));

        var restored = new Scaletta();
        restored.Load(dtos, @"C:\cache");

        Assert.Equal(3, restored.Items.Count);
        Assert.True(restored.Items[0].IsFixed);
        var ra = restored.Items[1];
        Assert.Equal(@"C:\cache\a.mp4", ra.Path);
        Assert.Equal(MediaKind.Video, ra.Kind);
        Assert.Equal(40, ra.Volume);
        Assert.Equal(VideoEndAction.HoldLastFrame, ra.VideoEnd);
        Assert.Equal("Rossi", ra.Speaker);
        Assert.Equal("S1", ra.Session);
        Assert.Equal(CopyState.Copying, ra.CopyState);
    }

    [Fact]
    public void Load_TipoNonSupportato_ComeNonMandabile()
    {
        var scaletta = new Scaletta();

        scaletta.Load([new ShowItemDto { RelativePath = "note.docx" }], @"C:\cache");

        var item = scaletta.Items[1];
        Assert.Equal(CopyState.Unsupported, item.CopyState);
        Assert.False(item.CanGoOnAir);
    }

    [Fact]
    public void CanGoOnAir_RichiedeCopiaPronta_ENessunErroreDiPreflight()
    {
        var item = Item("a.pdf");
        item.CopyState = CopyState.Copying;
        Assert.False(item.CanGoOnAir);

        item.CopyState = CopyState.Ready;
        Assert.True(item.CanGoOnAir);

        item.Preflight = PreflightStatus.Warning;
        Assert.True(item.CanGoOnAir);

        item.Preflight = PreflightStatus.Error;
        Assert.False(item.CanGoOnAir);
    }

    [Fact]
    public void MediaItem_NotificaICambiDiProprieta()
    {
        var item = Item("a.mp4");
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Volume = 30;
        item.Volume = 30; // invariato: nessuna notifica
        item.Speaker = "Bianchi";

        Assert.Equal(["Volume", "Speaker"], changed);
    }
}
