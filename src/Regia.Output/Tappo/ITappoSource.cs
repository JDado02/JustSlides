using Regia.Output.Windows;

namespace Regia.Output.Tappo;

/// <summary>Sorgente dell'immagine mostrata dalla finestra Tappo (fermo o video in loop).</summary>
public interface ITappoSource : IDisposable
{
    /// <summary>Carica la sorgente e comincia a mostrarla nella finestra Tappo.</summary>
    Task AttachAsync(TappoWindow window);

    /// <summary>Congela su un fotogramma (durante la dissolvenza). Per le immagini non fa nulla.</summary>
    void Freeze();

    /// <summary>Riprende la riproduzione dopo un Freeze.</summary>
    void Resume();
}
