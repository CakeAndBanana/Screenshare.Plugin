using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX;
using SharpDX.Direct3D11;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Network media-plane counterpart to <see cref="H264Encoder"/>: takes arbitrary H264 byte
/// chunks fed via <see cref="Feed"/> (as they arrive off a MediaSocketClient), decodes them with
/// an ffmpeg subprocess, and uploads each decoded frame to a shared texture for display.
/// <see cref="Feed"/> is safe to call from the network receive thread - it only ever enqueues
/// onto a bounded channel, decoupling the network receive thread from the ffmpeg subprocess
/// (a direct pipe between them caused a real multi-stage deadlock during this pipeline's
/// original development).
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private const int FeedChannelCapacity = 256;

    /// <summary>How much decoded video to hold before it's actually shown, absorbing network/
    /// decode jitter so playback runs on a steady clock instead of however bursty frame arrival
    /// happened to be. Without this, each frame was written to the display texture the instant it
    /// finished decoding - since decode completion directly follows network arrival timing (itself
    /// bursty: bounded drop-oldest queues at multiple hops, WebSocket message boundaries, etc.),
    /// playback visibly sped up through arrival bursts and slowed down through gaps, rather than
    /// running at a steady pace. 200ms is enough margin for typical jitter without adding much
    /// perceptible extra latency on top of it.</summary>
    private const int PresentationBufferMilliseconds = 200;

    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly Texture2D decodedTexture;
    private readonly FfmpegProcessRunner decoderProcess;
    private readonly Channel<byte[]> feedChannel;
    private readonly Channel<byte[]> presentationQueue;
    private readonly int presentationCapacity;
    private readonly int frameIntervalMs;
    private readonly Thread feedWriterThread;
    private readonly Thread readerThread;
    private readonly Thread displayThread;
    private volatile bool running;
    private volatile bool displayStarted;
    private int errorRecorded;

    private readonly Stopwatch sessionStopwatch = Stopwatch.StartNew();

    private long framesDecoded;
    private long droppedChunks;
    private long displayQueueDrops;

    public int Width { get; }

    public int Height { get; }

    public nint SharedHandle { get; }

    public long FramesDecoded => Interlocked.Read(ref framesDecoded);

    /// <summary>Average decode rate since this decoder was constructed - not an instantaneous
    /// rate, but a simple, cheap signal for "is decode actually keeping up with the stream's
    /// target FPS overall" without needing a rolling window. Directly comparable to
    /// H264Encoder.AverageFps on the host's side: if this is meaningfully lower than the
    /// configured target FPS while HostQueueDrops/DroppedChunks are both near zero, decode itself
    /// (not the network, not the encoder) is the bottleneck - most likely relevant for software
    /// decode of a CPU-expensive codec, though -hwaccel d3d11va should mostly rule that out.</summary>
    public double AverageFps => FramesDecoded / Math.Max(sessionStopwatch.Elapsed.TotalSeconds, 0.001);

    /// <summary>Fires exactly once, when the presentation buffer has filled enough to start
    /// playback smoothly (see <see cref="PresentationBufferMilliseconds"/>) - not the same moment
    /// this decoder is constructed (which happens as soon as VideoMetadata arrives, well before
    /// ffmpeg has actually spun up and produced anything) or the moment the first frame decodes
    /// (too early - there's no jitter margin yet). Actual display doesn't start until
    /// <see cref="StartDisplay"/> is called; used to synchronize with audio playback start the
    /// same way AudioPlaybackDecoder.ReadyToPlay does, so both start together rather than
    /// whichever buffer happened to fill first.</summary>
    public event Action? ReadyToDisplay;

    /// <summary>How many incoming network chunks got dropped because ffmpeg's decode couldn't
    /// keep up in real time (feedChannel filling up faster than FeedWriterLoop could hand chunks
    /// to the decoder's stdin) - the signal to check if playback looks like it's stalling and
    /// bursting rather than playing smoothly: a high value here means the decoder itself is the
    /// bottleneck (e.g. software HEVC decode being more expensive than the viewer's CPU can do in
    /// real time), not the network or the encoder.</summary>
    public long DroppedChunks => Interlocked.Read(ref droppedChunks);

    /// <summary>How many decoded frames got evicted from the presentation buffer before ever
    /// being shown, because decode is racing far enough ahead of the paced display clock to fill
    /// it faster than DisplayLoop can drain it. Should normally stay near zero (decode is
    /// throttled by the same network arrival rate driving display); a high value points at
    /// FeedWriterLoop/decode momentarily bursting well beyond the target fps.</summary>
    public long DisplayQueueDrops => Interlocked.Read(ref displayQueueDrops);

    public Exception? LastError { get; private set; }

    public H264Decoder(GameAdapterDevice gameAdapterDevice, string ffmpegExePath, int width, int height, int fps, VideoCodec codec = VideoCodec.H264)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        Width = width;
        Height = height;

        Device device = gameAdapterDevice.Device;
        decodedTexture = SharedTextureFactory.CreateEmpty(device, Width, Height);
        SharedHandle = SharedTextureFactory.GetSharedHandle(decodedTexture);

        frameIntervalMs = Math.Max(1, 1000 / Math.Max(1, fps));
        presentationCapacity = Math.Clamp(fps * PresentationBufferMilliseconds / 1000, 2, 15);
        presentationQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(presentationCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        feedChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(FeedChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // -hwaccel d3d11va offloads the actual decode work to the GPU's dedicated video decode
        // hardware (works uniformly across NVIDIA/AMD/Intel via Windows' own D3D11 Video
        // Acceleration API, unlike encode's per-vendor NVENC/AMF/QSV selection) - decode still had
        // no hardware path at all before this, always running ffmpeg's plain software decoder
        // regardless of codec. That mattered little for H264 but made HEVC (meaningfully more
        // CPU-expensive to decode in software) prone to falling behind real-time on the viewer's
        // CPU, which is already busy running the game - producing exactly the "freezes, catches
        // up in a burst, freezes again" pattern a real in-game HEVC test surfaced. Verified byte-
        // for-byte identical output against software decode for both H264 and HEVC using this
        // exact bundled ffmpeg binary before adding this, not assumed.
        string decodeArgs = $"-hide_banner -loglevel error -hwaccel d3d11va -f {VideoEncoderSelector.RawFormatName(codec)} -i - -f rawvideo -pix_fmt bgra -";
        decoderProcess = new FfmpegProcessRunner(ffmpegExePath, decodeArgs);

        running = true;
        feedWriterThread = new Thread(FeedWriterLoop) { IsBackground = true, Name = "ScreenShare-H264DecFeed" };
        readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "ScreenShare-H264DecRead" };
        displayThread = new Thread(DisplayLoop) { IsBackground = true, Name = "ScreenShare-H264Display" };
        feedWriterThread.Start();
        readerThread.Start();
        displayThread.Start();
    }

    /// <summary>Begins draining the presentation buffer onto the display texture at the paced
    /// target-fps rate - held off until called so playback can be started in lockstep with audio
    /// (see MediaSessionService's sync coordination) rather than the instant the buffer fills.
    /// Idempotent and safe to call more than once (e.g. if audio joins the stream mid-session,
    /// after video is already playing).</summary>
    public void StartDisplay() => displayStarted = true;

    /// <summary>Never blocks - safe to call from a network receive thread.</summary>
    public void Feed(byte[] chunk)
    {
        if (feedChannel.Reader.Count >= FeedChannelCapacity)
            Interlocked.Increment(ref droppedChunks);

        feedChannel.Writer.TryWrite(chunk);
    }

    private void FeedWriterLoop()
    {
        try
        {
            while (running && feedChannel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (feedChannel.Reader.TryRead(out byte[]? chunk))
                    decoderProcess.StandardInput.Write(chunk, 0, chunk.Length);
            }
        }
        catch (Exception ex) when (running)
        {
            RecordError("FeedWriterLoop (network -> decoder stdin)", ex);
        }
        finally
        {
            TryClose(decoderProcess.StandardInput);
        }
    }

    /// <summary>Reads each raw decoded frame from ffmpeg as soon as it's available and hands it
    /// to the presentation buffer - deliberately NOT written to the display texture here anymore.
    /// Decode completion tracks network arrival timing (itself bursty), so displaying immediately
    /// on this thread made playback speed follow that jitter directly; DisplayLoop now owns
    /// actually showing frames, on its own steady clock.</summary>
    private void ReadLoop()
    {
        int frameSize = Width * Height * 4;
        byte[] frame = new byte[frameSize];
        try
        {
            while (running)
            {
                int offset = 0;
                while (offset < frameSize)
                {
                    int read = decoderProcess.StandardOutput.Read(frame, offset, frameSize - offset);
                    if (read <= 0)
                    {
                        if (running)
                            decoderProcess.ThrowIfFailed();
                        return;
                    }
                    offset += read;
                }

                Interlocked.Increment(ref framesDecoded);

                if (presentationQueue.Reader.Count >= presentationCapacity)
                    Interlocked.Increment(ref displayQueueDrops);
                presentationQueue.Writer.TryWrite((byte[])frame.Clone());
            }
        }
        catch (Exception ex) when (running)
        {
            RecordError("ReadLoop (decoder stdout -> presentation buffer)", ex);
        }
    }

    /// <summary>Drains the presentation buffer onto the display texture at a steady, paced clock
    /// (one frame every frameIntervalMs) rather than whenever decode happens to produce one - this
    /// is what actually fixes playback speeding up/slowing down with network jitter. Waits for the
    /// buffer to fill before raising <see cref="ReadyToDisplay"/>, then waits again for
    /// <see cref="StartDisplay"/> so playback can begin in lockstep with audio. On an underrun
    /// (buffer empty - decode/network genuinely falling behind, a distinct problem from jitter)
    /// this just holds the last displayed frame and waits for the next tick rather than blocking
    /// indefinitely or busy-spinning.
    ///
    /// Paced against a Stopwatch schedule (target = frameIndex * frameIntervalMs) rather than a
    /// plain Thread.Sleep(frameIntervalMs) each iteration - the latter doesn't account for how
    /// long WriteFrameToTexture itself takes (a lock plus real GPU calls, non-zero real work), so
    /// each loop iteration actually took slightly LONGER than frameIntervalMs and the error
    /// compounded every single frame. Over a real session that added up to video visibly
    /// lagging further and further behind audio's hardware-clocked, drift-free playback -
    /// confirmed as the actual cause by a real report of video specifically falling behind.
    /// Scheduling against elapsed time instead means a slow iteration just eats into (or, if
    /// there's a burst of them, temporarily eliminates) the next frame's sleep rather than
    /// permanently pushing every future frame later.</summary>
    private void DisplayLoop()
    {
        try
        {
            while (running && presentationQueue.Reader.Count < presentationCapacity)
                Thread.Sleep(5);
            if (!running)
                return;

            ReadyToDisplay?.Invoke();

            while (running && !displayStarted)
                Thread.Sleep(5);

            var clock = Stopwatch.StartNew();
            long frameIndex = 0;

            while (running)
            {
                if (presentationQueue.Reader.TryRead(out byte[]? frame))
                    WriteFrameToTexture(frame);

                frameIndex++;
                long sleepMs = frameIndex * frameIntervalMs - clock.ElapsedMilliseconds;
                if (sleepMs > 0)
                    Thread.Sleep((int)sleepMs);
                // else already behind schedule - loop again immediately rather than sleeping,
                // which is how a burst of slow iterations catches back up instead of the delay
                // becoming permanent.
            }
        }
        catch (Exception ex) when (running)
        {
            RecordError("DisplayLoop (presentation buffer -> shared texture)", ex);
        }
    }

    private void WriteFrameToTexture(byte[] frame)
    {
        GCHandle pinned = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            lock (gameAdapterDevice.ImmediateContextLock)
            {
                var dataBox = new DataBox(pinned.AddrOfPinnedObject(), Width * 4, 0);
                gameAdapterDevice.Device.ImmediateContext.UpdateSubresource(dataBox, decodedTexture);

                // decodedTexture is a classic (non-keyed-mutex) shared resource opened on a
                // SEPARATE device by the game's own render thread (see SharedTextureBridge) -
                // without an explicit Flush here, this UpdateSubresource can sit queued in the
                // driver's command buffer indefinitely, invisible to the other device until
                // something UNRELATED happens to trigger a flush. That's exactly consistent
                // with "sometimes gets a frame" rather than a clean success/failure - it's not
                // broken, it's just unsynchronized. Flush forces this write to actually reach
                // the GPU now, not whenever the driver next feels like it.
                gameAdapterDevice.Device.ImmediateContext.Flush();
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private void RecordError(string stage, Exception ex)
    {
        ServiceCollector.PluginLog.Error(ex, $"ScreenShare H264Decoder error in {stage}");
        if (Interlocked.CompareExchange(ref errorRecorded, 1, 0) == 0)
            LastError = ex;
        running = false;
    }

    private static void TryClose(Stream stream)
    {
        try
        {
            stream.Close();
        }
        catch
        {
            // other side of the pipe may already be gone
        }
    }

    public void Dispose()
    {
        running = false;
        feedChannel.Writer.TryComplete();
        presentationQueue.Writer.TryComplete();
        feedWriterThread.Join(1000);
        readerThread.Join(1000);
        displayThread.Join(1000);
        decoderProcess.Dispose();
        decodedTexture.Dispose();
    }
}
