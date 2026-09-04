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
/// on its own dedicated thread, samples cross to a separate processing thread via a bounded
/// drop-oldest channel before <see cref="SamplesAvailable"/> ever fires, and Dispose only ever
/// waits on bounded (1s) Joins, never an unbounded one.
///
/// Unlike <see cref="SystemAudioCapture"/>, this one is EVENT-DRIVEN rather than polled - see
/// <see cref="FrameWaitTimeoutMs"/> for the measurements that forced that difference.
/// </summary>
public sealed class ProcessAudioCapture : IAudioSource
{
    // The process-loopback virtual device doesn't support GetMixFormat (documented as
    // unsupported/E_NOTIMPL) - a format has to be supplied up front instead of queried, so unlike
    // SystemAudioCapture there's no device-native rate to negotiate around: we choose the rate
    // outright. 48000 (not NAudio 3's 44100 default) specifically because Opus only accepts
    // 8/12/16/24/48 kHz - 44100 would throw constructing OpusFrameEncoder ("Sample rate is
    // invalid...") - and 48 kHz is Opus's own native internal rate besides.
    private const int SampleRate = 48000;
    private const int Channels = 2;

    // Only a safety net, NOT the normal wake-up path: WASAPI signals frameEvent every device
    // period (~10ms) and that is what actually drives CaptureLoop. This bounds how long a stalled
    // event could go unnoticed, and keeps the loop responsive to cancellation.
    //
    // This class used to poll on a 50ms timer exactly like SystemAudioCapture does, and THAT was
    // the real cause of the long-standing "robotic/glitchy audio, but only when targeting one
    // process" bug (two earlier fix attempts - coalescing packets, then adding
    // AutoConvertPcm|SrcDefaultQuality - both missed it). Measured against a known-good 440Hz sine
    // rendered by the capture target itself:
    //
    //     50ms polling  -> 63.3% of real time captured, 51 waveform splices in 3s
    //     10ms polling  -> 99.5% of real time captured,  1 splice (stream start)
    //     event-driven  -> 99.8% of real time captured,  1 splice (stream start)
    //
    // The reason is that the process-loopback virtual device IGNORES the 100ms hnsBufferDuration
    // requested below (a real render endpoint honors it, which is why system-wide capture was
    // always fine on the same 50ms poll). Its buffer measured out at exactly 3 packets of 512
    // frames - about 32ms - so a 50ms poll overruns it every single time and WASAPI silently
    // discards the excess: 32ms of audio kept per 50ms of real time, spliced end to end, which is
    // exactly 63%. Nothing downstream could detect it either - the discarded frames are not
    // flagged AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY (measured: zero discontinuity flags across
    // all three runs), so the samples arrive looking perfectly well-formed.
    //
    // Event-driven capture is also what Microsoft's own ApplicationLoopback sample does for this
    // exact API, and it self-tunes to whatever period the device actually uses instead of hoping a
    // hard-coded interval stays under an unqueryable buffer size.
    private const int FrameWaitTimeoutMs = 100;

    private readonly IAudioClient audioClient;
    private readonly ProcessLoopbackInterop.IAudioCaptureClient captureClient;
    private readonly WaveFormat waveFormat;

    /// <summary>Signalled by WASAPI once per device period. Its raw handle is held by the audio
    /// engine for as long as the client is running, so it must outlive audioClient.Stop().</summary>
    private readonly AutoResetEvent frameEvent;
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

    /// <summary>Async because WASAPI's ActivateAudioInterfaceAsync activation is inherently
    /// asynchronous (a COM completion callback, not a blocking call) - doing this synchronously
    /// on the render thread (where the "start audio capture" button lives) would risk the same
    /// class of game-freezing block this project has been careful to avoid elsewhere.</summary>
    public static async Task<ProcessAudioCapture> CreateAsync(uint targetProcessId, CancellationToken cancellationToken = default)
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        // Created before activation because Initialize/SetEventHandle happen inside the activation
        // callback and need it already in hand. Owned by the returned instance on success; on any
        // failure along the way it has to be disposed here, since nothing else ever gets a
        // reference to it.
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

