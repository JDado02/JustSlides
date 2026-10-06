using Regia.Core.Media;
using Serilog;

namespace Regia.Core.Wave;

/// <summary>
/// Orchestratore dell'onda: esegue GO / Stop / pagina / PANIC pilotando la macchina a stati,
/// il Tappo e il contenuto. Va usato da un solo thread (quello UI): non lancia mai eccezioni
/// verso l'alto, ogni errore porta al Tappo e allo stato Errore.
/// </summary>
public sealed class WaveController
{
    private readonly WaveStateMachine _machine;
    private readonly ITappoTransitions _tappo;
    private readonly IContentPresenterFactory _factory;

    private IContentPresenter? _current;
    private CancellationTokenSource _cts = new();
    private int _generation;

    public WaveController(WaveStateMachine machine, ITappoTransitions tappo, IContentPresenterFactory factory)
    {
        _machine = machine;
        _tappo = tappo;
        _factory = factory;
        _machine.StateChanged += (old, now) => StateChanged?.Invoke(old, now);
    }

    public WaveState State => _machine.State;

    public MediaItem? CurrentItem { get; private set; }

    public PageInfo? Page => _current?.Page;

    public event Action<WaveState, WaveState>? StateChanged;

    public event Action? PageChanged;

    /// <summary>Messaggio per l'operatore quando qualcosa è andato storto.</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>Messa in onda; se qualcosa è già in onda, cambio file passando dal Tappo.</summary>
    public async Task GoAsync(MediaItem item)
    {
        if (!_machine.CanFire(WaveTrigger.Go))
        {
            _machine.Fire(WaveTrigger.Go); // logga il comando ignorato
            return;
        }

        var generation = NewOperation(out var token);
        Log.Information("GO: {Item}", item.DisplayName);

        try
        {
            if (_machine.State == WaveState.InOnda)
            {
                // Cambio file: Tappo 0→1, solo dopo si chiude il vecchio contenuto.
                _machine.Fire(WaveTrigger.Go);
                var covered = await _tappo.CoverAsync();
                if (!covered || generation != _generation)
                    return;

                CloseCurrent();
                _machine.Fire(WaveTrigger.FadeCompleted);
            }

            _machine.Fire(WaveTrigger.Go);
            await LoadAndRevealAsync(item, generation, token);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                Fail("GO", ex);
        }
    }

    /// <summary>Ritorno al Tappo con dissolvenza, poi chiusura del contenuto.</summary>
    public async Task StopAsync()
    {
        if (!_machine.Fire(WaveTrigger.Stop))
            return;

        var generation = NewOperation(out _);
        Log.Information("Ritorno al Tappo: {Item}", CurrentItem?.DisplayName);

        try
        {
            var covered = await _tappo.CoverAsync();
            if (!covered || generation != _generation)
                return;

            CloseCurrent();
            _machine.Fire(WaveTrigger.FadeCompleted);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                Fail("Torna al Tappo", ex);
        }
    }

    public bool Next() => Navigate(p => p.Next(), "Avanti");

    public bool Previous() => Navigate(p => p.Previous(), "Indietro");

    /// <summary>PANIC: Tappo immediato da qualsiasi stato, annulla tutto, chiude il contenuto.</summary>
    public void Panic()
    {
        Log.Warning("PANIC (stato precedente: {State})", _machine.State);
        Abort();
        _machine.Fire(WaveTrigger.Panic);
    }

    /// <summary>Errore durante la proiezione: Tappo immediato, contenuto chiuso, stato Errore.</summary>
    public void Fail(string context, Exception ex)
    {
        Log.Error(ex, "Errore in {Context}: ritorno al Tappo", context);
        Abort();
        _machine.Fire(WaveTrigger.Fail);
        ErrorOccurred?.Invoke($"Errore ({context}): {ex.Message}");
    }

    private bool Navigate(Func<IContentPresenter, bool> move, string name)
    {
        if (!_machine.CanFire(WaveTrigger.Navigate) || _current is null)
        {
            Log.Warning("Comando {Name} ignorato: stato {State}", name, _machine.State);
            return false;
        }

        try
        {
            if (!move(_current))
            {
                Log.Information("Comando {Name} ignorato: nessuna pagina in quella direzione", name);
                return false;
            }

            _machine.Fire(WaveTrigger.Navigate);
            return true;
        }
        catch (Exception ex)
        {
            Fail(name, ex);
            return false;
        }
    }

    private async Task LoadAndRevealAsync(MediaItem item, int generation, CancellationToken token)
    {
        IContentPresenter presenter;
        try
        {
            presenter = _factory.Create(item);
        }
        catch (Exception ex)
        {
            FailLoad(item, ex);
            return;
        }

        _current = presenter;
        CurrentItem = item;
        presenter.PageChanged += OnPresenterPageChanged;

        try
        {
            await presenter.LoadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            presenter.Close();
            return;
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                FailLoad(item, ex);
            else
                presenter.Close();
            return;
        }

        // Annullato (PANIC) mentre caricava: il risultato tardivo si scarta.
        if (generation != _generation)
        {
            presenter.Close();
            return;
        }

        _machine.Fire(WaveTrigger.ContentReady);
        PageChanged?.Invoke();

        var revealed = await _tappo.RevealAsync();
        if (!revealed || generation != _generation)
            return;

        _machine.Fire(WaveTrigger.FadeCompleted);
        Log.Information("In onda: {Item}", item.DisplayName);
    }

    private void FailLoad(MediaItem item, Exception ex)
    {
        Log.Error(ex, "Caricamento fallito: {Item}", item.DisplayName);
        Abort();
        _machine.Fire(WaveTrigger.LoadFailed);
        ErrorOccurred?.Invoke($"Impossibile aprire \"{item.DisplayName}\": {ex.Message}");
    }

    /// <summary>Invalida le operazioni in corso, Tappo pieno subito, contenuto chiuso.</summary>
    private void Abort()
    {
        _generation++;
        _cts.Cancel();

        try
        {
            _tappo.CoverNow();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nel ritorno immediato al Tappo");
        }

        CloseCurrent();
    }

    private int NewOperation(out CancellationToken token)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        token = _cts.Token;
        return ++_generation;
    }

    private void CloseCurrent()
    {
        var presenter = _current;
        _current = null;
        CurrentItem = null;

        if (presenter is null)
            return;

        presenter.PageChanged -= OnPresenterPageChanged;
        try
        {
            presenter.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nella chiusura del contenuto");
        }

        PageChanged?.Invoke();
    }

    private void OnPresenterPageChanged() => PageChanged?.Invoke();
}
