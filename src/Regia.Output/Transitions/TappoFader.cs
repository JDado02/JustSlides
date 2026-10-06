using System.Windows;
using System.Windows.Media.Animation;
using Regia.Output.Tappo;
using Regia.Output.Windows;
using Serilog;

namespace Regia.Output.Transitions;

/// <summary>
/// Dissolvenza del Tappo: l'unica transizione è l'animazione dell'opacità della finestra Tappo.
/// Ogni metodo restituisce true se l'animazione è arrivata in fondo, false se è stata interrotta
/// (da un'altra transizione o dal PANIC).
/// </summary>
public sealed class TappoFader
{
    private readonly TappoWindow _tappo;
    private readonly Func<ITappoSource?> _sourceProvider;
    private TaskCompletionSource<bool>? _pending;

    public TappoFader(TappoWindow tappo, Func<ITappoSource?> sourceProvider)
    {
        _tappo = tappo;
        _sourceProvider = sourceProvider;
    }

    public bool IsAnimating => _pending is not null;

    /// <summary>Messa in onda: Tappo 1 → 0, poi click-through. Il video del Tappo si congela durante la dissolvenza.</summary>
    public async Task<bool> FadeOutAsync(int durationMs, bool hardCut)
    {
        _sourceProvider()?.Freeze();

        var completed = await AnimateAsync(0, hardCut ? 0 : durationMs);
        if (completed)
            _tappo.ClickThrough = true;

        return completed;
    }

    /// <summary>Ritorno al Tappo: toglie il click-through, Tappo 0 → 1, poi il video riprende.</summary>
    public async Task<bool> FadeInAsync(int durationMs, bool hardCut)
    {
        _tappo.ClickThrough = false;

        var completed = await AnimateAsync(1, hardCut ? 0 : durationMs);
        if (completed)
            _sourceProvider()?.Resume();

        return completed;
    }

    /// <summary>PANIC: ferma qualsiasi animazione e porta il Tappo a opacità piena, subito.</summary>
    public void Panic()
    {
        CancelAnimation();
        _tappo.Opacity = 1;
        _tappo.ClickThrough = false;
        _sourceProvider()?.Resume();
        Log.Information("Tappo: ritorno immediato (PANIC)");
    }

    private Task<bool> AnimateAsync(double to, int durationMs)
    {
        CancelAnimation();

        if (durationMs <= 0)
        {
            _tappo.Opacity = to;
            return Task.FromResult(true);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        var animation = new DoubleAnimation(_tappo.Opacity, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        animation.Completed += (_, _) =>
        {
            // Solo se questa animazione è ancora quella corrente.
            if (!ReferenceEquals(_pending, tcs))
                return;

            _pending = null;
            _tappo.BeginAnimation(UIElement.OpacityProperty, null);
            _tappo.Opacity = to;
            tcs.TrySetResult(true);
        };

        _tappo.BeginAnimation(UIElement.OpacityProperty, animation);
        return tcs.Task;
    }

    private void CancelAnimation()
    {
        var pending = _pending;
        _pending = null;

        if (pending is null)
            return;

        // Congelo l'opacità attuale prima di togliere l'animazione, poi segnalo l'interruzione.
        var current = _tappo.Opacity;
        _tappo.BeginAnimation(UIElement.OpacityProperty, null);
        _tappo.Opacity = current;
        pending.TrySetResult(false);
    }
}
