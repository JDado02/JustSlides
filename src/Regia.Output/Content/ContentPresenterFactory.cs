using Regia.Core.Media;
using Regia.Core.Settings;
using Regia.Core.Wave;
using Regia.Output.Audio;
using Regia.Output.Tappo;
using Serilog;

namespace Regia.Output.Content;

/// <summary>Crea il presenter giusto per il tipo di file, alla risoluzione dell'output corrente.</summary>
public sealed class ContentPresenterFactory : IContentPresenterFactory
{
    private readonly OutputHost _output;
    private readonly VlcService _vlc;

    public ContentPresenterFactory(OutputHost output, VlcService vlc, AppSettings settings)
    {
        _output = output;
        _vlc = vlc;
        Settings = settings;
    }

    /// <summary>Impostazioni correnti (dispositivo audio): vanno aggiornate quando l'operatore le cambia.</summary>
    public AppSettings Settings { get; set; }

    /// <summary>Avviso per l'operatore (es. dispositivo audio scelto non collegato).</summary>
    public event Action<string>? Warning;

    public IContentPresenter Create(MediaItem item)
    {
        var size = _output.OutputPixelSize;

        return item.Kind switch
        {
            MediaKind.TestPattern => new TestPatternPresenter(_output.Content),
            MediaKind.Image => new ImagePresenter(_output.Content, item.Path, size),
            MediaKind.Pdf => new PdfPresenter(_output.Content, item.Path, size),
            MediaKind.Video => new VideoPresenter(_output.Content, _vlc, item.Path, item.VideoEnd, ResolveAudioDevice()),
            MediaKind.Ppt => throw new NotSupportedException("PowerPoint non è ancora gestito (Milestone 4)"),
            _ => throw new NotSupportedException("Tipo di file non supportato")
        };
    }

    /// <summary>ID del dispositivo scelto se è ancora collegato, altrimenti null (predefinito di Windows) con avviso.</summary>
    private string? ResolveAudioDevice()
    {
        var id = Settings.AudioDeviceId;
        if (string.IsNullOrEmpty(id))
            return null;

        var devices = AudioDeviceEnumerator.GetOutputDevices();
        if (devices.Any(d => d.Id == id))
            return id;

        var name = string.IsNullOrEmpty(Settings.AudioDeviceName) ? id : Settings.AudioDeviceName;
        Log.Warning("Dispositivo audio non trovato, si usa il predefinito: {Name}", name);
        Warning?.Invoke($"Uscita audio \"{name}\" non trovata: il video suona sul dispositivo predefinito di Windows.");
        return null;
    }
}
