using System.Runtime.InteropServices;
using NAudio.Wave;

namespace TaskbarVisu.Audio;

/// <summary>
/// Capture du son d'une seule application (et de ses processus enfants), sans les autres sons du PC.
/// API « process loopback » de Windows 10 2004+ : ActivateAudioInterfaceAsync sur le périphérique virtuel
/// VAD\Process_Loopback. NAudio 2.2 n'en fournit que les structures : l'interop est faite ici.
/// </summary>
internal sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualDevice = "VAD\\Process_Loopback";
    private const uint StreamLoopback = 0x00020000, StreamEventCallback = 0x00040000;
    private const uint StreamAutoConvertPcm = 0x80000000, StreamSrcDefaultQuality = 0x08000000;
    private const uint BufferSilent = 0x2;

    private readonly int _processId;
    private Interop.IAudioClient? _client;
    private Interop.IAudioCaptureClient? _captureClient;
    private IntPtr _event;
    private Thread? _thread;
    private volatile bool _stop;
    private byte[] _buffer = new byte[64 * 1024];

    public ProcessLoopbackCapture(int processId)
    {
        _processId = processId;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        _client = Activate(_processId);
        // Ce périphérique virtuel n'a pas de format propre : on impose le nôtre (flottant 48 kHz, sinon PCM 16 bits).
        int hr = Initialize(_client, WaveFormat);
        if (hr < 0)
        {
            Marshal.ReleaseComObject(_client);
            _client = Activate(_processId);
            WaveFormat = new WaveFormat(44100, 16, 2);
            hr = Initialize(_client, WaveFormat);
        }
        Marshal.ThrowExceptionForHR(hr);

        _event = Interop.CreateEventW(IntPtr.Zero, false, false, null);
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_event));
        Marshal.ThrowExceptionForHR(_client.GetService(typeof(Interop.IAudioCaptureClient).GUID, out object service));
        _captureClient = (Interop.IAudioCaptureClient)service;
        Marshal.ThrowExceptionForHR(_client.Start());

        _stop = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "TaskbarVisu.AppCapture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static int Initialize(Interop.IAudioClient client, WaveFormat format)
    {
        var wf = new Interop.WaveFormatEx
        {
            FormatTag = (short)(format.Encoding == WaveFormatEncoding.IeeeFloat ? 3 : 1),
            Channels = (short)format.Channels,
            SamplesPerSec = format.SampleRate,
            AvgBytesPerSec = format.AverageBytesPerSecond,
            BlockAlign = (short)format.BlockAlign,
            BitsPerSample = (short)format.BitsPerSample,
        };
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<Interop.WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(wf, p, false);
            const long bufferDuration = 2_000_000; // 200 ms, en unités de 100 ns
            return client.Initialize(0, StreamLoopback | StreamEventCallback | StreamAutoConvertPcm | StreamSrcDefaultQuality,
                bufferDuration, 0, p, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private static Interop.IAudioClient Activate(int processId)
    {
        var parameters = new Interop.ActivationParams { ActivationType = 1, TargetProcessId = processId, LoopbackMode = 0 };
        int size = Marshal.SizeOf<Interop.ActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(size);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<Interop.BlobVariant>());
        try
        {
            Marshal.StructureToPtr(parameters, pParams, false);
            Marshal.StructureToPtr(new Interop.BlobVariant { Type = 65 /* VT_BLOB */, Size = (uint)size, Data = pParams }, pVariant, false);
            var handler = new Interop.CompletionHandler();
            Interop.ActivateAudioInterfaceAsync(VirtualDevice, typeof(Interop.IAudioClient).GUID, pVariant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Activation de la capture d'application");
            handler.Operation!.GetActivateResult(out int hr, out object client);
            Marshal.ThrowExceptionForHR(hr);
            return (Interop.IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    private void CaptureLoop()
    {
        Exception? error = null;
        try
        {
            int blockAlign = WaveFormat.BlockAlign;
            while (!_stop)
            {
                Interop.WaitForSingleObject(_event, 100);
                while (!_stop)
                {
                    Marshal.ThrowExceptionForHR(_captureClient!.GetNextPacketSize(out uint packet));
                    if (packet == 0) break;
                    Marshal.ThrowExceptionForHR(_captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int bytes = (int)frames * blockAlign;
                    if (_buffer.Length < bytes) _buffer = new byte[bytes * 2];
                    if ((flags & BufferSilent) != 0) Array.Clear(_buffer, 0, bytes);
                    else Marshal.Copy(data, _buffer, 0, bytes);
                    _captureClient.ReleaseBuffer(frames);
                    if (bytes > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(_buffer, bytes));
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        try { _client?.Stop(); } catch { }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    public void StopRecording()
    {
        _stop = true;
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(1000);
        _thread = null;
    }

    public void Dispose()
    {
        StopRecording();
        if (_captureClient is not null) Marshal.ReleaseComObject(_captureClient);
        if (_client is not null) Marshal.ReleaseComObject(_client);
        _captureClient = null;
        _client = null;
        if (_event != IntPtr.Zero) Interop.CloseHandle(_event);
        _event = IntPtr.Zero;
    }

    private static class Interop
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct ActivationParams
        {
            public int ActivationType;   // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
            public int TargetProcessId;
            public int LoopbackMode;     // 0 = inclure l'arborescence du processus
        }

        /// <summary>PROPVARIANT de type VT_BLOB (disposition x64).</summary>
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct BlobVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public uint Size;
            [FieldOffset(16)] public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WaveFormatEx
        {
            public short FormatTag;
            public short Channels;
            public int SamplesPerSec;
            public int AvgBytesPerSec;
            public short BlockAlign;
            public short BitsPerSample;
            public short Size;
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        /// <summary>Marqueur « agile » : Windows rappelle le gestionnaire depuis n'importe quel thread.</summary>
        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgileObject { }

        public sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public ManualResetEventSlim Done { get; } = new();
            public IActivateAudioInterfaceAsyncOperation? Operation { get; private set; }

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                Operation = operation;
                Done.Set();
            }
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
