using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenShare.Plugin.Capture.AudioCapture.Interop;

namespace ScreenShare.Plugin.Capture.AudioCapture;

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

        // Initialize is [PreserveSig] (confirmed by decompile, matching ProcessAudioCapture's own
        // equivalent call) - a failing HRESULT does NOT auto-throw, it has to be checked
        // explicitly. Missing this check here was a real bug: a genuine Initialize failure went
        // completely unnoticed, and the only visible symptom was a confusing downstream
        // AUDCLNT_E_NOT_INITIALIZED (0x88890001) from GetService below, which correctly reports
        // the client was never actually initialized but gives no hint why the real call failed.
        //
        // AutoConvertPcm | SrcDefaultQuality matter here in a way they don't for
        // ProcessAudioCapture's equivalent call: the process-loopback virtual device has no fixed
        // native format (a format is supplied, not queried), so any requested format is just
        // accepted. A REAL device has its own native mix format, and shared-mode WASAPI requires
        // either an exact format match or these flags to let the audio engine convert between
        // whatever we request (48kHz/2ch here) and the device's actual format - almost certainly
        // the real reason Initialize was failing, not just the missing HRESULT check. Matches
        // NAudio's own WasapiCapture.GetAudioClientStreamFlags() exactly for shared-mode loopback.
        Guid sessionGuid = Guid.Empty;
        int initializeHr = audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            1_000_000, // 100ms, matches ProcessAudioCapture/NAudio's own WasapiCapture default
            0,
            waveFormat,
            ref sessionGuid);
        Marshal.ThrowExceptionForHR(initializeHr);

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

        float[] combined = [.. pending];
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
