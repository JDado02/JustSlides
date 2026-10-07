namespace Regia.Core.Layout;

/// <summary>Adatta un'immagine di proporzioni date dentro un riquadro, centrata e senza deformarla (bande nere).</summary>
public static class AspectFit
{
    /// <summary>
    /// Rettangolo (relativo all'angolo del riquadro) in cui disegnare una sorgente di <paramref name="sourceWidth"/> ×
    /// <paramref name="sourceHeight"/> dentro un riquadro di <paramref name="boxWidth"/> × <paramref name="boxHeight"/>.
    /// Dimensioni non valide (zero o negative) → riquadro intero.
    /// </summary>
    public static (int X, int Y, int Width, int Height) Fit(int boxWidth, int boxHeight, int sourceWidth, int sourceHeight)
    {
        if (boxWidth <= 0 || boxHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return (0, 0, Math.Max(boxWidth, 0), Math.Max(boxHeight, 0));

        var scale = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        var width = Math.Min(boxWidth, Math.Max(1, (int)Math.Round(sourceWidth * scale)));
        var height = Math.Min(boxHeight, Math.Max(1, (int)Math.Round(sourceHeight * scale)));
        return ((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }
}
