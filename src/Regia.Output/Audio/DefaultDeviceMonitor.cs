using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace Regia.Output.Audio;

/// <summary>
/// Segnala quando cambia il dispositivo audio predefinito di Windows o ne appare/sparisce uno.
/// L'evento arriva da un thread di CoreAudio: chi lo ascolta deve rientrare sul thread UI.
/// </summary>
public sealed class DefaultDeviceMonitor : IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly Client? _client;

    public DefaultDeviceMonitor()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _client = new Client(() => Changed?.Invoke());
            _enumerator.RegisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            // Senza notifiche l'avviso si aggiorna solo quando cambiano le impostazioni: non è grave.
            Log.Warning(ex, "Notifiche dei dispositivi audio non disponibili");
        }
    }

    public event Action? Changed;

    /// <summary>ID del dispositivo di uscita predefinito di Windows; null se non c'è o CoreAudio non risponde.</summary>
    public static string? GetDefaultId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Nessun dispositivo audio predefinito");
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_enumerator is not null && _client is not null)
                _enumerator.UnregisterEndpointNotificationCallback(_client);
            _enumerator?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Rilascio delle notifiche audio");
        }
    }

    private sealed class Client(Action onChange) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChange();

        public void OnDeviceAdded(string pwstrDeviceId) => onChange();

        public void OnDeviceRemoved(string deviceId) => onChange();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => onChange();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Nessun interesse: arriva spesso e non cambia il predefinito.
        }
    }
}
