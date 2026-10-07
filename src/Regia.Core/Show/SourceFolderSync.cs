using System.Threading.Channels;
using Regia.Core.Media;
using Serilog;

namespace Regia.Core.Show;

public enum SourceStatus
{
    /// <summary>Nessuna cartella impostata: l'operatore deve configurarla.</summary>
    NotConfigured,

    Ok,

    /// <summary>La cartella impostata non esiste o non risponde (rete caduta, chiavetta tolta...).</summary>
    Unreachable
}

/// <summary>
/// Tiene allineata la scaletta alla cartella collegata: la riscansiona ogni pochi secondi, copia in locale i file
/// nuovi o modificati (si va in onda SOLO dalla copia) e toglie quelli spariti. Tutto avviene lontano dal thread
/// UI (scansione e copia su thread pool), ma la scaletta si modifica solo dal contesto da cui è stato chiamato
/// <see cref="Configure"/> (il thread UI). Regole di sicurezza:
/// <list type="bullet">
/// <item>il file in onda non viene mai toccato: aggiornamento e rimozione sono rimandati al ritorno al Tappo;</item>
/// <item>le copie sono sospese durante caricamento e dissolvenze;</item>
/// <item>una cartella irraggiungibile non toglie nulla dalla lista (vedi <see cref="SourceTracker"/>).</item>
/// </list>
/// </summary>
public sealed class SourceFolderSync : IDisposable
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

    private readonly Scaletta _scaletta;
    private readonly Func<MediaItem?> _live;
    private readonly Func<bool> _isCritical;
    private readonly SourceTracker _tracker = new();
    private readonly HashSet<MediaItem> _queued = [];

    private CancellationTokenSource? _cts;
    private Channel<MediaItem>? _queue;
    private Task<IReadOnlyList<SourceFile>?>? _scanTask;
    private DateTime _scanStartedUtc;
    private string _folder = "";
    private string _cacheRoot = "";
    private int _active;

    public SourceFolderSync(Scaletta scaletta, Func<MediaItem?> live, Func<bool> isCritical)
    {
        _scaletta = scaletta;
        _live = live;
        _isCritical = isCritical;
    }

    public SourceStatus Status { get; private set; } = SourceStatus.NotConfigured;

    /// <summary>Messaggio per l'operatore; vuoto quando tutto è a posto.</summary>
    public string StatusMessage { get; private set; } = "";

    public string Folder => _folder;

    public event Action? StatusChanged;

    /// <summary>Una copia locale è pronta (o aggiornata): serve pre-flight e miniatura.</summary>
    public event Action<MediaItem, bool>? ItemReady;

    /// <summary>Una voce è stata sostituita da un'altra (file rinominato nella cartella).</summary>
    public event Action<MediaItem, MediaItem>? ItemReplaced;

    /// <summary>Una voce è sparita dalla scaletta perché il file non è più nella cartella.</summary>
    public event Action<MediaItem>? ItemRemoved;

    /// <summary>È cambiato qualcosa che va nel file show: autosave.</summary>
    public event Action? PersistRequested;

    /// <summary>
    /// Imposta cartella e cache e (ri)avvia la sincronizzazione. Va chiamato dal thread UI. Le voci già in scaletta
    /// con copia locale integra sono subito pronte, anche se la cartella non è raggiungibile. Con
    /// <paramref name="autoScan"/> a false non c'è il ciclo automatico: si scansiona a mano con <see cref="ScanOnceAsync"/> (test).
    /// </summary>
    public void Configure(string folder, string cacheRoot, bool autoScan = true)
    {
        Stop();

        _folder = folder?.Trim() ?? "";
        _cacheRoot = cacheRoot;
        _queue = Channel.CreateUnbounded<MediaItem>();
        _queued.Clear();
        _active = 0;
        _cts = new CancellationTokenSource();

        var seed = new List<SourceFile>();
        foreach (var item in _scaletta.Items.Where(i => !i.IsFixed).ToList())
        {
            if (item.Kind == MediaKind.Unknown)
            {
                item.CopyState = CopyState.Unsupported;
                item.Preflight = PreflightStatus.Error;
                item.StatusDetail = "Tipo di file non supportato";
                continue;
            }

            if (HasValidLocalCopy(item))
            {
                item.CopyState = CopyState.Ready;
                seed.Add(new SourceFile(item.RelativePath, item.SourceSize, item.SourceMtimeUtc));
                ItemReady?.Invoke(item, false);
            }
            else
            {
                item.CopyState = CopyState.Copying;
            }
        }

        _tracker.Seed(seed);

        if (_folder.Length == 0)
        {
            SetStatus(SourceStatus.NotConfigured, "Cartella contenuti non configurata: impostala da Impostazioni per far comparire i file in scaletta.");
            return;
        }

        var token = _cts.Token;
        // Il lavoratore parte dal thread UI: dopo ogni await la scaletta si tocca solo da lì. L'I/O pesante è in Task.Run.
        _ = CopyWorkerAsync(_queue, token);

        if (autoScan)
            _ = RunAsync(token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _queue?.Writer.TryComplete();
        _queue = null;
    }

    public void Dispose() => Stop();

    private static bool HasValidLocalCopy(MediaItem item)
    {
        try
        {
            return item.SourceSize > 0 && File.Exists(item.Path) && new FileInfo(item.Path).Length == item.SourceSize;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(ScanInterval);
            do
            {
                try
                {
                    await ScanOnceAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Errore nella scansione della cartella contenuti");
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Riconfigurazione o chiusura.
        }
    }

    /// <summary>Una scansione: elenco in background (con tetto di tempo), poi applicazione dei cambiamenti sul thread chiamante.</summary>
    public async Task ScanOnceAsync(CancellationToken token = default)
    {
        if (_folder.Length == 0)
            return;

        // Una scansione di rete bloccata non si sovrappone alla successiva: si aspetta che finisca.
        if (_scanTask is { IsCompleted: false })
        {
            if (DateTime.UtcNow - _scanStartedUtc > ScanTimeout)
                SetUnreachable();
            return;
        }

        var folder = _folder;
        _scanStartedUtc = DateTime.UtcNow;
        var scan = _scanTask = Task.Run(() => FolderScanner.Scan(folder), CancellationToken.None);

        IReadOnlyList<SourceFile>? files;
        try
        {
            files = await scan.WaitAsync(ScanTimeout, token);
        }
        catch (TimeoutException)
        {
            SetUnreachable();
            return;
        }

        if (!string.Equals(folder, _folder, StringComparison.Ordinal))
            return; // nel frattempo la cartella è cambiata

        var changes = _tracker.Apply(files);
        if (changes.Unreachable)
        {
            SetUnreachable();
            return;
        }

        SetStatus(SourceStatus.Ok, "");
        if (!changes.IsEmpty)
            ApplyChanges(changes);
    }

    private void SetUnreachable() =>
        SetStatus(SourceStatus.Unreachable,
            $"Cartella contenuti non raggiungibile: {_folder}. I file già copiati restano utilizzabili; la lista non viene modificata.");

    private void SetStatus(SourceStatus status, string message)
    {
        if (Status == status && StatusMessage == message)
            return;

        if (Status != status)
        {
            if (status == SourceStatus.Unreachable)
                Log.Warning("Cartella contenuti non raggiungibile: {Folder}", _folder);
            else if (Status == SourceStatus.Unreachable)
                Log.Information("Cartella contenuti di nuovo raggiungibile: {Folder}", _folder);
        }

        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private void ApplyChanges(ScanChanges changes)
    {
        var live = _live();

        foreach (var file in changes.Added)
        {
            var item = _scaletta.Find(file.RelativePath);
            if (item is null)
            {
                item = Create(file.RelativePath);
                _scaletta.Insert(item);
                Log.Information("Nuovo file nella cartella contenuti: {Path} ({Kind})", file.RelativePath, item.Kind);
            }
            else
            {
                item.RemovedFromSource = false;
            }

            if (item.CopyState != CopyState.Unsupported)
                Enqueue(item);
        }

        foreach (var file in changes.Modified)
        {
            var item = _scaletta.Find(file.RelativePath);
            if (item is null)
            {
                item = Create(file.RelativePath);
                _scaletta.Insert(item);
            }

            if (item.CopyState == CopyState.Unsupported)
                continue;

            Log.Information("File modificato nella cartella contenuti: {Path}", file.RelativePath);
            if (ReferenceEquals(item, live))
                DeferUpdate(item);
            else
                Enqueue(item);
        }

        foreach (var relative in changes.Removed)
        {
            var item = _scaletta.Find(relative);
            if (item is null)
                continue;

            Log.Information("File tolto dalla cartella contenuti: {Path}", relative);
            if (_scaletta.Remove(item, live))
            {
                DeleteLocalCopy(item);
                ItemRemoved?.Invoke(item);
            }
            else
            {
                item.StatusDetail = "Tolto dalla cartella: sparisce al ritorno al Tappo";
            }
        }

        foreach (var rename in changes.Renamed)
            ApplyRename(rename, live);

        PersistRequested?.Invoke();
    }

    private void ApplyRename(SourceRename rename, MediaItem? live)
    {
        var old = _scaletta.Find(rename.OldPath);
        if (old is null)
        {
            var created = Create(rename.New.RelativePath);
            _scaletta.Insert(created);
            if (created.CopyState != CopyState.Unsupported)
                Enqueue(created);
            return;
        }

        Log.Information("File rinominato/spostato nella cartella contenuti: {Old} -> {New}", rename.OldPath, rename.New.RelativePath);

        var item = Create(rename.New.RelativePath);
        item.Speaker = old.Speaker;
        item.Volume = old.Volume;
        item.VideoEnd = old.VideoEnd;
        item.Excluded = old.Excluded;
        if (old.SessionEdited)
        {
            item.Session = old.Session;
            item.SessionEdited = true;
        }

        var index = _scaletta.Items.IndexOf(old);
        if (ReferenceEquals(old, live))
        {
            // Il vecchio resta in onda finché serve; il nuovo si copia a parte.
            old.RemovedFromSource = true;
            old.StatusDetail = "Spostato nella cartella: sparisce al ritorno al Tappo";
            _scaletta.Items.Insert(index + 1, item);
            Enqueue(item);
        }
        else
        {
            _scaletta.Items[index] = item;
            if (!TryMoveLocalCopy(old, item))
                Enqueue(item);

            ItemReplaced?.Invoke(old, item);
        }
    }

    /// <summary>Rinomina la copia locale invece di ricopiarla (un video da 4 GB non si ricopia per una rinomina).</summary>
    private bool TryMoveLocalCopy(MediaItem old, MediaItem item)
    {
        try
        {
            if (!HasValidLocalCopy(old) || item.CopyState == CopyState.Unsupported)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
            File.Move(old.Path, item.Path, overwrite: true);

            item.SourceSize = old.SourceSize;
            item.SourceMtimeUtc = old.SourceMtimeUtc;
            item.CopyState = CopyState.Ready;
            ItemReady?.Invoke(item, false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Impossibile rinominare la copia locale {Old}: la ricopio", old.Path);
            return false;
        }
    }

    private MediaItem Create(string relativePath)
    {
        var path = Path.Combine(_cacheRoot, relativePath);
        var kind = MediaKindDetector.FromPath(path);
        var item = new MediaItem(path, kind)
        {
            RelativePath = relativePath,
            Session = Scaletta.SessionFromRelativePath(relativePath)
        };

        if (kind == MediaKind.Unknown)
        {
            item.CopyState = CopyState.Unsupported;
            item.Preflight = PreflightStatus.Error;
            item.StatusDetail = "Tipo di file non supportato";
            item.PreflightSummary = "Tipo di file non supportato dalla regia";
        }
        else
        {
            item.CopyState = CopyState.Copying;
        }

        return item;
    }

    private void DeferUpdate(MediaItem item)
    {
        item.PendingUpdate = true;
        item.StatusDetail = "Aggiornamento in attesa: il file è in onda";
        Log.Information("Aggiornamento di {Path} rimandato: è in onda", item.RelativePath);
    }

    private void Enqueue(MediaItem item)
    {
        if (_queue is null || !_queued.Add(item))
            return;

        Interlocked.Increment(ref _active);
        _queue.Writer.TryWrite(item);
    }

    /// <summary>
    /// Da chiamare quando cambia lo stato dell'onda o il file in onda (sul thread UI): esegue le rimozioni e gli
    /// aggiornamenti che erano stati rimandati perché il file era in onda.
    /// </summary>
    public void OnWaveChanged()
    {
        var live = _live();

        var gone = _scaletta.FlushPending(live);
        foreach (var item in gone)
        {
            DeleteLocalCopy(item);
            ItemRemoved?.Invoke(item);
        }

        foreach (var item in _scaletta.PendingUpdates(live))
        {
            item.PendingUpdate = false;
            item.StatusDetail = "";
            Enqueue(item);
        }

        if (gone.Count > 0)
            PersistRequested?.Invoke();
    }

    /// <summary>Per i test: attende che la coda delle copie si svuoti.</summary>
    public async Task WaitIdleAsync(TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref _active) > 0)
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException("La coda delle copie non si è svuotata");

            await Task.Delay(10);
        }
    }

    private async Task CopyWorkerAsync(Channel<MediaItem> queue, CancellationToken token)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await ProcessAsync(item, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Errore imprevisto nella copia di {Path}", item.RelativePath);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Riconfigurazione o chiusura.
        }
    }

    private async Task ProcessAsync(MediaItem item, CancellationToken token)
    {
        // Da qui il file può essere rimesso in coda se cambia di nuovo durante la copia.
        _queued.Remove(item);

        if (!_scaletta.Items.Contains(item) || item.RemovedFromSource)
            return;

        // Nei momenti critici (caricamento, dissolvenze) non si fa I/O pesante.
        while (_isCritical())
            await Task.Delay(250, token);

        if (ReferenceEquals(_live(), item))
        {
            DeferUpdate(item);
            return;
        }

        var wasReady = item.CopyState == CopyState.Ready;
        var source = Path.Combine(_folder, item.RelativePath);
        string? part = null;

        try
        {
            // Anche solo leggere le informazioni di un file di rete può bloccarsi: mai dal thread UI.
            var (exists, size, mtime) = await Task.Run(() => Stat(source), token);
            if (!exists)
                throw new FileNotFoundException("File non più presente nella cartella", source);

            if (wasReady)
                item.StatusDetail = "Aggiornamento in corso...";
            else
            {
                item.CopyState = CopyState.Copying;
                item.CopyProgress = 0;
                item.StatusDetail = "";
            }

            var context = SynchronizationContext.Current;
            void Report(int percent)
            {
                if (wasReady)
                    return;

                if (context is null)
                    item.CopyProgress = percent;
                else
                    context.Post(_ => item.CopyProgress = percent, null);
            }

            part = await Task.Run(() => FileCopier.CopyToPartAsync(source, item.Path, Report, token), token);

            // Se il file è cambiato mentre lo copiavo la copia è incompleta: si rifà al prossimo giro.
            var (_, sizeAfter, mtimeAfter) = await Task.Run(() => Stat(source), token);
            if (sizeAfter != size || mtimeAfter != mtime)
                throw new IOException("Il file è cambiato durante la copia");

            // Se nel frattempo è andato in onda (o si sta caricando), la copia nuova NON sostituisce quella in uso.
            while (_isCritical())
                await Task.Delay(250, token);

            if (ReferenceEquals(_live(), item))
            {
                FileCopier.TryDelete(part);
                DeferUpdate(item);
                return;
            }

            FileCopier.Commit(part, item.Path);
            part = null;

            item.SourceSize = size;
            item.SourceMtimeUtc = mtime;
            item.CopyProgress = 100;
            item.CopyState = CopyState.Ready;
            item.PendingUpdate = false;
            item.StatusDetail = "";
            if (wasReady)
                item.IsUpdated = true;

            Log.Information("Copia locale {Kind}: {Path} ({Size} byte)", wasReady ? "aggiornata" : "completata", item.RelativePath, size);
            ItemReady?.Invoke(item, wasReady);
            PersistRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            if (part is not null)
                FileCopier.TryDelete(part);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (part is not null)
                FileCopier.TryDelete(part);

            var detail = (wasReady ? "Aggiornamento non riuscito: " : "Copia non riuscita: ") + ex.Message;
            if (item.StatusDetail != detail)
                Log.Warning(ex, "Copia di {Path} non riuscita", item.RelativePath);

            item.StatusDetail = detail;
            if (!wasReady)
                item.CopyState = CopyState.Failed;

            // Un file sparito lo toglie la scansione; altrimenti si riprova al giro successivo.
            if (ex is not FileNotFoundException and not DirectoryNotFoundException)
                _tracker.Forget(item.RelativePath);
        }
    }

    private static (bool Exists, long Size, DateTime MtimeUtc) Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (true, info.Length, info.LastWriteTimeUtc) : (false, 0, default);
    }

    private static void DeleteLocalCopy(MediaItem item)
    {
        if (!string.IsNullOrEmpty(item.Path))
            FileCopier.TryDelete(item.Path);
    }

    /// <summary>
    /// "Aggiungi file": copia i file DENTRO la cartella collegata (nella radice, con nome libero); poi li trova la
    /// scansione. Una sola fonte di verità. Restituisce i messaggi di errore (vuota se tutto bene).
    /// </summary>
    public async Task<IReadOnlyList<string>> CopyIntoFolderAsync(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        if (_folder.Length == 0)
        {
            errors.Add("Cartella contenuti non configurata: impostala da Impostazioni.");
            return errors;
        }

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var fullFolder = Path.GetFullPath(_folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(path).StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase))
                    continue; // è già nella cartella

                var name = FileCopier.UniqueName(_folder, Path.GetFileName(path));
                var destination = Path.Combine(_folder, name);
                var part = await Task.Run(() => FileCopier.CopyToPartAsync(path, destination, null, CancellationToken.None));
                FileCopier.Commit(part, destination);
                Log.Information("File copiato nella cartella contenuti: {Source} -> {Destination}", path, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Impossibile copiare {Path} nella cartella contenuti", path);
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return errors;
    }
}
