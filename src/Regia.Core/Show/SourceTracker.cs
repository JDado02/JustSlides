namespace Regia.Core.Show;

/// <summary>Un file visto nella cartella collegata.</summary>
public sealed record SourceFile(string RelativePath, long Size, DateTime MtimeUtc);

/// <summary>Un file spostato o rinominato nella cartella (stessa dimensione e data).</summary>
public sealed record SourceRename(string OldPath, SourceFile New);

/// <summary>Cambiamenti confermati da una scansione.</summary>
public sealed record ScanChanges(
    IReadOnlyList<SourceFile> Added,
    IReadOnlyList<SourceFile> Modified,
    IReadOnlyList<string> Removed,
    IReadOnlyList<SourceRename> Renamed,
    bool Unreachable)
{
    public static ScanChanges None { get; } = new([], [], [], [], false);

    public static ScanChanges NotReachable { get; } = new([], [], [], [], true);

    public bool IsEmpty => !Unreachable && Added.Count == 0 && Modified.Count == 0 && Removed.Count == 0 && Renamed.Count == 0;
}

/// <summary>
/// Confronta le scansioni successive della cartella collegata (logica pura, nessun I/O) e restituisce solo i
/// cambiamenti CONFERMATI:
/// <list type="bullet">
/// <item>un file nuovo o modificato conta solo se dimensione e data sono uguali in due scansioni di fila
/// (nessuno lo sta ancora copiando o salvando);</item>
/// <item>un file sparito conta come rimosso solo se manca in due scansioni di fila;</item>
/// <item>una scansione fallita (<c>null</c>, cartella irraggiungibile) NON cambia nulla: non svuota mai la lista;</item>
/// <item>rimosso + aggiunto con stessa dimensione e data nello stesso giro = rinomina/spostamento.</item>
/// </list>
/// </summary>
public sealed class SourceTracker
{
    /// <summary>Scansioni consecutive in cui un file deve mancare per essere dato per rimosso.</summary>
    public const int MissingScansToRemove = 2;

    private readonly Dictionary<string, SourceFile> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceFile> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _missing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File già accettati (e copiati) in passato: all'avvio vengono dallo show salvato.</summary>
    public void Seed(IEnumerable<SourceFile> files)
    {
        _known.Clear();
        _candidates.Clear();
        _missing.Clear();

        foreach (var file in files)
            _known[file.RelativePath] = file;
    }

    /// <summary>Dimenticare tutto (cambio di cartella o nuovo evento).</summary>
    public void Reset() => Seed([]);

    public bool IsKnown(string relativePath) => _known.ContainsKey(relativePath);

    /// <summary>Un file noto non è più valido (copia fallita o mancante): alla prossima scansione sarà trattato come nuovo.</summary>
    public void Forget(string relativePath)
    {
        _known.Remove(relativePath);
        _candidates.Remove(relativePath);
        _missing.Remove(relativePath);
    }

    /// <summary>Elabora una scansione (<c>null</c> = cartella non raggiungibile).</summary>
    public ScanChanges Apply(IReadOnlyList<SourceFile>? scan)
    {
        if (scan is null)
            return ScanChanges.NotReachable;

        var present = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in scan)
        {
            if (!IsIgnored(file.RelativePath))
                present[file.RelativePath] = file;
        }

        var added = new List<SourceFile>();
        var modified = new List<SourceFile>();

        foreach (var (path, file) in present)
        {
            _missing.Remove(path);

            if (_known.TryGetValue(path, out var known))
            {
                if (SameVersion(known, file))
                {
                    _candidates.Remove(path);
                    continue;
                }

                if (_candidates.TryGetValue(path, out var candidate) && SameVersion(candidate, file))
                {
                    _known[path] = file;
                    _candidates.Remove(path);
                    modified.Add(file);
                }
                else
                {
                    _candidates[path] = file;
                }
            }
            else if (_candidates.TryGetValue(path, out var candidate) && SameVersion(candidate, file))
            {
                _known[path] = file;
                _candidates.Remove(path);
                added.Add(file);
            }
            else
            {
                _candidates[path] = file;
            }
        }

        // I candidati spariti prima di stabilizzarsi si dimenticano.
        foreach (var path in _candidates.Keys.Where(p => !present.ContainsKey(p)).ToList())
            _candidates.Remove(path);

        var removed = new List<SourceFile>();
        foreach (var path in _known.Keys.Where(p => !present.ContainsKey(p)).ToList())
        {
            var count = _missing.GetValueOrDefault(path) + 1;
            if (count >= MissingScansToRemove)
            {
                removed.Add(_known[path]);
                _known.Remove(path);
                _missing.Remove(path);
            }
            else
            {
                _missing[path] = count;
            }
        }

        return DetectRenames(added, modified, removed);
    }

    /// <summary>Rimosso + aggiunto con stessa dimensione e data nello stesso giro = rinomina o spostamento.</summary>
    private static ScanChanges DetectRenames(List<SourceFile> added, List<SourceFile> modified, List<SourceFile> removed)
    {
        var renamed = new List<SourceRename>();

        foreach (var file in added.ToList())
        {
            var old = removed.FirstOrDefault(r => SameVersion(r, file));
            if (old is null)
                continue;

            removed.Remove(old);
            added.Remove(file);
            renamed.Add(new SourceRename(old.RelativePath, file));
        }

        return new ScanChanges(added, modified, removed.Select(r => r.RelativePath).ToList(), renamed, false);
    }

    private static bool SameVersion(SourceFile a, SourceFile b) => a.Size == b.Size && a.MtimeUtc == b.MtimeUtc;

    /// <summary>File che non sono contenuti: lock di Office, miniature e file temporanei di sistema.</summary>
    public static bool IsIgnored(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith('.'))
            return true;

        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(name);
        return ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase);
    }
}
