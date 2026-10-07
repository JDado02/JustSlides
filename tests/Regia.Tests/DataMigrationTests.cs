using Regia.Core.Settings;

namespace Regia.Tests;

public sealed class DataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "justslides-migr-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_root, "Regia");

    private string Fresh => Path.Combine(_root, "JustSlides");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Pulizia di una cartella temporanea: se non va, pazienza.
        }
    }

    private void Write(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void MakeLegacy()
    {
        Write(Legacy, "show.json", "{\"show\":1}");
        Write(Legacy, "settings.json", "{\"settings\":1}");
        Write(Legacy, "keys.json", "{\"keys\":1}");
        Write(Legacy, @"shows\evento-1.json", "{\"old\":1}");
        Write(Legacy, @"cache\abc\video.mp4", "VIDEO");
        Write(Legacy, @"logs\regia-20261007.log", "LOG");
        Write(Legacy, "ppt-processes.json", "[]");
    }

    [Fact]
    public void SenzaVecchiaCartella_NienteDaFare()
    {
        var result = DataMigration.Run(Legacy, Fresh);

        Assert.Equal(MigrationOutcome.NotNeeded, result.Outcome);
        Assert.False(Directory.Exists(Fresh));
    }

    [Fact]
    public void Migra_ShowImpostazioniTastiEArchivi_ELaVecchiaCartellaResta()
    {
        MakeLegacy();

        var result = DataMigration.Run(Legacy, Fresh);

        Assert.Equal(MigrationOutcome.Migrated, result.Outcome);
        Assert.Equal("{\"show\":1}", File.ReadAllText(Path.Combine(Fresh, "show.json")));
        Assert.Equal("{\"settings\":1}", File.ReadAllText(Path.Combine(Fresh, "settings.json")));
        Assert.Equal("{\"keys\":1}", File.ReadAllText(Path.Combine(Fresh, "keys.json")));
        Assert.Equal("{\"old\":1}", File.ReadAllText(Path.Combine(Fresh, "shows", "evento-1.json")));
        Assert.True(File.Exists(Path.Combine(Fresh, DataMigration.MarkerFileName)));

        // Gli originali restano (tranne la cache, spostata).
        Assert.True(File.Exists(Path.Combine(Legacy, "show.json")));
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Legacy, "keys.json")));
        Assert.True(File.Exists(Path.Combine(Legacy, "shows", "evento-1.json")));
    }

    [Fact]
    public void LaCacheSiSposta_ELeCopieLocaliSegnuonoLoShow()
    {
        MakeLegacy();

        DataMigration.Run(Legacy, Fresh);

        Assert.Equal("VIDEO", File.ReadAllText(Path.Combine(Fresh, "cache", "abc", "video.mp4")));
        Assert.False(Directory.Exists(Path.Combine(Legacy, "cache")));
    }

    [Fact]
    public void LogEFileDeiProcessi_NonSiPortanoDietro()
    {
        MakeLegacy();

        DataMigration.Run(Legacy, Fresh);

        Assert.False(Directory.Exists(Path.Combine(Fresh, "logs")));
        Assert.False(File.Exists(Path.Combine(Fresh, "ppt-processes.json")));
    }

    [Fact]
    public void SecondaEsecuzione_NonFaNienteENonSovrascrive()
    {
        MakeLegacy();
        DataMigration.Run(Legacy, Fresh);

        // L'utente continua a lavorare nella nuova cartella; la vecchia cambia (altra versione) ma non deve rientrare.
        File.WriteAllText(Path.Combine(Fresh, "show.json"), "{\"nuovo\":1}");
        File.WriteAllText(Path.Combine(Legacy, "show.json"), "{\"vecchio-modificato\":1}");

        var result = DataMigration.Run(Legacy, Fresh);

        Assert.Equal(MigrationOutcome.AlreadyDone, result.Outcome);
        Assert.Equal("{\"nuovo\":1}", File.ReadAllText(Path.Combine(Fresh, "show.json")));
    }

    [Fact]
    public void ShowNuovoGiaPresente_NonSiSovrascriveMai()
    {
        MakeLegacy();
        Write(Fresh, "show.json", "{\"gia-qui\":1}");

        var result = DataMigration.Run(Legacy, Fresh);

        Assert.Equal(MigrationOutcome.NotNeeded, result.Outcome);
        Assert.Equal("{\"gia-qui\":1}", File.ReadAllText(Path.Combine(Fresh, "show.json")));
        Assert.True(Directory.Exists(Path.Combine(Legacy, "cache")));
    }

    [Fact]
    public void FileInUso_FallisceSenzaDanni_ESiRitentaDopo()
    {
        MakeLegacy();

        // Un altro processo tiene aperto lo show (lo si copia per ultimo): niente segnaposto, la copia riparte al prossimo avvio.
        using (new FileStream(Path.Combine(Legacy, "show.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = DataMigration.Run(Legacy, Fresh);

            Assert.Equal(MigrationOutcome.Failed, failed.Outcome);
            Assert.False(File.Exists(Path.Combine(Fresh, DataMigration.MarkerFileName)));
            Assert.False(File.Exists(Path.Combine(Fresh, "show.json")));
        }

        var retried = DataMigration.Run(Legacy, Fresh);

        Assert.Equal(MigrationOutcome.Migrated, retried.Outcome);
        Assert.Equal("{\"show\":1}", File.ReadAllText(Path.Combine(Fresh, "show.json")));
        Assert.Equal("{\"keys\":1}", File.ReadAllText(Path.Combine(Fresh, "keys.json")));
        Assert.True(File.Exists(Path.Combine(Fresh, DataMigration.MarkerFileName)));
    }

    [Fact]
    public void LaCartellaDatiPredefinita_EJustSlides()
    {
        // Senza la variabile di prova la cartella dati porta il nome del prodotto.
        var previous = Environment.GetEnvironmentVariable(DataMigration.DataDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(DataMigration.DataDirVariable, null);

            Assert.EndsWith(Path.DirectorySeparatorChar + "JustSlides", SettingsStore.DefaultDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataMigration.DataDirVariable, previous);
        }
    }

    [Fact]
    public void ConCartellaDatiDiProva_ELaVecchiaNonIndicata_NonMigraNulla()
    {
        var previousData = Environment.GetEnvironmentVariable(DataMigration.DataDirVariable);
        var previousLegacy = Environment.GetEnvironmentVariable(DataMigration.LegacyDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(DataMigration.DataDirVariable, Fresh);
            Environment.SetEnvironmentVariable(DataMigration.LegacyDirVariable, null);

            var result = DataMigration.RunForCurrentUser();

            Assert.Equal(MigrationOutcome.NotNeeded, result.Outcome);
            Assert.False(Directory.Exists(Fresh));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataMigration.DataDirVariable, previousData);
            Environment.SetEnvironmentVariable(DataMigration.LegacyDirVariable, previousLegacy);
        }
    }
}
