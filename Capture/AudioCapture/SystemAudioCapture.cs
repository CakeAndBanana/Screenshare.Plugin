using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenShare.Plugin.Capture.AudioCapture.Interfaces;
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
        waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        object enumeratorObj = Activator.CreateInstance(Type.GetTypeFromCLSID(SystemLoopbackInterop.MMDeviceEnumeratorClsid)!)!;
        var enumerator = (SystemLoopbackInterop.IMMDeviceEnumerator)enumeratorObj;

        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out SystemLoopbackInterop.IMMDevice device));

        Marshal.ThrowExceptionForHR(device.Activate(typeof(IAudioClient).GUID, SystemLoopbackInterop.ClsCtxInprocServer, 0, out object audioClientObj));
        audioClient = (IAudioClient)audioClientObj;

        Guid sessionGuid = Guid.Empty;
        int initializeHr = audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            1_000_000,
            0,
            waveFormat,
            ref sessionGuid);
        Marshal.ThrowExceptionForHR(initializeHr);

        Marshal.ThrowExceptionForHR(audioClient.GetService(typeof(ProcessLoopbackInterop.IAudioCaptureClient).GUID, out object serviceObj));
        captureClient = (ProcessLoopbackInterop.IAudioCaptureClient)serviceObj;

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
