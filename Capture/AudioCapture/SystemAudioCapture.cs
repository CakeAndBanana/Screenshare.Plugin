using System.Threading.Channels;
using NAudio.Wave;

namespace ScreenShare.Plugin.Capture.AudioCapture;

/// <summary>
/// Captures system output ("what you hear") via WASAPI loopback on the default render device.
///
/// NAudio's WasapiCapture.Dispose() calls captureThread.Join() with NO timeout, and that thread
/// invokes DataAvailable synchronously inline, only re-checking its stop flag afterward. Any
/// slow or blocked consumer of DataAvailable therefore risks an unbounded hang inside Dispose()
/// - which for us runs on the game's render thread and would freeze the whole game (confirmed:
/// this happened before this class deferred processing off the capture thread). So the capture
/// callback here does the minimum possible bounded work and hands everything else to our own
/// processing thread, which we fully control and can bound at shutdown.
/// </summary>
public sealed class SystemAudioCapture : IAudioSource
{
    private readonly WasapiLoopbackCapture capture;
    private readonly Channel<float[]> sampleQueue;
    private readonly Thread processingThread;
    private readonly CancellationTokenSource stopCts = new();
    private float currentPeak;
    private long bytesCaptured;

    public WaveFormat WaveFormat => capture.WaveFormat;

    public float CurrentPeak => Volatile.Read(ref currentPeak);

    public long BytesCaptured => Interlocked.Read(ref bytesCaptured);

    public Exception? LastError { get; private set; }

    /// <summary>Raised on our own processing thread (never NAudio's capture thread) with each
    /// buffer converted to interleaved float samples, regardless of the device's actual mix
    /// format (float32 or 16-bit PCM).</summary>
    public event Action<float[]>? SamplesAvailable;

    public SystemAudioCapture()
    {
        capture = new WasapiLoopbackCapture();

        // Bounded + DropOldest: OnDataAvailable's write can never block regardless of how far
        // behind the processing thread falls.
        sampleQueue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        processingThread = new Thread(ProcessingLoop)
        {
            IsBackground = true,
            Name = "ScreenShare-AudioProcessing",
        };
        processingThread.Start();

        capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Interlocked.Add(ref bytesCaptured, e.BytesRecorded);
        float[] samples = ToFloatSamples(e.Buffer, e.BytesRecorded, capture.WaveFormat);
        Volatile.Write(ref currentPeak, ComputePeak(samples));
        sampleQueue.Writer.TryWrite(samples);
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

    private static float[] ToFloatSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int sampleCount = bytesRecorded / 4;
            float[] samples = new float[sampleCount];
            System.Buffer.BlockCopy(buffer, 0, samples, 0, sampleCount * 4);
            return samples;
        }

        if (format.BitsPerSample == 16)
        {
            int sampleCount = bytesRecorded / 2;
            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            return samples;
        }

        return [];
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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            LastError = e.Exception;
    }

    public void Dispose()
    {
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.StopRecording();
        capture.Dispose();

        stopCts.Cancel();
        processingThread.Join(1000);
        stopCts.Dispose();
    }
}
