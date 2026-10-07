using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Regia.Core.Media;
using Regia.Core.Settings;
using Regia.Core.Show;
using Regia.Output.Preflight;
using Serilog;

namespace Regia.App.Services;

/// <summary>
/// Lo show corrente: file JSON con autosave (debounce 500 ms), scaletta, cartella collegata con copia locale e
/// pre-flight. Tutto vive sul thread UI; l'I/O pesante è nei servizi (<see cref="SourceFolderSync"/>,
/// <see cref="PreflightService"/>). Il file show contiene anche le impostazioni dell'evento.
/// </summary>
public sealed class ShowController : IDisposable
{
    /// <summary>Proprietà della voce che finiscono nel file show: le altre (copia, pre-flight) non fanno salvare.</summary>
    private static readonly HashSet<string> PersistedProperties =
    [
        nameof(MediaItem.Session), nameof(MediaItem.SessionEdited), nameof(MediaItem.Speaker),
        nameof(MediaItem.Volume), nameof(MediaItem.VideoEnd), nameof(MediaItem.Excluded)
    ];

    private readonly ShowStore _store;
    private readonly DispatcherTimer _saveTimer;
    private readonly PreflightService _preflight;
    private ShowDocument _document;
    private MediaItem? _selected;
    private bool _loading;

    public ShowController(ShowStore store, ShowDocument document, Scaletta scaletta, SourceFolderSync sync, PreflightService preflight)
    {
        _store = store;
        _document = document;
        _preflight = preflight;
        Scaletta = scaletta;
        Sync = sync;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveNow();

        Scaletta.Items.CollectionChanged += OnItemsChanged;
        Sync.ItemReady += (item, _) => _preflight.Enqueue(item);
        Sync.PersistRequested += ScheduleSave;
    }

    public Scaletta Scaletta { get; }

    public SourceFolderSync Sync { get; }

    public AppSettings Settings => _document.Settings;

    public string EventId => _document.EventId;

    /// <summary>La voce da selezionare all'avvio (quella dell'ultima volta), se c'è ancora.</summary>
    public MediaItem? RestoredSelection { get; private set; }

    /// <summary>Cartella della copia locale dell'evento: <c>cache\&lt;EventId&gt;</c>.</summary>
    public string CacheRoot => Path.Combine(CacheBase, _document.EventId);

    private static string CacheBase => Path.Combine(SettingsStore.DefaultDirectory, "cache");

    private string ThumbsDirectory => Path.Combine(CacheRoot, ".thumbs");

    /// <summary>Salvataggio non riuscito (disco pieno, file bloccato...): da mostrare all'operatore.</summary>
    public event Action<string>? SaveFailed;

    /// <summary>Impostazioni sostituite (nuovo evento): vanno riapplicate a monitor, Tappo, audio.</summary>
    public event Action<AppSettings>? SettingsReplaced;

    /// <summary>Carica la scaletta salvata e avvia cartella collegata e pre-flight.</summary>
    public void Start()
    {
        CleanupOrphanCaches();
        LoadFromDocument();
    }

    private void LoadFromDocument()
    {
        _loading = true;
        try
        {
            Scaletta.Load(_document.Items, CacheRoot);
        }
        finally
        {
            _loading = false;
        }

        foreach (var item in Scaletta.Items.Where(i => !i.IsFixed))
            Hook(item);

        RestoredSelection = _document.SelectedPath is { } path ? Scaletta.Find(path) : null;

        Directory.CreateDirectory(CacheRoot);
        _preflight.Configure(ThumbsDirectory);
        Sync.Configure(_document.Settings.SourceFolder, CacheRoot);
        Log.Information("Show caricato: {Count} voci, cartella contenuti '{Folder}', cache {Cache}",
            _document.Items.Count, _document.Settings.SourceFolder, CacheRoot);
    }

    /// <summary>La voce selezionata: si ricorda nel file show.</summary>
    public void NoteSelection(MediaItem? item)
    {
        var path = item is { IsFixed: false } ? item.RelativePath : null;
        _selected = item;
        if (path != _document.SelectedPath)
        {
            _document = _document with { SelectedPath = path };
            ScheduleSave();
        }
    }

    /// <summary>Nuove impostazioni dell'evento (da Impostazioni). Se cambia la cartella contenuti si riavvia la sincronizzazione.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        var folderChanged = !string.Equals(_document.Settings.SourceFolder, settings.SourceFolder, StringComparison.OrdinalIgnoreCase);
        _document = _document with { Settings = settings.Normalize() };
        ScheduleSave();

        if (folderChanged)
        {
            Log.Information("Cartella contenuti cambiata: {Folder}", settings.SourceFolder);
            Sync.Configure(_document.Settings.SourceFolder, CacheRoot);
        }
    }

    /// <summary>Nuovo evento: archivia lo show, azzera scaletta e impostazioni, cancella la vecchia cache.</summary>
    public void NewEvent()
    {
        Sync.Stop();
        _preflight.Stop();
        SaveNow();

        var oldCache = CacheRoot;
        var archived = _store.Archive(DateTime.Now);
        Log.Information("Nuovo evento: show precedente archiviato in {Archive}", archived ?? "(nessuno)");

        _document = new ShowDocument().Normalize();
        _selected = null;

        _loading = true;
        try
        {
            foreach (var item in Scaletta.Items.Where(i => !i.IsFixed).ToList())
                Unhook(item);

            Scaletta.Load([], CacheRoot);
        }
        finally
        {
            _loading = false;
        }

        RestoredSelection = null;
        Directory.CreateDirectory(CacheRoot);
        _preflight.Configure(ThumbsDirectory);
        Sync.Configure("", CacheRoot);
        SaveNow();
        SettingsReplaced?.Invoke(_document.Settings);

        _ = Task.Run(() => TryDeleteDirectory(oldCache));
    }

    public void ScheduleSave()
    {
        if (_loading)
            return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();

        try
        {
            _document = _document with
            {
                Items = Scaletta.ToDtos(),
                SelectedPath = _selected is { IsFixed: false } ? _selected.RelativePath : _document.SelectedPath
            };
            _store.Save(_document);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Impossibile salvare lo show");
            SaveFailed?.Invoke("Impossibile salvare lo show: " + ex.Message);
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<MediaItem>())
                Unhook(item);
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<MediaItem>().Where(i => !i.IsFixed))
                Hook(item);
        }

        ScheduleSave();
    }

    private void Hook(MediaItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        item.PropertyChanged += OnItemPropertyChanged;
    }

    private void Unhook(MediaItem item) => item.PropertyChanged -= OnItemPropertyChanged;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && PersistedProperties.Contains(e.PropertyName))
            ScheduleSave();
    }

    /// <summary>Le cache di eventi che non sono quello corrente (crash, vecchi show) non servono più.</summary>
    private void CleanupOrphanCaches()
    {
        var current = _document.EventId;
        var baseDir = CacheBase;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(baseDir))
                    return;

                foreach (var dir in Directory.EnumerateDirectories(baseDir))
                {
                    if (!string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase))
                        TryDeleteDirectory(dir);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pulizia delle vecchie cache non riuscita");
            }
        });
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Impossibile cancellare la cache {Path}", path);
        }
    }

    public void Dispose()
    {
        SaveNow();
        Sync.Dispose();
        _preflight.Dispose();
    }
}
