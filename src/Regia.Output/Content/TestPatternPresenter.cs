using System.Windows;
using Regia.Core.Wave;
using Regia.Output.TestPattern;
using Regia.Output.Windows;

namespace Regia.Output.Content;

/// <summary>Schermata di prova (barre colore + orologio) come contenuto, utile per provare l'output.</summary>
public sealed class TestPatternPresenter : IContentPresenter
{
    private readonly ContentWindow _window;
    private UIElement? _view;
    private bool _closed;

    public TestPatternPresenter(ContentWindow window)
    {
        _window = window;
    }

    public PageInfo? Page => null;

    public event Action? PageChanged
    {
        add { }
        remove { }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _view = new TestPatternView();
        _window.SetContent(_view);
        await RenderWait.NextFramesAsync(cancellationToken);
    }

    public bool Next() => false;

    public bool Previous() => false;

    public void Close()
    {
        if (_closed)
            return;

        _closed = true;
        if (_view is not null)
            _window.ClearContent(_view);
    }
}
