using System.Windows;
using System.Windows.Controls;

namespace Regia.App;

/// <summary>
/// Dispone due "schede" affiancate (Preview e Program) con la stessa dimensione, sempre.
/// Ogni scheda = riquadro 16:9 + zona info di altezza fissa (<see cref="InfoHeight"/>).
/// La larghezza dipende SOLO dallo spazio disponibile (larghezza e altezza del pannello):
/// mai dal contenuto, dal tipo di file, dallo stato dell'onda o dagli avvisi.
/// Il riquadro 16:9 è la prima riga della scheda (le righe sotto sono la zona info).
/// </summary>
public sealed class MonitorPairPanel : Panel
{
    public static readonly DependencyProperty InfoHeightProperty = DependencyProperty.Register(
        nameof(InfoHeight), typeof(double), typeof(MonitorPairPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(MonitorPairPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Altezza fissa sotto il riquadro 16:9 (titoli, stato, pagine...).</summary>
    public double InfoHeight
    {
        get => (double)GetValue(InfoHeightProperty);
        set => SetValue(InfoHeightProperty, value);
    }

    /// <summary>Spazio tra le due schede.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Dimensione di una scheda per lo spazio dato; larghezza multipla di 16 così l'altezza 16:9 è intera.</summary>
    private Size CardSize(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 0 : available.Width;
        var cardWidth = (width - Gap) / 2;
        if (!double.IsInfinity(available.Height))
            cardWidth = Math.Min(cardWidth, (available.Height - InfoHeight) * 16.0 / 9.0);

        cardWidth = Math.Max(0, Math.Floor(cardWidth / 16.0) * 16.0);
        return new Size(cardWidth, cardWidth / 16.0 * 9.0 + InfoHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var card = CardSize(availableSize);
        foreach (UIElement child in InternalChildren)
            child.Measure(card);

        return new Size(Math.Min(availableSize.Width, card.Width * 2 + Gap), card.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var card = CardSize(finalSize);
        var total = card.Width * 2 + Gap;
        var x = Math.Max(0, (finalSize.Width - total) / 2);

        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, card.Width, card.Height));
            x += card.Width + Gap;
        }

        return finalSize;
    }
}
