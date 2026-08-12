using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenShare.Plugin.Capture.AudioCapture.Interop;

namespace ScreenShare.Plugin.Capture.AudioCapture;

/// <summary>
/// Captures system output ("what you hear") via WASAPI loopback on the default render device.
///
/// Uses hand-rolled COM interop (<see cref="SystemLoopbackInterop"/>) for device enumeration
/// instead of NAudio's own WasapiLoopbackCapture/MMDeviceEnumerator - see that type's doc comment
/// for why: a real, confirmed, reproducible-even-after-a-full-PC-restart InvalidCastException from
/// NAudio's own internal COM activation, not a hypothetical concern.
///
/// Threading/shutdown discipline mirrors <see cref="ProcessAudioCapture"/> deliberately (and, in
/// turn, what this class itself used to do before this rewrite): this project already hit a real
/// game freeze once from unbounded WASAPI shutdown blocking, so capture happens on its own
/// polling thread, samples cross to a separate processing thread via a bounded drop-oldest
/// channel before <see cref="SamplesAvailable"/> ever fires, and Dispose only ever does bounded
/// (1s) Joins, never unbounded ones.
/// </summary>
public sealed class SystemAudioCapture : IAudioSource
{
    private const int PollIntervalMs = 50;

    private readonly IAudioClient audioClient;
    private readonly ProcessLoopbackInterop.IAudioCaptureClient captureClient;
    private readonly WaveFormat waveFormat;
    private readonly Channel<float[]> sampleQueue;
    private readonly Thread captureThread;
    private readonly Thread processingThread;
    private readonly CancellationTokenSource stopCts = new();
    private float currentPeak;

    public WaveFormat WaveFormat => waveFormat;

    public float CurrentPeak => Volatile.Read(ref currentPeak);

    public Exception? LastError { get; private set; }

    /// <summary>Raised on our own processing thread (never the capture thread) with each buffer
    /// converted to interleaved float samples.</summary>
    public event Action<float[]>? SamplesAvailable;

    public SystemAudioCapture()
    {
        // Opus only accepts specific sample rates (8/12/16/24/48 kHz) and 1-2 channels; requesting
        // this directly (rather than querying the device's own mix format, which could be 44100Hz
        // or a surround channel count) relies on shared-mode WASAPI's AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
        // to resample/downmix transparently - same reasoning the previous implementation used.
        waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        object enumeratorObj = Activator.CreateInstance(Type.GetTypeFromCLSID(SystemLoopbackInterop.MMDeviceEnumeratorClsid)!)!;
        var enumerator = (SystemLoopbackInterop.IMMDeviceEnumerator)enumeratorObj;

        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out SystemLoopbackInterop.IMMDevice device));

        Marshal.ThrowExceptionForHR(device.Activate(typeof(IAudioClient).GUID, SystemLoopbackInterop.ClsCtxInprocServer, 0, out object audioClientObj));
        audioClient = (IAudioClient)audioClientObj;

        Guid sessionGuid = Guid.Empty;
        audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback,
            1_000_000, // 100ms, matches ProcessAudioCapture/NAudio's own WasapiCapture default
            0,
            waveFormat,
            ref sessionGuid);

        Marshal.ThrowExceptionForHR(audioClient.GetService(typeof(ProcessLoopbackInterop.IAudioCaptureClient).GUID, out object serviceObj));
        captureClient = (ProcessLoopbackInterop.IAudioCaptureClient)serviceObj;

        // Bounded + DropOldest: the capture thread's write can never block regardless of how far
        // behind the processing thread falls.
        sampleQueue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        processingThread = new Thread(ProcessingLoop) { IsBackground = true, Name = "ScreenShare-AudioProcessing" };
        processingThread.Start();

        captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenShare-AudioCapture" };

        audioClient.Start();
        captureThread.Start();
    }

    private void CaptureLoop()
    {
        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                if (stopCts.Token.WaitHandle.WaitOne(PollIntervalMs))
                    break;

                ReadAvailablePackets();
            }
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
    }

    /// <summary>Coalesces every WASAPI packet available this poll into a single combined array
    /// before queuing it, rather than queuing one entry per packet - see ProcessAudioCapture's
    /// equivalent method for why this matters (sampleQueue's capacity is measured in items, not
    /// audio duration, so many small entries give meaningfully less buffered-audio headroom than
    /// one larger one per poll).</summary>
    private void ReadAvailablePackets()
    {
        List<float> pending = [];

        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out int packetFrames));
        while (packetFrames != 0)
        {
            Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                out nint buffer, out int framesToRead, out AudioClientBufferFlags flags, out _, out _));

            int sampleCount = framesToRead * waveFormat.Channels;
            float[] samples = new float[sampleCount];
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                Marshal.Copy(buffer, samples, 0, sampleCount);

            pending.AddRange(samples);

            Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(framesToRead));
            Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
        }

        if (pending.Count == 0)
            return;

        float[] combined = pending.ToArray();
        Volatile.Write(ref currentPeak, ComputePeak(combined));
        sampleQueue.Writer.TryWrite(combined);
    }

    private static float ComputePeak(float[] samples)
    {
        float peak = 0f;
        foreach (float sample in samples)
        {
            float abs = Math.Abs(sample);
            if (abs > peak)
                peak = abs;
        }

        return peak;
    }

    private void ProcessingLoop()
    {
        try
        {
            while (sampleQueue.Reader.WaitToReadAsync(stopCts.Token).AsTask().GetAwaiter().GetResult())
            {
                while (sampleQueue.Reader.TryRead(out float[]? samples))
                    SamplesAvailable?.Invoke(samples);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown path
        }
    }

    public void Dispose()
    {
        stopCts.Cancel();
        captureThread.Join(1000);
        processingThread.Join(1000);
        stopCts.Dispose();

        try
        {
            audioClient.Stop();
        }
        catch
        {
            // best-effort - the client may already be in a bad/torn-down state
        }

        Marshal.ReleaseComObject(captureClient);
        Marshal.ReleaseComObject(audioClient);
    }
}