        // Per WASAPI's documented contract, IAudioClient.Initialize for a process-loopback client
        // MUST be called synchronously from inside ActivateCompleted, not after the fact on some
        // other thread - confirmed against NAudio 3's real (2026) implementation of this same
        // feature, which calls out this requirement explicitly.
        var handler = new ActivationCompletionHandler(tcs, client =>
        {
            Guid sessionGuid = Guid.Empty;
            int hr = client.Initialize(
                AudioClientShareMode.Shared,
                // AutoConvertPcm | SrcDefaultQuality matches SystemAudioCapture's own Initialize
                // call - originally omitted here on the theory that the process-loopback virtual
                // device doesn't support GetMixFormat, so there'd be nothing to convert FROM. That
                // theory was never actually verified against real hardware, and a real report of
                // extremely glitchy/"robotic" audio specifically when targeting one process (never
                // when using system-wide capture, which already has these flags) points at it
                // being wrong: the virtual device is still a view into a real device's actual mix
                // format, so without these flags WASAPI can hand back samples in a format that
                // doesn't match what was requested, and this class was blindly copying those bytes
                // into a float[] as if it did - exactly what a garbled/robotic artifact looks like,
                // as opposed to the choppiness a timing/buffering issue would cause instead.
                // EventCallback is what makes the capture thread wake once per device period
                // instead of on a fixed timer - see FrameWaitTimeoutMs for the measurements behind
                // that, and why a fixed timer was destroying a third of the audio here.
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                    | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                // Requested, but NOT honored by this virtual device (measured at ~32ms actual) -
                // harmless now that nothing depends on the buffer being big enough to poll against.
                1_000_000, // 100ms, matches SystemAudioCapture/NAudio's own WasapiCapture default
                0, // shared-mode event-driven streams require a zero periodicity
                format,
                ref sessionGuid);
            Marshal.ThrowExceptionForHR(hr);

            // Must come after Initialize and before Start, and (like Initialize itself) inside the
            // activation callback. DangerousGetHandle is safe here specifically because the handle
            // is kept alive by the AutoResetEvent that CreateAsync hands to the instance, which
            // outlives audioClient.Stop() - see Dispose.
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
        // Waiting on the cancellation token's own handle alongside the frame event means Dispose
        // interrupts the wait immediately rather than after FrameWaitTimeoutMs, which is what the
        // old poll loop got from WaitOne(token.WaitHandle) and what keeps Dispose's bounded Join
        // from ever actually needing its full second.
        WaitHandle[] waits = [frameEvent, stopCts.Token.WaitHandle];

        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                // WaitTimeout falls through to a read as well: draining on a missed event is
                // strictly better than skipping one, and it degrades to (slow) polling rather than
                // going silent if the event ever stops arriving.
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

    /// <summary>Coalesces every WASAPI packet available on THIS wake-up into a single combined
    /// array before queuing it, rather than queuing one entry per packet (which is what this used
    /// to do). This isn't just tidiness - sampleQueue's capacity is measured in ITEMS, not audio
    /// duration, so queuing many small per-packet entries instead of one larger one per wake-up
    /// gives this capture path meaningfully LESS actual buffered-audio headroom for the same
    /// nominal queue size before DropOldest starts discarding audio. Matches what NAudio's own
    /// WasapiCapture does internally for system-wide capture (confirmed by decompiling it).
    ///
    /// Note this was originally committed as the fix for the per-process jitter, and it wasn't -
    /// see FrameWaitTimeoutMs for what actually was. It's kept because the queue-headroom
    /// reasoning above holds on its own merits.</summary>
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

        // Strictly last: the audio engine holds this event's raw handle for as long as the client
        // is running, so it can only be closed once the client is stopped and released. If the
        // bounded Join above timed out and the capture thread is somehow still inside WaitAny,
        // this surfaces there as an ObjectDisposedException, which CaptureLoop already catches
        // into LastError and exits on - noisy in a log, but not a hang and not a crash, which is
        // the same trade the bounded Joins themselves are making.
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
