using NAudio.CoreAudioApi;
using Serilog;

namespace Regia.Output.Audio;

/// <summary>Un dispositivo di uscita audio (endpoint CoreAudio).</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Elenco dei dispositivi di uscita attivi, da CoreAudio.</summary>
public static class AudioDeviceEnumerator
{
    /// <summary>Dispositivi di rendering attivi. Lista vuota (e log) se CoreAudio non risponde: non lancia mai.</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = defaultDevice.ID;
            }
            catch (Exception ex)
            {
                // Nessun dispositivo predefinito (es. nessuna scheda audio): si elenca comunque il resto.
                Log.Debug(ex, "Nessun dispositivo audio predefinito");
            }

            var result = new List<AudioDeviceInfo>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossibile leggere i dispositivi audio");
            return [];
        }
    }
}
