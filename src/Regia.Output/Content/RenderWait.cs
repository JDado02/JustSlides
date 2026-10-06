using System.Windows.Media;

namespace Regia.Output.Content;

/// <summary>Dimensione in pixel fisici dell'output (per decodificare e renderizzare alla risoluzione giusta).</summary>
public readonly record struct OutputSize(int Width, int Height);

/// <summary>
/// WPF non ha un evento "frame presentato": si aspettano un paio di cicli di rendering dopo aver
/// messo il contenuto nella finestra, con un tetto di tempo per non restare mai appesi.
/// </summary>
internal static class RenderWait
{
    public static async Task NextFramesAsync(CancellationToken cancellationToken, int frames = 2)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = frames;

        void Handler(object? sender, EventArgs e)
        {
            if (--left <= 0)
                tcs.TrySetResult();
        }

        CompositionTarget.Rendering += Handler;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(1000, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            CompositionTarget.Rendering -= Handler;
        }
    }
}
