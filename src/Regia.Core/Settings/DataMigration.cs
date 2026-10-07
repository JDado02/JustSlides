namespace Regia.Core.Settings;

public enum MigrationOutcome
{
    /// <summary>Niente da fare: la vecchia cartella non esiste, oppure i dati nuovi ci sono già.</summary>
    NotNeeded,

    /// <summary>Già migrata in un avvio precedente (c'è il file segnaposto).</summary>
    AlreadyDone,

    Migrated,

    /// <summary>Qualcosa non è stato copiato: la migrazione si ritenterà al prossimo avvio.</summary>
    Failed
}

public sealed record MigrationResult(MigrationOutcome Outcome, string Message, IReadOnlyList<string> Items);

/// <summary>
/// Con il nuovo nome il programma usa %LOCALAPPDATA%\JustSlides. Al primo avvio i dati della vecchia cartella %LOCALAPPDATA%\Regia
/// (show corrente con scaletta e impostazioni, tasti, vecchio settings.json, show archiviati, copie locali dei file) passano alla nuova.
/// Impostazioni, tasti, show e show archiviati si COPIANO: la vecchia cartella resta com'è. La cache (copie locali dei file, ricostruibile ma
/// pesante) si SPOSTA con un rinomina, istantaneo: così un evento senza la cartella sorgente raggiungibile ha comunque i suoi file.
/// Log e file dei processi non si portano dietro. È sicura da ripetere: il file segnaposto impedisce una seconda migrazione e lo show si
/// copia per ultimo, così un'interruzione a metà viene ritentata.
/// </summary>
public static class DataMigration
{
    public const string FolderName = "JustSlides";
    public const string LegacyFolderName = "Regia";

    /// <summary>Solo per prove e sviluppo: cartella dati alternativa. Con questa impostata la migrazione parte solo se c'è anche <see cref="LegacyDirVariable"/>.</summary>
    public const string DataDirVariable = "JUSTSLIDES_DATA_DIR";

    /// <summary>Solo per prove: cartella "vecchia" alternativa da cui migrare.</summary>
    public const string LegacyDirVariable = "JUSTSLIDES_LEGACY_DIR";

    public const string MarkerFileName = "migrated-from-regia.txt";

    private static readonly string[] Files = ["keys.json", "keys.json.bak", "settings.json", "settings.json.bak"];

    /// <summary>La migrazione con le cartelle del profilo corrente; non fa nulla se si sta usando una cartella dati di prova senza la vecchia di prova.</summary>
    public static MigrationResult RunForCurrentUser()
    {
        var custom = Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 };
        var legacy = Environment.GetEnvironmentVariable(LegacyDirVariable) is { Length: > 0 } legacyOverride
            ? legacyOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);

        if (custom && Environment.GetEnvironmentVariable(LegacyDirVariable) is not { Length: > 0 })
            return new MigrationResult(MigrationOutcome.NotNeeded, "Cartella dati di prova in uso: nessuna migrazione.", []);

        return Run(legacy, SettingsStore.DefaultDirectory);
    }

    public static MigrationResult Run(string legacyDir, string newDir)
    {
        if (!Directory.Exists(legacyDir))
            return new MigrationResult(MigrationOutcome.NotNeeded, "Nessuna cartella dati precedente.", []);

        if (File.Exists(Path.Combine(newDir, MarkerFileName)))
            return new MigrationResult(MigrationOutcome.AlreadyDone, "Dati già migrati.", []);

        // Dati nuovi già presenti (installazione pulita usata prima): non si sovrascrive mai nulla.
        if (File.Exists(Path.Combine(newDir, "show.json")))
            return new MigrationResult(MigrationOutcome.NotNeeded, "La nuova cartella ha già uno show: nessuna migrazione.", []);

        var done = new List<string>();
        try
        {
            Directory.CreateDirectory(newDir);

            foreach (var name in Files)
                CopyIfExists(legacyDir, newDir, name, done);

            var shows = Path.Combine(legacyDir, "shows");
            if (Directory.Exists(shows))
            {
                CopyDirectory(shows, Path.Combine(newDir, "shows"));
                done.Add("shows\\");
            }

            MoveCache(legacyDir, newDir, done);

            // Lo show per ultimo: finché non c'è, una migrazione interrotta si ritenta.
            CopyIfExists(legacyDir, newDir, "show.json", done);
            CopyIfExists(legacyDir, newDir, "show.json.bak", done);

            File.WriteAllText(Path.Combine(newDir, MarkerFileName),
                $"Dati migrati da {legacyDir} il {DateTime.Now:yyyy-MM-dd HH:mm:ss}.{Environment.NewLine}Elementi: {string.Join(", ", done)}{Environment.NewLine}");

            return new MigrationResult(MigrationOutcome.Migrated, $"Dati migrati da {legacyDir}: {string.Join(", ", done)}.", done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MigrationResult(MigrationOutcome.Failed,
                $"Migrazione dei dati da {legacyDir} non completata ({ex.Message}); si ritenta al prossimo avvio.", done);
        }
    }

    private static void CopyIfExists(string from, string to, string name, List<string> done)
    {
        var source = Path.Combine(from, name);
        var target = Path.Combine(to, name);
        if (!File.Exists(source))
            return;

        // Un tentativo interrotto prima può aver già copiato questo file: lo si lascia (e non si sovrascrive mai ciò che c'è).
        if (!File.Exists(target))
            File.Copy(source, target, overwrite: false);

        done.Add(name);
    }

    /// <summary>Sposta la cache con un rinomina; se non si può (altro volume, cartella in uso) resta dov'è e le copie si rifanno dalla sorgente.</summary>
    private static void MoveCache(string legacyDir, string newDir, List<string> done)
    {
        var source = Path.Combine(legacyDir, "cache");
        var target = Path.Combine(newDir, "cache");
        if (!Directory.Exists(source) || Directory.Exists(target))
            return;

        try
        {
            Directory.Move(source, target);
            done.Add("cache\\ (spostata)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non è un errore: la cache si ricrea dalla cartella contenuti.
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
