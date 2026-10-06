using Regia.Core.Media;
using Regia.Core.Wave;

namespace Regia.Output.Content;

/// <summary>Crea il presenter giusto per il tipo di file, alla risoluzione dell'output corrente.</summary>
public sealed class ContentPresenterFactory : IContentPresenterFactory
{
    private readonly OutputHost _output;

    public ContentPresenterFactory(OutputHost output)
    {
        _output = output;
    }

    public IContentPresenter Create(MediaItem item)
    {
        var size = _output.OutputPixelSize;

        return item.Kind switch
        {
            MediaKind.TestPattern => new TestPatternPresenter(_output.Content),
            MediaKind.Image => new ImagePresenter(_output.Content, item.Path, size),
            MediaKind.Pdf => new PdfPresenter(_output.Content, item.Path, size),
            MediaKind.Video => throw new NotSupportedException("I video non sono ancora gestiti (Milestone 3)"),
            MediaKind.Ppt => throw new NotSupportedException("PowerPoint non è ancora gestito (Milestone 4)"),
            _ => throw new NotSupportedException("Tipo di file non supportato")
        };
    }
}
