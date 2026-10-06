using System.Windows;
using Regia.Output.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Regia.Output.Windows;

/// <summary>Finestra contenuto (sotto il Tappo): ospita il contenuto in onda (immagine, pagina PDF...), nero se vuota.</summary>
public partial class ContentWindow : Window
{
    internal PixelPlacement Placement { get; }

    public ContentWindow()
    {
        InitializeComponent();
        Placement = new PixelPlacement(this);
    }

    /// <summary>Sostituisce il contenuto mostrato (null = nero).</summary>
    public void SetContent(UIElement? element)
    {
        Host.Children.Clear();
        if (element is not null)
            Host.Children.Add(element);
    }

    /// <summary>Toglie il contenuto solo se è ancora quello indicato (evita che un presenter vecchio svuoti il nuovo).</summary>
    public void ClearContent(UIElement owner)
    {
        if (Host.Children.Contains(owner))
            Host.Children.Remove(owner);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.AddExStyle(this, WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Placement.ReapplyDeferred();
    }
}
