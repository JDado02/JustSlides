using LibVLCSharp.Shared;

namespace Regia.Output.Tappo;

/// <summary>Istanza condivisa di LibVLC, creata al primo uso.</summary>
public sealed class VlcService : IDisposable
{
    private LibVLC? _instance;

    public LibVLC Instance
    {
        get
        {
            if (_instance is null)
            {
                LibVLCSharp.Shared.Core.Initialize();
                _instance = new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview");
            }

            return _instance;
        }
    }

    public void Dispose()
    {
        _instance?.Dispose();
        _instance = null;
    }
}
