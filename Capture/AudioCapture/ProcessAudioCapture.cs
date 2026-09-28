using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenShare.Plugin.Capture.AudioCapture.Interfaces;
using ScreenShare.Plugin.Capture.AudioCapture.Interop;

namespace ScreenShare.Plugin.Capture.AudioCapture;

/// <summary>
/// Processes audio capture from a specific process using loopback audio capture. It captures audio samples, computes the peak level, and provides the samples to subscribers via an event
/// </summary>
public sealed class ProcessAudioCapture : IAudioSource
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FrameWaitTimeoutMs = 100;

    private readonly IAudioClient audioClient;
    private readonly ProcessLoopbackInterop.IAudioCaptureClient captureClient;
    private readonly WaveFormat waveFormat;
    private readonly AutoResetEvent frameEvent;
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
    /// Constructs a new ProcessAudioCapture instance with the specified audio client, capture client, wave format, and frame event. It initializes the audio capture and processing threads.
    /// </summary>
    /// <param name="audioClient">The audio client for capturing audio.</param>
    /// <param name="captureClient">The capture client for retrieving audio frames.</param>
    /// <param name="waveFormat">The wave format for the audio.</param>
    /// <param name="frameEvent">The event for signaling new audio frames.</param>
    private ProcessAudioCapture(
        IAudioClient audioClient,
        ProcessLoopbackInterop.IAudioCaptureClient captureClient,
        WaveFormat waveFormat,
        AutoResetEvent frameEvent)
    {
        // Initialize the audio client, capture client, wave format, and frame event.
        this.audioClient = audioClient;
        this.captureClient = captureClient;
        this.waveFormat = waveFormat;
        this.frameEvent = frameEvent;

        // Creating a bounded channel for audio samples with a capacity of 64. If the channel is full, the oldest samples will be dropped.
        sampleQueue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        // Starting the processing thread that will read samples from the channel and invoke the SamplesAvailable event.
        processingThread = new Thread(ProcessingLoop) { IsBackground = true, Name = "ScreenShare-ProcessAudioProcessing" };
        processingThread.Start();

        captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenShare-ProcessAudioCapture" };

        // Starting the audio client to begin capturing audio and starting the capture thread that will read audio frames from the capture client.
        audioClient.Start();
        captureThread.Start();
    }

    /// <summary>
    /// Creates a new instance of ProcessAudioCapture for the specified target process ID. It initializes the audio client and capture client, sets up the wave format, and starts capturing audio from the target process.
    /// </summary>
    /// <param name="targetProcessId">The ID of the target process to capture audio from.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The created ProcessAudioCapture instance.</returns>
    public static async Task<ProcessAudioCapture> CreateAsync(uint targetProcessId, CancellationToken cancellationToken = default)
    {
        // Create a wave format for capturing audio in IEEE float format with the specified sample rate and number of channels.
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        var frameEvent = new AutoResetEvent(false);
        IAudioClient client;
        // Activate the audio client for the target process ID and initialize it with the specified wave format and frame event.
        try
        {
            client = await ActivateAsync(targetProcessId, format, frameEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            frameEvent.Dispose();
            throw;
        }

        try
        {
            int hr = client.GetService(typeof(ProcessLoopbackInterop.IAudioCaptureClient).GUID, out object serviceObj);
            Marshal.ThrowExceptionForHR(hr);

            return new ProcessAudioCapture(client, (ProcessLoopbackInterop.IAudioCaptureClient)serviceObj, format, frameEvent);
        }
        catch
        {
            frameEvent.Dispose();
            Marshal.ReleaseComObject(client);
            throw;
        }
    }

    /// <summary>
    /// Activates the audio client for the specified target process ID, initializes it with the specified wave format and frame event, and returns the activated audio client.
    /// This method uses the ProcessLoopbackInterop to activate the audio interface asynchronously.
    /// </summary>
    /// <param name="targetProcessId">The ID of the target process to activate the audio client for.</param>
    /// <param name="format">The wave format to initialize the audio client with.</param>
    /// <param name="frameEvent">The event to signal when audio frames are available.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The activated audio client.</returns>
    /// <returns></returns>
    private static async Task<IAudioClient> ActivateAsync(uint targetProcessId, WaveFormat format, AutoResetEvent frameEvent, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IAudioClient>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new ActivationCompletionHandler(tcs, client =>
        {
            Guid sessionGuid = Guid.Empty;
            int hr = client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                    | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                1_000_000,
                0,
                format,
                ref sessionGuid);

            Marshal.ThrowExceptionForHR(hr);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(frameEvent.SafeWaitHandle.DangerousGetHandle()));
        });

        var activationParams = new ProcessLoopbackInterop.AudioClientActivationParams
        {
            ActivationType = ProcessLoopbackInterop.ActivationTypeProcessLoopback,
            TargetProcessId = targetProcessId,
            ProcessLoopbackMode = ProcessLoopbackInterop.ProcessLoopbackModeIncludeTargetProcessTree,
        };

        int paramsSize = Marshal.SizeOf<ProcessLoopbackInterop.AudioClientActivationParams>();
        nint paramsPtr = Marshal.AllocHGlobal(paramsSize);
        nint propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propVariant = new PropVariant
            {
                vt = (short)VarEnum.VT_BLOB,
                blobVal = new Blob { Length = paramsSize, Data = paramsPtr },
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            ProcessLoopbackInterop.ActivateAudioInterfaceAsync(
                ProcessLoopbackInterop.VirtualAudioDeviceProcessLoopback,
                typeof(IAudioClient).GUID,
                propVariantPtr,
                handler,
                out ProcessLoopbackInterop.IActivateAudioInterfaceAsyncOperation operation);
            Marshal.ReleaseComObject(operation);

            return cancellationToken.CanBeCanceled
                ? await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    /// <summary>
    /// Capturing loop that continuously reads available audio packets from the capture client and processes them. 
    /// It waits for new audio frames to be available or for a cancellation request to stop the loop.
    /// </summary>
    private void CaptureLoop()
    {
        WaitHandle[] waits = [frameEvent, stopCts.Token.WaitHandle];

        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits, FrameWaitTimeoutMs) == 1)
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
    /// Reading available audio packets from the capture client, computing the peak level, and writing the samples to the sample queue for processing.
    /// </summary>
    private void ReadAvailablePackets()
    {
        List<float> pending = [];

        // Get the size of the next available audio packet from the capture client. If there are no packets available, return early.
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

    /// <summary>
    /// Computes peak value from the given audio samples. 
    /// It iterates through the samples, calculates the absolute value of each sample, and keeps track of the maximum absolute value found, which represents the peak level of the audio signal.
    /// </summary>
    /// <param name="samples">The audio samples to process.</param>
    /// <returns>The peak value.</returns>
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
    /// Loops the processing of audio samples from the sample queue. 
    /// It waits for samples to be available in the queue and invokes the SamplesAvailable event with the read samples. 
    /// The loop continues until a cancellation request is received.
    /// </summary>
    private void ProcessingLoop()
    {
        try
        {
            // Wait for samples to be available in the sample queue and process them until a cancellation request is received.
            while (sampleQueue.Reader.WaitToReadAsync(stopCts.Token).AsTask().GetAwaiter().GetResult())
            {
                while (sampleQueue.Reader.TryRead(out float[]? samples))
                    SamplesAvailable?.Invoke(samples);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Diposes the ProcessAudioCapture instance, stopping the audio capture and processing threads, releasing COM objects, and disposing of resources.
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
            // Ignore exceptions during stop, as the audio client may already be stopped or disposed.
        }

        Marshal.ReleaseComObject(captureClient);
        Marshal.ReleaseComObject(audioClient);

        frameEvent.Dispose();
    }

    /// <summary>
    /// Activation completion handler that is called when the audio interface activation is completed. 
    /// It retrieves the activated audio client, initializes it, and sets the result or exception on the provided TaskCompletionSource.
    /// </summary>
    /// <param name="tcs">The task completion source.</param>
    /// <param name="initializeAction">The action to initialize the audio client.</param>
    private sealed class ActivationCompletionHandler(TaskCompletionSource<IAudioClient> tcs, Action<IAudioClient> initializeAction)
        : ProcessLoopbackInterop.IActivateAudioInterfaceCompletionHandler
    {
        public void ActivateCompleted(ProcessLoopbackInterop.IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object activateInterface);
                if (hr != 0)
                {
                    tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("Audio interface activation failed.", hr));
                    return;
                }

                var client = (IAudioClient)activateInterface;
                try
                {
                    initializeAction(client);
                    tcs.TrySetResult(client);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }
}
