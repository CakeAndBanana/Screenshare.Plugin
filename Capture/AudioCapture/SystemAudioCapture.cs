using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenShare.Plugin.Capture.AudioCapture.Interfaces;
using ScreenShare.Plugin.Capture.AudioCapture.Interop;

namespace ScreenShare.Plugin.Capture.AudioCapture;

/// <summary>
/// System audio capture class that captures audio from the system's default audio endpoint using WASAPI loopback. 
/// It provides PCM float samples and peak level information.
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

    public event Action<float[]>? SamplesAvailable;

    /// <summary>
    /// Creates a new instance of the SystemAudioCapture class, initializing the audio client and capture client for loopback audio capture.
    /// </summary>
    public SystemAudioCapture()
    {
        // Initialize the wave format to 48kHz, stereo, IEEE float
        waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        object enumeratorObj = Activator.CreateInstance(Type.GetTypeFromCLSID(SystemLoopbackInterop.MMDeviceEnumeratorClsid)!)!;
        var enumerator = (SystemLoopbackInterop.IMMDeviceEnumerator)enumeratorObj;

        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out SystemLoopbackInterop.IMMDevice device));

        Marshal.ThrowExceptionForHR(device.Activate(typeof(IAudioClient).GUID, SystemLoopbackInterop.ClsCtxInprocServer, 0, out object audioClientObj));
        audioClient = (IAudioClient)audioClientObj;

        // Initialize the audio client for loopback capture with the specified wave format and flags
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

        // creates a bounded channel for audio samples with a capacity of 64, dropping the oldest samples when full
        sampleQueue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        processingThread = new Thread(ProcessingLoop) { IsBackground = true, Name = "ScreenShare-AudioProcessing" };
        processingThread.Start();

        captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenShare-AudioCapture" };

        // Starting the audio client and capture thread to begin capturing audio samples
        audioClient.Start();
        captureThread.Start();
    }

    /// <summary>
    /// Capture loop that continuously reads available audio packets from the capture client and processes them. It runs in a separate thread and stops when cancellation is requested.
    /// </summary>
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

    /// <summary>
    /// Reads available audio packets from the capture client, processes them into PCM float samples, computes the peak level, and writes the samples to the sample queue.
    /// It handles silent buffers and releases the capture buffer after reading.
    /// </summary>
    private void ReadAvailablePackets()
    {
        List<float> pending = [];

        // Get the size of the next available audio packet
        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out int packetFrames));
        while (packetFrames != 0)
        {
            Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                out nint buffer, out int framesToRead, out AudioClientBufferFlags flags, out _, out _));

            // Process the audio samples from the buffer into a float array 
            int sampleCount = framesToRead * waveFormat.Channels;
            float[] samples = new float[sampleCount];
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                Marshal.Copy(buffer, samples, 0, sampleCount);

            // Add the processed samples to the pending list for further processing
            pending.AddRange(samples);

            Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(framesToRead));
            Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
        }

        if (pending.Count == 0)
            return;

        // Combine the pending samples into a single float array and compute the peak level
        float[] combined = [.. pending];
        Volatile.Write(ref currentPeak, ComputePeak(combined));
        sampleQueue.Writer.TryWrite(combined);
    }

    /// <summary>
    /// Computes the peak absolute value of the provided audio samples. 
    /// It iterates through the samples, calculates the absolute value of each sample, and keeps track of the maximum value found.
    /// </summary>
    /// <param name="samples">The audio samples to process.</param>
    /// <returns>The peak absolute value.</returns>
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

    /// <summary>
    /// Processing loop that continuously reads audio samples from the sample queue and invokes the SamplesAvailable event for each batch of samples. 
    /// It runs in a separate thread and stops when cancellation is requested.
    /// </summary>
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
            // Expected when stopping the processing loop, no action needed.
        }
    }

    /// <summary>
    /// Disposes the SystemAudioCapture instance, stopping the capture and processing threads, releasing COM objects, and stopping the audio client.
    /// </summary>
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
        }

        Marshal.ReleaseComObject(captureClient);
        Marshal.ReleaseComObject(audioClient);
    }
}
