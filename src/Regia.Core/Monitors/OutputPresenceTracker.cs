namespace Regia.Core.Monitors;

/// <summary>Cosa è cambiato per il monitor di output dall'ultima valutazione.</summary>
public enum PresenceChange
{
    /// <summary>Niente da fare.</summary>
    None,

    /// <summary>Il monitor di output è sparito (cavo, proiettore spento, matrice che cambia sorgente): agire SUBITO.</summary>
    Lost,

    /// <summary>È ricomparso ed è rimasto stabile abbastanza a lungo: si può riposizionare tutto.</summary>
    Returned,

    /// <summary>C'è ancora ma è cambiato (risoluzione, posizione, DPI): riapplicare il rettangolo.</summary>
    Changed
}

/// <summary>
/// Segue la presenza del monitor di output nel tempo (logica pura, orologio passato da fuori: testabile).
/// La sparizione è immediata (Windows sta già spostando le finestre sul monitor della regia); il ritorno viene confermato
/// solo se resta stabile per <see cref="StableFor"/>, perché una matrice o un proiettore che si riaccendono fanno
/// comparire e sparire il monitor più volte di fila.
/// Vale solo in modalità reale: in simulazione o senza monitor scelto non fa mai nulla.
/// </summary>
public sealed class OutputPresenceTracker
{
    /// <summary>Quanto deve restare presente un monitor tornato prima di riposizionare le finestre.</summary>
    public static readonly TimeSpan StableFor = TimeSpan.FromMilliseconds(1500);

    private bool _initialized;
    private bool _present;
    private MonitorInfo? _geometry;
    private DateTimeOffset? _backSince;

    /// <summary>Il monitor è tornato ma si aspetta ancora che sia stabile: serve una nuova valutazione entro <see cref="StableFor"/>.</summary>
    public bool Pending => _backSince is not null;

    /// <summary>Si dimentica tutto (cambio di impostazioni): la prossima valutazione riparte da zero senza segnalare nulla.</summary>
    public void Reset()
    {
        _initialized = false;
        _present = false;
        _geometry = null;
        _backSince = null;
    }

    /// <param name="wanted">Monitor di output scelto nelle impostazioni (null = nessuno).</param>
    /// <param name="simulation">Modalità simulazione: niente da sorvegliare.</param>
    /// <param name="monitors">Monitor attualmente collegati. Lista vuota = rilevamento fallito: si ignora.</param>
    public PresenceChange Evaluate(MonitorId? wanted, bool simulation, IReadOnlyList<MonitorInfo> monitors, DateTimeOffset now)
    {
        if (simulation || wanted is null)
        {
            Reset();
            return PresenceChange.None;
        }

        // Un errore di rilevamento non è "l'output è sparito": un PANIC a vuoto in onda sarebbe peggio.
        if (monitors.Count == 0)
            return PresenceChange.None;

        // Il monitor primario non è mai un'uscita valida (la regia non si copre): per noi è come assente.
        var found = MonitorMatcher.Find(wanted, monitors) is { IsPrimary: false } m ? m : null;

        if (!_initialized)
        {
            _initialized = true;
            _present = found is not null;
            _geometry = found;
            return PresenceChange.None;
        }

        if (found is null)
        {
            _backSince = null;
            if (!_present)
                return PresenceChange.None;

            _present = false;
            _geometry = null;
            return PresenceChange.Lost;
        }

        if (!_present)
        {
            _backSince ??= now;
            if (now - _backSince < StableFor)
                return PresenceChange.None;

            _present = true;
            _backSince = null;
            _geometry = found;
            return PresenceChange.Returned;
        }

        if (SameGeometry(_geometry, found))
            return PresenceChange.None;

        _geometry = found;
        return PresenceChange.Changed;
    }

    private static bool SameGeometry(MonitorInfo? a, MonitorInfo b) =>
        a is not null && a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.Dpi == b.Dpi;
}
