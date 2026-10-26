using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using TaskbarVisu.Core;

namespace TaskbarVisu.Audio;

/// <summary>
/// Capture WASAPI de la source choisie : tout le son du PC (loopback de la sortie par défaut, qui suit les
/// changements de périphérique), une sortie précise, une seule application, ou une entrée (micro, ligne).
/// </summary>
internal sealed class AudioCapture : IMMNotificationClient, IDisposable
{
    /// <summary>Loopback avec un petit tampon (20 ms) pour un flux régulier vers le rendu.</summary>
    private sealed class LowLatencyLoopback : WasapiCapture
    {
        public LowLatencyLoopback(MMDevice device) : base(device, false, 20) { }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    private enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private bool _callbackRegistered;
    private IWaveIn? _capture;
    private MMDevice? _device;
    private string? _deviceId;
    private int _appProcessId;

    // Source voulue (réglages).
    private AudioSourceKind _source = AudioSourceKind.SystemOutput;
    private string? _wantedDeviceId;
    private string? _wantedApp;
    private SampleKind _kind;
    private int _channels;
    private int _bytesPerSample;
    private float[] _convert = new float[8192];
    private volatile bool _wanted;
    private int _restartTicket;
    private bool _disposed;

    public SampleRing Ring { get; } = new(1 << 17);

    private sealed record StatusInfo(string Format, object[] Args);
    private volatile StatusInfo _status = new("Arrêté", Array.Empty<object>());

    /// <summary>État de la capture, en français (journal, diagnostics).</summary>
    public string Status => string.Format(CultureInfo.InvariantCulture, _status.Format, _status.Args);

    /// <summary>État de la capture dans la langue de l'interface.</summary>
    public string StatusText => Loc.F(_status.Format, _status.Args);

    private void SetStatus(string format, params object[] args) => _status = new StatusInfo(format, args);

    public void Start()
    {
        _wanted = true;
        ScheduleRestart(0);
    }

    /// <summary>Change la source écoutée ; la capture redémarre seulement si quelque chose a changé.</summary>
    public void Configure(AudioSourceKind source, string? deviceId, string? app)
    {
        lock (_gate)
        {
            if (source == _source && deviceId == _wantedDeviceId && app == _wantedApp) return;
            (_source, _wantedDeviceId, _wantedApp) = (source, deviceId, app);
        }
        if (_wanted) ScheduleRestart(0);
    }

    /// <summary>Diagnostic (--test-file) : lit un fichier audio (WAV, MP3…) en boucle à la place de la capture (rien n'est joué).</summary>
    public void StartTestFile(string path)
    {
        using NAudio.Wave.WaveStream file = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new NAudio.Wave.WaveFileReader(path)
            : new NAudio.Wave.MediaFoundationReader(path);
        var reader = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
        int rate = reader.WaveFormat.SampleRate, channels = reader.WaveFormat.Channels;
        var all = new List<float>();
        var buffer = new float[4096 * channels];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + channels <= read; i += channels)
            {
                all.Add(buffer[i]);
                all.Add(buffer[i + (channels > 1 ? 1 : 0)]);
            }
        }
        var samples = all.ToArray();
        int frames = samples.Length / 2;
        if (frames == 0) return;

