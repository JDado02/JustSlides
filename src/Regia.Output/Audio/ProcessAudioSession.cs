using System.Diagnostics;
using NAudio.CoreAudioApi;
using Regia.Core.Audio;
using Serilog;

namespace Regia.Output.Audio;

/// <summary>
/// Volume, Mute e fade di uscita sulla sessione CoreAudio di un processo (POWERPNT.EXE, per PID).
/// Tutto il lavoro CoreAudio gira su un thread MTA dedicato: i metodi pubblici non bloccano e non lanciano mai.
/// Non tocca il volume master dell'endpoint né le sessioni di altri processi.
/// A fine uso <see cref="Release"/> rimette la sessione a 100% (Windows ricorda il volume per applicazione).
/// </summary>
public sealed class ProcessAudioSession : IDisposable
{
    private static readonly object Registry = new();
    private static readonly Dictionary<int, ProcessAudioSession> ActiveByPid = [];

    private const int ScanMs = 100;
    private const int FadeTickMs = 30;
    private const int DeviceRefreshMs = 2000;

    private readonly int _pid;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    private int _volume = 100;
    private bool _muted;
    private double _fadeScale = 1.0;
    private bool _silenced;
    private bool _stop;
    private bool _restoreOnStop;
    private DateTime _restoreAt = DateTime.MaxValue;

    private Stopwatch? _fadeWatch;
    private TimeSpan _fadeDuration;
    private double _fadeStart;
    private TaskCompletionSource? _fadeDone;

    public ProcessAudioSession(int pid)
    {
        _pid = pid;

        // Una sola sessione attiva per PID: la precedente (in fase di ripristino) cede il posto senza ripristinare.
        ProcessAudioSession? previous;
        lock (Registry)
        {
            ActiveByPid.TryGetValue(pid, out previous);
            ActiveByPid[pid] = this;
        }

        previous?.Abandon();

        _thread = new Thread(Run) { IsBackground = true, Name = "AudioSession-" + pid };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void SetVolume(int volume)
    {
        lock (_gate)
            _volume = Math.Clamp(volume, 0, 100);
        _wake.Set();
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
            _muted = muted;
        _wake.Set();
    }

    /// <summary>Rampa a zero in <paramref name="duration"/>; completa a fine rampa o alla chiusura.</summary>
    public Task FadeOutAsync(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_stop)
                return Task.CompletedTask;

            _fadeDone?.TrySetResult();
            _fadeDone = null;

            if (duration <= TimeSpan.Zero)
            {
                _fadeScale = 0;
                _fadeWatch = null;
                _wake.Set();
                return Task.CompletedTask;
            }

            _fadeStart = _fadeScale;
            _fadeDuration = duration;
            _fadeWatch = Stopwatch.StartNew();
            _fadeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _wake.Set();
            return _fadeDone.Task;
        }
    }

    /// <summary>
    /// Fine dell'uso: silenzio subito, poi (dopo <paramref name="restoreAfter"/>, il tempo che lo slideshow si chiuda)
    /// la sessione torna a 100% e senza Mute, così il PowerPoint dell'utente non resta a zero.
    /// </summary>
    public void Release(TimeSpan restoreAfter)
    {
        lock (_gate)
        {
            if (_stop)
                return;

            _silenced = true;
            _fadeDone?.TrySetResult();
            _fadeDone = null;
            _fadeWatch = null;
            _restoreOnStop = true;
            _restoreAt = DateTime.UtcNow + restoreAfter;
        }

        _wake.Set();
    }

    public void Dispose() => Release(TimeSpan.Zero);

    /// <summary>Un'altra sessione per lo stesso PID prende il posto: si ferma senza ripristinare.</summary>
    private void Abandon()
    {
        lock (_gate)
        {
            _stop = true;
            _restoreOnStop = false;
            _fadeDone?.TrySetResult();
            _fadeDone = null;
        }

        _wake.Set();
    }

    private void Run()
    {
        MMDeviceEnumerator? enumerator = null;
        var devices = new List<MMDevice>();
        var lastRefresh = DateTime.MinValue;

        try
        {
            enumerator = new MMDeviceEnumerator();

            while (true)
            {
                float target;
                bool restoreNow;
                bool stop;
                bool fading;

                lock (_gate)
                {
                    if (_fadeWatch is { } watch)
                    {
                        _fadeScale = AudioRamp.FadeOutScale(_fadeStart, watch.Elapsed, _fadeDuration);
                        if (watch.Elapsed >= _fadeDuration)
                        {
                            _fadeWatch = null;
                            _fadeDone?.TrySetResult();
                            _fadeDone = null;
                        }
                    }

                    fading = _fadeWatch is not null;
                    target = _silenced ? 0f : AudioRamp.Effective(_volume, _muted, _fadeScale);
                    restoreNow = _restoreOnStop && DateTime.UtcNow >= _restoreAt;
                    stop = _stop;
                }

                if (stop)
                    break;

                if (restoreNow)
                {
                    Apply(enumerator, devices, ref lastRefresh, 1f, true, forceRefresh: true);
                    break;
                }

                // La scansione periodica serve alle sessioni nate dopo (PowerPoint crea la sessione al primo suono).
                Apply(enumerator, devices, ref lastRefresh, target, false, forceRefresh: false);

                _wake.WaitOne(fading ? FadeTickMs : ScanMs);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio della sessione PowerPoint (pid {Pid}): errore CoreAudio", _pid);
        }
        finally
        {
            foreach (var device in devices)
                SafeDispose(device);
            SafeDispose(enumerator);

            lock (Registry)
            {
                if (ActiveByPid.TryGetValue(_pid, out var current) && ReferenceEquals(current, this))
                    ActiveByPid.Remove(_pid);
            }

            lock (_gate)
            {
                _stop = true;
                _fadeDone?.TrySetResult();
                _fadeDone = null;
            }

            // _wake non si distrugge: SetVolume/SetMuted possono ancora chiamare Set() da altri thread.
        }
    }

    /// <summary>Applica volume (e Mute se <paramref name="resetMute"/>) a tutte le sessioni del PID. Vero se ha trovato almeno una sessione.</summary>
    private bool Apply(MMDeviceEnumerator enumerator, List<MMDevice> devices, ref DateTime lastRefresh, float volume, bool resetMute, bool forceRefresh)
    {
        var found = false;

        try
        {
            if (forceRefresh || devices.Count == 0 || (DateTime.UtcNow - lastRefresh).TotalMilliseconds >= DeviceRefreshMs)
            {
                foreach (var device in devices)
                    SafeDispose(device);
                devices.Clear();
                devices.AddRange(enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));
                lastRefresh = DateTime.UtcNow;
            }

            foreach (var device in devices)
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;

                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.GetProcessID != (uint)_pid)
                            continue;

                        found = true;
                        var simple = session.SimpleAudioVolume;
                        if (Math.Abs(simple.Volume - volume) > 0.001f)
                            simple.Volume = volume;
                        if (resetMute && simple.Mute)
                            simple.Mute = false;
                    }
                }
                catch (Exception ex)
                {
                    // Un dispositivo che sparisce a metà scansione: si riscansiona subito la lista.
                    Log.Debug("Sessione audio non leggibile: {Message}", ex.Message);
                    lastRefresh = DateTime.MinValue;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio PowerPoint (pid {Pid}): scansione sessioni non riuscita", _pid);
            lastRefresh = DateTime.MinValue;
        }

        return found;
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Rilascio COM best effort.
        }
    }
}
