using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenShare.Plugin.Capture.AudioCapture.Interfaces;
using ScreenShare.Plugin.Capture.AudioCapture.Interop;

namespace ScreenShare.Plugin.Capture.AudioCapture;

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

    private ProcessAudioCapture(
        IAudioClient audioClient,
        ProcessLoopbackInterop.IAudioCaptureClient captureClient,
        WaveFormat waveFormat,
        AutoResetEvent frameEvent)
    {
        this.audioClient = audioClient;
        this.captureClient = captureClient;
        this.waveFormat = waveFormat;
        this.frameEvent = frameEvent;

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

    public static async Task<ProcessAudioCapture> CreateAsync(uint targetProcessId, CancellationToken cancellationToken = default)
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        var frameEvent = new AutoResetEvent(false);
        IAudioClient client;
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
        }

        Marshal.ReleaseComObject(captureClient);
        Marshal.ReleaseComObject(audioClient);

        frameEvent.Dispose();
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
