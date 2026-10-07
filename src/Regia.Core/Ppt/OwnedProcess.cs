using System.Text.Json;
using Serilog;

namespace Regia.Core.Ppt;

/// <summary>Un processo avviato da noi (PptHost o il suo POWERPNT.EXE), salvato su file per ripulire gli orfani.</summary>
public sealed record OwnedProcess(int Pid, DateTime StartTimeUtc, string Role)
{
    /// <summary>Tolleranza sull'ora di avvio: l'orologio dei processi ha granularità e arrotondamenti diversi.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Vero solo se il processo vivo con questo PID è proprio quello registrato: PID riusati da Windows o processi Office
    /// dell'utente non coincidono per ora di avvio e non vanno mai terminati.
    /// </summary>
    public bool IsSameProcess(int pid, DateTime startTimeUtc) =>
        pid == Pid && (startTimeUtc - StartTimeUtc).Duration() <= StartTimeTolerance;
}

/// <summary>File JSON dei processi avviati dalla regia. Mai eccezioni verso l'alto: un file illeggibile vale "nessun orfano".</summary>
public sealed class OwnedProcessStore(string path)
{
    public static string DefaultPath => Path.Combine(Settings.SettingsStore.DefaultDirectory, "ppt-processes.json");

    public IReadOnlyList<OwnedProcess> Load()
    {
        try
        {
            if (!File.Exists(path))
                return [];

            return JsonSerializer.Deserialize<List<OwnedProcess>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "File dei processi PowerPoint illeggibile: {Path}", path);
            return [];
        }
    }

    public void Save(IEnumerable<OwnedProcess> processes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(processes.ToList()));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile salvare il file dei processi PowerPoint: {Path}", path);
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile cancellare il file dei processi PowerPoint: {Path}", path);
        }
    }
}
