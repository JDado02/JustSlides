using System.Windows;
using System.Windows.Controls;

namespace Regia.App;

/// <summary>
/// Dispone Preview e Program affiancati con la stessa dimensione, sempre. Quattro figli, in quest'ordine:
/// riquadro sinistro, riquadro destro, info sinistra, info destra.
/// I due riquadri sono 16:9 e hanno larghezza decisa SOLO dallo spazio disponibile (larghezza e altezza del pannello):
/// mai dal contenuto, dal tipo di file, dallo stato dell'onda o dagli avvisi.
/// Le info stanno sotto i riquadri e occupano tutta l'altezza che resta (almeno <see cref="InfoHeight"/>):
/// su uno schermo piccolo il loro contenuto scorre, su uno grande si legge per intero.
/// </summary>
public sealed class MonitorPairPanel : Panel
{
    public static readonly DependencyProperty InfoHeightProperty = DependencyProperty.Register(
        nameof(InfoHeight), typeof(double), typeof(MonitorPairPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(MonitorPairPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Altezza minima della zona info sotto il riquadro 16:9 (titoli, stato, pagine...).</summary>
    public double InfoHeight
    {
        get => (double)GetValue(InfoHeightProperty);
        set => SetValue(InfoHeightProperty, value);
    }

    /// <summary>Spazio tra le due colonne.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Larghezza di una colonna per lo spazio dato; multipla di 16 così l'altezza 16:9 è intera.</summary>
    private double ColumnWidth(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 0 : available.Width;
        var columnWidth = (width - Gap) / 2;
        if (!double.IsInfinity(available.Height))
            columnWidth = Math.Min(columnWidth, (available.Height - InfoHeight) * 16.0 / 9.0);

        return Math.Max(0, Math.Floor(columnWidth / 16.0) * 16.0);
    }

    private double InfoHeightFor(Size available, double boxHeight) =>
        double.IsInfinity(available.Height) ? InfoHeight : Math.Max(InfoHeight, available.Height - boxHeight);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columnWidth = ColumnWidth(availableSize);
        var boxHeight = columnWidth / 16.0 * 9.0;
        var infoHeight = InfoHeightFor(availableSize, boxHeight);

        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(new Size(columnWidth, i < 2 ? boxHeight : infoHeight));

        return new Size(
            double.IsInfinity(availableSize.Width) ? columnWidth * 2 + Gap : availableSize.Width,
            boxHeight + infoHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = ColumnWidth(finalSize);
        var boxHeight = columnWidth / 16.0 * 9.0;
        var infoHeight = InfoHeightFor(finalSize, boxHeight);
        var left = Math.Max(0, (finalSize.Width - (columnWidth * 2 + Gap)) / 2);

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var x = left + (i % 2) * (columnWidth + Gap);
            var rect = i < 2
                ? new Rect(x, 0, columnWidth, boxHeight)
                : new Rect(x, boxHeight, columnWidth, infoHeight);
            InternalChildren[i].Arrange(rect);
        }

        return finalSize;
    }
}
