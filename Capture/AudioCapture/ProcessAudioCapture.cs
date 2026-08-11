using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenShare.Plugin.Capture.AudioCapture.Interop;

namespace ScreenShare.Plugin.Capture.AudioCapture;

/// <summary>
/// Captures the render (output) audio of one specific process and its child-process tree,
/// instead of the whole system mix - e.g. "just the browser" rather than everything currently
/// playing. See <see cref="ProcessLoopbackInterop"/> for the raw COM interop this is built on and
/// why it exists as its own file rather than an upgrade to a newer NAudio.
///
/// Threading/shutdown safety mirrors <see cref="SystemAudioCapture"/> deliberately: WASAPI code
/// running on the game's render thread that blocks unboundedly is exactly what froze the game
/// once already in this project (see SystemAudioCapture's own doc comment) - so capture happens
/// on its own polling thread, samples cross to a separate processing thread via a bounded
/// drop-oldest channel before <see cref="SamplesAvailable"/> ever fires, and Dispose only ever
/// waits on bounded (1s) Joins, never an unbounded one.
/// </summary>
public sealed class ProcessAudioCapture : IAudioSource
{
    // The process-loopback virtual device doesn't support GetMixFormat (documented as
    // unsupported/E_NOTIMPL) - a format has to be supplied up front instead of queried. Matches
    // the default NAudio 3's own equivalent feature settled on.
    private const int SampleRate = 44100;
    private const int Channels = 2;
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

    /// <summary>Raised on our own processing thread, same guarantee as SystemAudioCapture.</summary>
    public event Action<float[]>? SamplesAvailable;

    private ProcessAudioCapture(IAudioClient audioClient, ProcessLoopbackInterop.IAudioCaptureClient captureClient, WaveFormat waveFormat)
    {
        this.audioClient = audioClient;
        this.captureClient = captureClient;
        this.waveFormat = waveFormat;

        sampleQueue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        processingThread = new Thread(ProcessingLoop) { IsBackground = true, Name = "ScreenShare-ProcessAudioProcessing" };
        processingThread.Start();

        captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenShare-ProcessAudioCapture" };

        audioClient.Start();
        captureThread.Start();
    }

    /// <summary>Async because WASAPI's ActivateAudioInterfaceAsync activation is inherently
    /// asynchronous (a COM completion callback, not a blocking call) - doing this synchronously
    /// on the render thread (where the "start audio capture" button lives) would risk the same
    /// class of game-freezing block this project has been careful to avoid elsewhere.</summary>
    public static async Task<ProcessAudioCapture> CreateAsync(uint targetProcessId, CancellationToken cancellationToken = default)
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        IAudioClient client = await ActivateAsync(targetProcessId, format, cancellationToken).ConfigureAwait(false);

        int hr = client.GetService(typeof(ProcessLoopbackInterop.IAudioCaptureClient).GUID, out object serviceObj);
        Marshal.ThrowExceptionForHR(hr);

        return new ProcessAudioCapture(client, (ProcessLoopbackInterop.IAudioCaptureClient)serviceObj, format);
    }

    private static async Task<IAudioClient> ActivateAsync(uint targetProcessId, WaveFormat format, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IAudioClient>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Per WASAPI's documented contract, IAudioClient.Initialize for a process-loopback client
        // MUST be called synchronously from inside ActivateCompleted, not after the fact on some
        // other thread - confirmed against NAudio 3's real (2026) implementation of this same
        // feature, which calls out this requirement explicitly.
        var handler = new ActivationCompletionHandler(tcs, client =>
        {
            Guid sessionGuid = Guid.Empty;
            int hr = client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback,
                1_000_000, // 100ms, matches SystemAudioCapture/NAudio's own WasapiCapture default
                0,
                format,
                ref sessionGuid);
            Marshal.ThrowExceptionForHR(hr);
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

            // The native call only starts the activation - the actual work (and the copy of the
            // blob it needs) happens asynchronously via the completion callback, so the unmanaged
            // buffers below must outlive this await, not just the synchronous call above.
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
        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out int packetFrames));
        while (packetFrames != 0)
        {
            Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                out nint buffer, out int framesToRead, out AudioClientBufferFlags flags, out _, out _));

            int sampleCount = framesToRead * waveFormat.Channels;
            var samples = new float[sampleCount];
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                Marshal.Copy(buffer, samples, 0, sampleCount);

            Volatile.Write(ref currentPeak, ComputePeak(samples));
            sampleQueue.Writer.TryWrite(samples);

            Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(framesToRead));
            Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
        }
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

                // The process-loopback virtual device only supports the base IAudioClient -
                // casting to IAudioClient2 here would throw (confirmed via NAudio 3's own
                // implementation notes for this exact scenario).
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
