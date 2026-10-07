using System.Text.Json;
using System.Text.Json.Serialization;
using Regia.Core.Settings;
using Serilog;

namespace Regia.Core.Show;

/// <summary>
/// Lettura/scrittura del file show JSON, con scrittura atomica. Mai eccezioni in lettura: file assente o corrotto
/// danno uno show vuoto (il corrotto viene copiato in .bak). Se manca lo show ma esiste il vecchio
/// <c>settings.json</c>, le impostazioni dell'evento passano nel nuovo show (migrazione da M5).
/// </summary>
public sealed class ShowStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly string? _legacySettingsPath;

    public ShowStore(string path, string? legacySettingsPath = null)
    {
        _path = path;
        _legacySettingsPath = legacySettingsPath;
    }

    public string Path => _path;

    public static string DefaultPath => System.IO.Path.Combine(SettingsStore.DefaultDirectory, "show.json");

    /// <summary>Cartella dove finiscono gli show archiviati da "Nuovo evento".</summary>
    public string ArchiveDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path) ?? "", "shows");

    public ShowDocument Load()
    {
        if (!File.Exists(_path))
            return Migrate();

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<ShowDocument>(json, JsonOptions);
            return (loaded ?? new ShowDocument()).Normalize();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "File show corrotto ({Path}): parto da uno show vuoto", _path);
            TryBackupCorruptFile();
            return new ShowDocument().Normalize();
        }
    }

    public void Save(ShowDocument show)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(show.Normalize(), JsonOptions));

        if (File.Exists(_path))
            File.Replace(tmp, _path, null);
        else
            File.Move(tmp, _path);
    }

    /// <summary>Sposta lo show corrente in <c>shows\show-AAAAMMGG-hhmmss.json</c> ("Nuovo evento"). Restituisce il nuovo percorso o null.</summary>
    public string? Archive(DateTime now)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            Directory.CreateDirectory(ArchiveDirectory);
            var target = System.IO.Path.Combine(ArchiveDirectory, $"show-{now:yyyyMMdd-HHmmss}.json");
            File.Move(_path, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile archiviare lo show {Path}", _path);
            return null;
        }
    }

    private ShowDocument Migrate()
    {
        if (_legacySettingsPath is null || !File.Exists(_legacySettingsPath))
            return new ShowDocument().Normalize();

        var settings = new SettingsStore(_legacySettingsPath).Load();
        Log.Information("Migrazione: impostazioni evento portate dal vecchio settings.json nel nuovo show");
        return new ShowDocument { Settings = settings }.Normalize();
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile salvare la copia .bak del file show");
        }
    }
}