        Ring.Reset(rate);
        SetStatus("Signal de test");
        var thread = new Thread(() =>
        {
            int chunk = rate / 100, pos = 0;
            var block = new float[chunk * 2];
            long written = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!_disposed)
            {
                for (int i = 0; i < chunk; i++, pos = (pos + 1) % frames)
                {
                    block[i * 2] = samples[pos * 2];
                    block[i * 2 + 1] = samples[pos * 2 + 1];
                }
                Ring.Write(block, chunk);
                written += chunk;
                double ahead = written / (double)rate - clock.Elapsed.TotalSeconds;
                if (ahead > 0) Thread.Sleep(TimeSpan.FromSeconds(ahead));
            }
        }) { IsBackground = true, Name = "TaskbarVisu.TestFile" };
        thread.Start();
    }

    /// <summary>
    /// Diagnostic (--test-signal) : remplace la capture par un petit morceau synthétique
    /// (kick, basse, accord stéréo, charleston). Rien n'est joué sur la sortie audio.
    /// </summary>
    public void StartTestSignal(TestSong? song = null)
    {
        const int rate = 48000, chunk = 480;
        Ring.Reset(rate);
        SetStatus("Signal de test");
        var thread = new Thread(() =>
        {
            var buffer = new float[chunk * 2];
            var rng = new Random(1);
            long n = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double[] chord = { 220.0, 277.18, 329.63, 440.0 };
            while (!_disposed)
            {
                for (int i = 0; i < chunk; i++, n++)
                {
                    double t = n / (double)rate;
                    if (song is not null)
                    {
                        var (sl, sr) = song.Sample(t);
                        buffer[i * 2] = sl;
                        buffer[i * 2 + 1] = sr;
                        continue;
                    }
                    double beat = t % 0.5;
                    double kick = Math.Sin(2 * Math.PI * (50 + 120 * Math.Exp(-beat * 30)) * beat) * Math.Exp(-beat * 9) * 0.8;
                    double bass = Math.Sin(2 * Math.PI * (t % 2 < 1 ? 55 : 41.2) * t) * 0.25;
                    double padL = 0, padR = 0;
                    for (int k = 0; k < chord.Length; k++)
                    {
                        double v = Math.Sin(2 * Math.PI * chord[k] * t + k) + 0.3 * Math.Sin(4 * Math.PI * chord[k] * t);
                        padL += v * (k % 2 == 0 ? 0.07 : 0.035);
                        padR += v * (k % 2 == 0 ? 0.035 : 0.07);
                    }
                    double off = (t + 0.25) % 0.5;
                    double hat = (rng.NextDouble() * 2 - 1) * Math.Exp(-off * 60) * 0.25;
                    buffer[i * 2] = (float)(kick + bass + padL + hat * 0.4);
                    buffer[i * 2 + 1] = (float)(kick + bass + padR + hat);
                }
                Ring.Write(buffer, chunk);
                double ahead = n / (double)rate - clock.Elapsed.TotalSeconds;
                if (ahead > 0) Thread.Sleep(TimeSpan.FromSeconds(ahead));
            }
        }) { IsBackground = true, Name = "TaskbarVisu.TestSignal" };
        thread.Start();
    }

    public void Stop()
    {
        _wanted = false;
        Interlocked.Increment(ref _restartTicket);
        lock (_gate) StopInternal();
        SetStatus("En pause");
    }

    private void ScheduleRestart(int delayMs)
    {
        int ticket = Interlocked.Increment(ref _restartTicket);
        Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            if (ticket != Volatile.Read(ref _restartTicket) || !_wanted) return;
            lock (_gate)
            {
                if (_disposed || !_wanted) return;
                StopInternal();
                StartInternal();
            }
        });
    }

    private void StartInternal()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            if (!_callbackRegistered)
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
                _callbackRegistered = true;
            }

            if (_source == AudioSourceKind.Application)
            {
                StartApplication();
                return;
            }

            bool input = _source == AudioSourceKind.Input;
            var flow = input ? DataFlow.Capture : DataFlow.Render;
            MMDevice? device = null;
            bool missingChoice = false;
            if (_source is AudioSourceKind.OutputDevice or AudioSourceKind.Input && _wantedDeviceId is { } id)
            {
                try
                {
                    var chosen = _enumerator.GetDevice(id);
                    if (chosen.State == DeviceState.Active) device = chosen;
                    else chosen.Dispose();
                }
                catch { /* périphérique débranché */ }
                if (device is null) missingChoice = true;
            }
            if (device is null)
            {
                if (!_enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                {
                    SetStatus(input ? "Aucune entrée audio" : "Aucune sortie audio");
                    ScheduleRestart(3000);
                    return;
                }
                device = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            }

            WasapiCapture capture = input ? new WasapiCapture(device, false, 20) : new LowLatencyLoopback(device);
            if (!Begin(capture))
            {
                device.Dispose();
                return;
            }
            _device = device;
            _deviceId = device.ID;
            SetStatus(missingChoice ? "{0} — {1} Hz (périphérique choisi absent : celui par défaut est utilisé)" : "{0} — {1} Hz",
                device.FriendlyName, capture.WaveFormat.SampleRate);
            Log.Info($"Capture {(input ? "d'entrée" : "loopback")} démarrée : {Status}, {capture.WaveFormat}");
        }
        catch (Exception ex)
        {
            SetStatus("Capture audio impossible (nouvel essai…)");
            Log.Error("Démarrage de la capture impossible", ex);
            StopInternal();
            ScheduleRestart(2000);
        }
    }

    /// <summary>Branche une capture prête : format, tampon, événements. False si le format n'est pas géré.</summary>
    private bool Begin(IWaveIn capture)
    {
        if (!ConfigureFormat(capture.WaveFormat))
        {
            SetStatus("Format audio non géré : {0}", capture.WaveFormat);
            Log.Error(Status);
            capture.Dispose();
            return false;
        }
        Ring.Reset(capture.WaveFormat.SampleRate);
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        capture.StartRecording();
        _capture = capture;
        return true;
    }

    /// <summary>Une seule application : on attend qu'elle soit lancée, et on la suit si elle redémarre.</summary>
    private void StartApplication()
    {
        string? app = _wantedApp;
        if (string.IsNullOrWhiteSpace(app))
        {
            SetStatus("Aucune application choisie");
            return;
        }
        if (AudioSources.FindProcess(app) is not { } pid)
        {
            SetStatus("En attente de {0}…", app);
            ScheduleRestart(3000);
            return;
        }

        var capture = new ProcessLoopbackCapture(pid);
        if (!Begin(capture))
        {
            ScheduleRestart(5000);
            return;
        }
        _appProcessId = pid;
        SetStatus("{0} — {1} Hz", app, capture.WaveFormat.SampleRate);
        Log.Info($"Capture de l'application démarrée : {app} (processus {pid}), {capture.WaveFormat}");
        WatchApplication(Volatile.Read(ref _restartTicket), pid);
    }

    /// <summary>Si l'application se ferme, on revient en attente pour la reprendre à son prochain lancement.</summary>
    private void WatchApplication(int ticket, int pid)
    {
        Task.Run(async () =>
        {
            while (ticket == Volatile.Read(ref _restartTicket) && _wanted)
            {
                await Task.Delay(3000).ConfigureAwait(false);
                if (ticket != Volatile.Read(ref _restartTicket) || !_wanted) return;
                if (!AudioSources.IsRunning(pid))
                {
                    Log.Info("Application écoutée fermée : en attente de son retour");
                    ScheduleRestart(0);
                    return;
                }
            }
        });
    }

    private void StopInternal()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }
        try { _device?.Dispose(); } catch { }
        _device = null;
        _deviceId = null;
        _appProcessId = 0;
    }

    private bool ConfigureFormat(WaveFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _bytesPerSample = format.BitsPerSample / 8;

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        if (format is WaveFormatExtensible ext)
        {
            isFloat = ext.SubFormat == SubtypeFloat;
            isPcm = ext.SubFormat == SubtypePcm;
        }

        if (isFloat && format.BitsPerSample == 32) { _kind = SampleKind.Float32; return true; }
        if (isPcm && format.BitsPerSample == 16) { _kind = SampleKind.Pcm16; return true; }
        if (isPcm && format.BitsPerSample == 24) { _kind = SampleKind.Pcm24; return true; }
        if (isPcm && format.BitsPerSample == 32) { _kind = SampleKind.Pcm32; return true; }
        return false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int frameBytes = _bytesPerSample * _channels;
        if (frameBytes == 0) return;
        int frames = e.BytesRecorded / frameBytes;
        if (frames <= 0) return;

        if (_convert.Length < frames * 2) _convert = new float[frames * 2];
        var dst = _convert.AsSpan(0, frames * 2);
        var src = e.Buffer.AsSpan(0, frames * frameBytes);
        int rOffset = _channels > 1 ? _bytesPerSample : 0;

        switch (_kind)
        {
            case SampleKind.Float32:
                for (int f = 0; f < frames; f++)
                {
                    int o = f * frameBytes;
                    dst[f * 2] = MemoryMarshal.Read<float>(src[o..]);
                    dst[f * 2 + 1] = MemoryMarshal.Read<float>(src[(o + rOffset)..]);
                }
                break;
            case SampleKind.Pcm16:
                for (int f = 0; f < frames; f++)
                {
                    int o = f * frameBytes;
                    dst[f * 2] = BinaryPrimitives.ReadInt16LittleEndian(src[o..]) / 32768f;
                    dst[f * 2 + 1] = BinaryPrimitives.ReadInt16LittleEndian(src[(o + rOffset)..]) / 32768f;
                }
                break;
            case SampleKind.Pcm24:
                for (int f = 0; f < frames; f++)
                {
                    int o = f * frameBytes;
                    dst[f * 2] = Read24(src, o);
                    dst[f * 2 + 1] = Read24(src, o + rOffset);
                }
                break;
            case SampleKind.Pcm32:
                for (int f = 0; f < frames; f++)
                {
                    int o = f * frameBytes;
                    dst[f * 2] = BinaryPrimitives.ReadInt32LittleEndian(src[o..]) / 2147483648f;
                    dst[f * 2 + 1] = BinaryPrimitives.ReadInt32LittleEndian(src[(o + rOffset)..]) / 2147483648f;
                }
                break;
        }

        Ring.Write(dst, frames);
    }

    private static float Read24(ReadOnlySpan<byte> s, int o) =>
        ((s[o] << 8 | s[o + 1] << 16 | s[o + 2] << 24) >> 8) / 8388608f;

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log.Error("Capture interrompue", e.Exception);
            SetStatus("Capture interrompue (nouvel essai…)");
        }
        if (_wanted) ScheduleRestart(1000);
    }

    // --- IMMNotificationClient : appelé par Windows sur un thread quelconque, ne jamais bloquer ici.

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role != Role.Multimedia || !_wanted) return;
        // Seules les sources « par défaut » suivent le changement ; un périphérique choisi reste celui choisi.
        bool follows = _source switch
        {
            AudioSourceKind.SystemOutput => flow == DataFlow.Render,
            AudioSourceKind.OutputDevice => flow == DataFlow.Render && _deviceId != _wantedDeviceId,
            AudioSourceKind.Input => flow == DataFlow.Capture && _deviceId != _wantedDeviceId,
            _ => false,
        };
        if (follows) ScheduleRestart(300);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (_wanted && deviceId == _deviceId && newState != DeviceState.Active)
            ScheduleRestart(300);
    }

    void IMMNotificationClient.OnDeviceAdded(string deviceId)
    {
        // Le périphérique choisi revient (casque rebranché) : on le reprend.
        if (_wanted && deviceId == _wantedDeviceId && _deviceId != deviceId) ScheduleRestart(500);
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        if (_wanted && deviceId == _deviceId) ScheduleRestart(300);
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _wanted = false;
        lock (_gate)
        {
            _disposed = true;
            StopInternal();
            if (_callbackRegistered && _enumerator is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            }
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }
}
