using Regia.Core.Media;
using Regia.Core.Show;

namespace Regia.Tests;

public sealed class SourceFolderSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RegiaSyncTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _cache;
    private readonly Scaletta _scaletta = new();
    private readonly SourceFolderSync _sync;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MediaItem? _live;
    private bool _critical;

    public SourceFolderSyncTests()
    {
        _source = Path.Combine(_root, "sorgente");
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_source);
        _sync = new SourceFolderSync(_scaletta, () => _live, () => _critical);
    }

    public void Dispose()
    {
        _sync.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string relative, string content, DateTime? mtime = null)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (mtime is not null)
            File.SetLastWriteTimeUtc(path, mtime.Value);
        return path;
    }

    /// <summary>Due scansioni (conferma della stabilità) e attesa della coda di copia.</summary>
    private async Task SettleAsync()
    {
        await _sync.ScanOnceAsync(Ct);
        await _sync.ScanOnceAsync(Ct);
        await _sync.WaitIdleAsync();
    }

    private void Start() => _sync.Configure(_source, _cache, autoScan: false);

    [Fact]
    public async Task FileNuovo_ComparePoiSiCopiaInLocale_ConSessioneDallaSottocartella()
    {
        Start();
        Write(@"Sala A\Rossi.pdf", "contenuto");

        await _sync.ScanOnceAsync(Ct);
        Assert.Single(_scaletta.Items); // solo la voce fissa: non ancora confermato

        await _sync.ScanOnceAsync(Ct);
        await _sync.WaitIdleAsync();

        var item = Assert.Single(_scaletta.Items, i => !i.IsFixed);
        Assert.Equal(@"Sala A\Rossi.pdf", item.RelativePath);
        Assert.Equal("Sala A", item.Session);
        Assert.Equal(CopyState.Ready, item.CopyState);
        Assert.Equal(Path.Combine(_cache, @"Sala A\Rossi.pdf"), item.Path);
        Assert.Equal("contenuto", File.ReadAllText(item.Path));
        Assert.Equal(9, item.SourceSize);
        Assert.False(item.IsUpdated);
        Assert.False(File.Exists(item.Path + ".part"));
    }

    [Fact]
    public async Task FileNuovo_NotificaItemReady()
    {
        var ready = new List<(MediaItem Item, bool Update)>();
        _sync.ItemReady += (i, u) => ready.Add((i, u));
        Start();
        Write("a.pdf", "x");

        await SettleAsync();

        var (item, update) = Assert.Single(ready);
        Assert.Equal("a.pdf", item.RelativePath);
        Assert.False(update);
    }

    [Fact]
    public async Task FileModificato_AggiornaLaCopiaEMarcaAggiornato()
    {
        Start();
        Write("a.pdf", "v1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SettleAsync();
        var item = _scaletta.Items[1];

        Write("a.pdf", "versione-due", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        await SettleAsync();

        Assert.Equal("versione-due", File.ReadAllText(item.Path));
        Assert.True(item.IsUpdated);
        Assert.Equal(CopyState.Ready, item.CopyState);
        Assert.Equal(12, item.SourceSize);
    }

    [Fact]
    public async Task FileModificatoMentreEInOnda_LaCopiaNonCambiaFinoAlTappo()
    {
        Start();
        Write("a.pptx", "v1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SettleAsync();
        var item = _scaletta.Items[1];
        _live = item;

        Write("a.pptx", "versione-due", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        await SettleAsync();

        Assert.Equal("v1", File.ReadAllText(item.Path));
        Assert.True(item.PendingUpdate);
        Assert.False(item.IsUpdated);

        // Ancora in onda: nulla cambia.
        _sync.OnWaveChanged();
        await _sync.WaitIdleAsync();
        Assert.Equal("v1", File.ReadAllText(item.Path));

        // Ritorno al Tappo: l'aggiornamento parte.
        _live = null;
        _sync.OnWaveChanged();
        await _sync.WaitIdleAsync();

        Assert.Equal("versione-due", File.ReadAllText(item.Path));
        Assert.False(item.PendingUpdate);
        Assert.True(item.IsUpdated);
    }

    [Fact]
    public async Task FileTolto_SparisceDallaListaEDallaCache()
    {
        var removed = new List<MediaItem>();
        _sync.ItemRemoved += removed.Add;
        Start();
        var path = Write("a.pdf", "x");
        await SettleAsync();
        var item = _scaletta.Items[1];

        File.Delete(path);
        await _sync.ScanOnceAsync(Ct);
        Assert.Contains(item, _scaletta.Items); // una sola scansione senza il file non basta

        await _sync.ScanOnceAsync(Ct);

        Assert.DoesNotContain(item, _scaletta.Items);
        Assert.False(File.Exists(item.Path));
        Assert.Equal([item], removed);
    }

    [Fact]
    public async Task FileToltoMentreEInOnda_RestaFinoAlTappo()
    {
        Start();
        var path = Write("a.pdf", "x");
        await SettleAsync();
        var item = _scaletta.Items[1];
        _live = item;

        File.Delete(path);
        await _sync.ScanOnceAsync(Ct);
        await _sync.ScanOnceAsync(Ct);

        Assert.Contains(item, _scaletta.Items);
        Assert.True(item.RemovedFromSource);
        Assert.True(File.Exists(item.Path));

        _live = null;
        _sync.OnWaveChanged();

        Assert.DoesNotContain(item, _scaletta.Items);
        Assert.False(File.Exists(item.Path));
    }

    [Fact]
    public async Task FileRinominato_ConservaRelatoreVolumeESpostaLaCopia()
    {
        Start();
        var oldPath = Write(@"A\vecchio.pdf", "contenuto", new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
        await SettleAsync();
        var old = _scaletta.Items[1];
        old.Speaker = "Rossi";
        old.Volume = 55;

        var newPath = Path.Combine(_source, @"B\nuovo.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(oldPath, newPath);
        await SettleAsync();

        var item = Assert.Single(_scaletta.Items, i => !i.IsFixed);
        Assert.NotSame(old, item);
        Assert.Equal(@"B\nuovo.pdf", item.RelativePath);
        Assert.Equal("B", item.Session);
        Assert.Equal("Rossi", item.Speaker);
        Assert.Equal(55, item.Volume);
        Assert.Equal(CopyState.Ready, item.CopyState);
        Assert.Equal("contenuto", File.ReadAllText(item.Path));
        Assert.False(File.Exists(old.Path));
    }

    [Fact]
    public async Task CartellaNonRaggiungibile_NonToccaLaLista_EAvvisa()
    {
        Start();
        Write("a.pdf", "x");
        await SettleAsync();
        var item = _scaletta.Items[1];
        Assert.Equal(SourceStatus.Ok, _sync.Status);

        Directory.Delete(_source, recursive: true);
        for (var i = 0; i < 4; i++)
            await _sync.ScanOnceAsync(Ct);

        Assert.Equal(SourceStatus.Unreachable, _sync.Status);
        Assert.Contains("non raggiungibile", _sync.StatusMessage);
        Assert.Contains(item, _scaletta.Items);
        Assert.Equal(CopyState.Ready, item.CopyState);
        Assert.True(File.Exists(item.Path));

        // Torna la cartella con lo stesso file: tutto come prima, nessuna rimozione.
        Write("a.pdf", "x");
        await SettleAsync();
        Assert.Equal(SourceStatus.Ok, _sync.Status);
        Assert.Contains(item, _scaletta.Items);
    }

    [Fact]
    public void CartellaNonConfigurata_Avvisa()
    {
        _sync.Configure("", _cache, autoScan: false);

        Assert.Equal(SourceStatus.NotConfigured, _sync.Status);
        Assert.Contains("non configurata", _sync.StatusMessage);
    }

    [Fact]
    public async Task AlRiavvio_LeVociConCopiaIntegraSonoSubitoPronte_AncheSenzaCartella()
    {
        // Primo "avvio": copio un file.
        Start();
        Write("a.pdf", "contenuto");
        await SettleAsync();
        var dtos = _scaletta.ToDtos();
        _sync.Stop();

        // Secondo "avvio": scaletta ricaricata, cartella sparita.
        Directory.Delete(_source, recursive: true);
        var restored = new Scaletta();
        restored.Load(dtos, _cache);
        using var sync2 = new SourceFolderSync(restored, () => null, () => false);
        var ready = new List<MediaItem>();
        sync2.ItemReady += (i, _) => ready.Add(i);

        sync2.Configure(_source, _cache, autoScan: false);
        await sync2.ScanOnceAsync(Ct);
        await sync2.ScanOnceAsync(Ct);
        await sync2.ScanOnceAsync(Ct);

        var item = restored.Items[1];
        Assert.Equal(CopyState.Ready, item.CopyState);
        Assert.True(item.CanGoOnAir);
        Assert.Equal([item], ready);
        Assert.Equal(SourceStatus.Unreachable, sync2.Status);
    }

    [Fact]
    public async Task AlRiavvio_FileCambiatoNellaCartella_SiAggiorna()
    {
        Start();
        Write("a.pdf", "v1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SettleAsync();
        var dtos = _scaletta.ToDtos();
        _sync.Stop();

        Write("a.pdf", "versione-nuova", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var restored = new Scaletta();
        restored.Load(dtos, _cache);
        using var sync2 = new SourceFolderSync(restored, () => null, () => false);
        sync2.Configure(_source, _cache, autoScan: false);

        await sync2.ScanOnceAsync(Ct);
        await sync2.ScanOnceAsync(Ct);
        await sync2.WaitIdleAsync();

        Assert.Equal("versione-nuova", File.ReadAllText(restored.Items[1].Path));
    }

    [Fact]
    public async Task TipoNonSupportato_InListaMaNonMandabile_NessunaCopia()
    {
        Start();
        Write("note.docx", "x");

        await SettleAsync();

        var item = Assert.Single(_scaletta.Items, i => !i.IsFixed);
        Assert.Equal(CopyState.Unsupported, item.CopyState);
        Assert.False(item.CanGoOnAir);
        Assert.False(File.Exists(item.Path));
    }

    [Fact]
    public async Task FileDiLockDiOffice_NonCompare()
    {
        Start();
        Write("~$Rossi.pptx", "x");

        await SettleAsync();

        Assert.Single(_scaletta.Items);
    }

    [Fact]
    public async Task NuovoFile_VaDopoL_UltimaDellaStessaSessione()
    {
        Start();
        Write(@"A\1.pdf", "x");
        Write(@"B\1.pdf", "x");
        await SettleAsync();

        Write(@"A\2.pdf", "x");
        await SettleAsync();

        Assert.Equal([@"A\1.pdf", @"A\2.pdf", @"B\1.pdf"], _scaletta.Items.Skip(1).Select(i => i.RelativePath));
    }

    [Fact]
    public async Task CopiaSospesaNeiMomentiCritici()
    {
        Start();
        _critical = true;
        Write("a.pdf", "x");
        await _sync.ScanOnceAsync(Ct);
        await _sync.ScanOnceAsync(Ct);

        await Task.Delay(600, Ct);
        var item = _scaletta.Items[1];
        Assert.Equal(CopyState.Copying, item.CopyState);
        Assert.False(File.Exists(item.Path));

        _critical = false;
        await _sync.WaitIdleAsync();

        Assert.Equal(CopyState.Ready, item.CopyState);
    }

    [Fact]
    public async Task CopyIntoFolder_CopiaNellaRadiceConNomeLibero_EPoiLaScansioneLoTrova()
    {
        Start();
        var outside = Path.Combine(_root, "esterno");
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "slide.pdf");
        File.WriteAllText(file, "esterno");
        Write("slide.pdf", "gia-presente");

        var errors = await _sync.CopyIntoFolderAsync([file, Path.Combine(_source, "slide.pdf")]);

        Assert.Empty(errors);
        Assert.Equal("esterno", File.ReadAllText(Path.Combine(_source, "slide (2).pdf")));
        Assert.Equal("gia-presente", File.ReadAllText(Path.Combine(_source, "slide.pdf")));

        await SettleAsync();
        Assert.Equal(2, _scaletta.Items.Count(i => !i.IsFixed));
    }

    [Fact]
    public async Task CopyIntoFolder_SenzaCartellaConfigurata_Avvisa()
    {
        _sync.Configure("", _cache, autoScan: false);

        var errors = await _sync.CopyIntoFolderAsync(["x.pdf"]);

        Assert.Single(errors);
    }
}
