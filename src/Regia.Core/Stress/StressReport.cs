using System.Globalization;
using System.Text;

namespace Regia.Core.Stress;

public enum StressOutcome
{
    /// <summary>Il ciclo è andato come previsto.</summary>
    Ok,

    /// <summary>Un passo non è arrivato allo stato atteso entro il tempo massimo.</summary>
    Failed,

    /// <summary>Guasto iniettato: la regia è tornata al Tappo/Errore e ha ripreso a funzionare, come previsto.</summary>
    FaultRecovered
}

/// <summary>Misure prese alla fine di un ciclo.</summary>
public sealed record StressSample(
    int Cycle,
    string File,
    string Kind,
    StressFault Fault,
    StressOutcome Outcome,
    int LoadMs,
    string FinalState,
    double PrivateMemoryMb,
    int Handles,
    int Threads,
    int GdiObjects,
    int UserObjects,
    double CpuPercent,
    int ChildProcesses,
    string Note);

public sealed record StressSummary(
    int Cycles,
    int Failures,
    int FaultsInjected,
    int LoadMinMs,
    double LoadAvgMs,
    int LoadMaxMs,
    double MemoryGrowthMb,
    int HandleGrowth,
    int GdiGrowth,
    bool Interrupted)
{
    /// <summary>Crescita oltre queste soglie (primo → ultimo decile): sospetto di perdita.</summary>
    public const double MemoryLeakSuspectMb = 150;

    public const int HandleLeakSuspect = 200;

    public bool LooksLeaky => MemoryGrowthMb > MemoryLeakSuspectMb || HandleGrowth > HandleLeakSuspect || GdiGrowth > 100;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Cicli eseguiti: {Cycles}{(Interrupted ? " (interrotto dall'operatore)" : "")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Falliti: {Failures}   Guasti iniettati e superati: {FaultsInjected}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tempo GO → in onda: min {LoadMinMs} ms, medio {LoadAvgMs:F0} ms, max {LoadMaxMs} ms");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Crescita primo → ultimo decile: memoria {MemoryGrowthMb:+0.0;-0.0;0} MB, handle {HandleGrowth:+0;-0;0}, oggetti GDI {GdiGrowth:+0;-0;0}");
        sb.Append(LooksLeaky ? "ATTENZIONE: crescita sospetta (possibile perdita di risorse)." : "Nessuna crescita sospetta.");
        return sb.ToString();
    }
}

/// <summary>Raccoglie le misure dello stress test, le scrive in CSV e ne ricava il riepilogo.</summary>
public sealed class StressReport
{
    private readonly List<StressSample> _samples = [];

    public IReadOnlyList<StressSample> Samples => _samples;

    public void Add(StressSample sample) => _samples.Add(sample);

    public StressSummary Summarize(bool interrupted = false)
    {
        if (_samples.Count == 0)
            return new StressSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, interrupted);

        var loads = _samples.Where(s => s.LoadMs > 0).Select(s => s.LoadMs).ToList();

        // Confronto primo e ultimo decile: la crescita di un solo valore sarebbe rumore.
        var decile = Math.Max(1, _samples.Count / 10);
        var first = _samples.Take(decile).ToList();
        var last = _samples.TakeLast(decile).ToList();

        return new StressSummary(
            _samples.Count,
            _samples.Count(s => s.Outcome == StressOutcome.Failed),
            _samples.Count(s => s.Fault != StressFault.None && s.Outcome == StressOutcome.FaultRecovered),
            loads.Count == 0 ? 0 : loads.Min(),
            loads.Count == 0 ? 0 : loads.Average(),
            loads.Count == 0 ? 0 : loads.Max(),
            last.Average(s => s.PrivateMemoryMb) - first.Average(s => s.PrivateMemoryMb),
            (int)Math.Round(last.Average(s => s.Handles) - first.Average(s => s.Handles)),
            (int)Math.Round(last.Average(s => s.GdiObjects) - first.Average(s => s.GdiObjects)),
            interrupted);
    }

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ciclo;file;tipo;guasto;esito;ms_go_onda;stato_finale;memoria_mb;handle;thread;gdi;user;cpu_percento;processi_figli;note");

        foreach (var s in _samples)
        {
            sb.Append(s.Cycle.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(Escape(s.File)).Append(';')
                .Append(s.Kind).Append(';')
                .Append(s.Fault).Append(';')
                .Append(s.Outcome).Append(';')
                .Append(s.LoadMs.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(s.FinalState).Append(';')
                .Append(s.PrivateMemoryMb.ToString("F1", CultureInfo.InvariantCulture)).Append(';')
                .Append(s.Handles.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(s.Threads.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(s.GdiObjects.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(s.UserObjects.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(s.CpuPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(';')
                .Append(s.ChildProcesses.ToString(CultureInfo.InvariantCulture)).Append(';')
                .AppendLine(Escape(s.Note));
        }

        return sb.ToString();
    }

    /// <summary>Il separatore è ';': i campi di testo non devono contenerlo né andare a capo.</summary>
    private static string Escape(string text) =>
        text.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
}
