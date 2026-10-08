using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Regia.Output.Audio;

/// <summary>
/// Il "ding" del timer a zero: un solo suono sintetizzato in memoria (nessun file), riprodotto sul dispositivo dell'evento
/// (stesso ID dei video; vuoto o non più collegato = predefinito di Windows). Parte su un thread a parte e non lancia mai:
/// un errore audio si logga e basta, la regia non se ne accorge.
/// </summary>
public static class TimerChime
{
    private const double DurationSeconds = 1.2;

    public static void Play(string? deviceId)
    {
        var thread = new Thread(() => PlayCore(deviceId)) { IsBackground = true, Name = "TimerChime" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private static void PlayCore(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = OpenDevice(enumerator, deviceId);

            var mix = device.AudioClient.MixFormat;
            var provider = new ChimeProvider(mix.SampleRate, mix.Channels);

            using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 100);
            using var finished = new ManualResetEventSlim();
            output.PlaybackStopped += (_, _) => finished.Set();
            output.Init(provider);
            output.Play();

            finished.Wait(TimeSpan.FromSeconds(DurationSeconds + 2));
            Log.Information("Timer: suono di fine tempo riprodotto su {Device}", device.FriendlyName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Timer: impossibile riprodurre il suono di fine tempo");
        }
    }

    private static MMDevice OpenDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Timer: dispositivo audio dell'evento non disponibile, uso il predefinito");
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>Campana: sinusoide a 880 Hz con una armonica a 1760 Hz, attacco di 5 ms e decadimento esponenziale.</summary>
    private sealed class ChimeProvider : ISampleProvider
    {
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly long _totalFrames;
        private long _frame;

        public ChimeProvider(int sampleRate, int channels)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _totalFrames = (long)(sampleRate * DurationSeconds);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written + _channels <= count && _frame < _totalFrames)
            {
                var t = (double)_frame / _sampleRate;
                var attack = Math.Min(1.0, t / 0.005);
                var tail = Math.Min(1.0, (_totalFrames - _frame) / (_sampleRate * 0.05));
                var envelope = attack * tail * Math.Exp(-t * 4.5);
                var wave = Math.Sin(2 * Math.PI * 880 * t) + 0.3 * Math.Sin(2 * Math.PI * 1760 * t);
                var sample = (float)(0.3 * envelope * wave);

                for (var c = 0; c < _channels; c++)
                    buffer[offset + written++] = sample;

                _frame++;
            }

            return written;
        }
    }
}
