using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Regia.Core.Settings;

/// <summary>Lettura/scrittura del file impostazioni JSON, con scrittura atomica.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    /// <summary>Cartella dati predefinita: %LOCALAPPDATA%\JustSlides (o quella indicata da <see cref="DataMigration.DataDirVariable"/>, solo per prove e sviluppo).</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable(DataMigration.DataDirVariable) is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataMigration.FolderName);

    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>Carica le impostazioni. Mai un'eccezione: se il file manca o è corrotto torna ai default.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(_path))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return (loaded ?? new AppSettings()).Normalize();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "File impostazioni corrotto ({Path}): uso i valori di default", _path);
            TryBackupCorruptFile();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));

        if (File.Exists(_path))
            File.Replace(tmp, _path, null);
        else
            File.Move(tmp, _path);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile salvare la copia .bak del file impostazioni");
        }
    }
}
