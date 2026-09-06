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
public sealed class H264Decoder : IVideoDecoder
{
    private const int FeedChannelCapacity = 256;

    /// <summary>How much decoded video to hold before it's actually shown, absorbing network/
    /// decode jitter so playback runs on a steady clock instead of however bursty frame arrival
    /// happened to be. Without this, each frame was written to the display texture the instant it
    /// finished decoding - since decode completion directly follows network arrival timing (itself
    /// bursty: bounded drop-oldest queues at multiple hops, WebSocket message boundaries, etc.),
    /// playback visibly sped up through arrival bursts and slowed down through gaps, rather than
    /// running at a steady pace. 200ms is enough margin for typical jitter without adding much
    /// perceptible extra latency on top of it.
    ///
    /// Internal rather than private because it is also AudioPlaybackDecoder's latency budget: this
    /// queue is bounded drop-oldest, so frames older than this are gone, and an audio clock that
    /// sits further back than this reach asks for frames that no longer exist. The two constants
    /// have to move together.</summary>
    internal const int PresentationBufferMilliseconds = 200;

    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly Texture2D decodedTexture;
    private readonly FfmpegProcessRunner decoderProcess;
    private readonly Channel<(long PtsMs, byte[] Chunk)> feedChannel;

    /// <summary>Owns the jitter buffer and the audio-clock pacing; this class only decodes into it.
    /// Shared with the Media Foundation decoder rather than duplicated - see the type's own
    /// comment for why that logic exists exactly once.</summary>
    private readonly PresentationScheduler<byte[]> scheduler;

    private readonly Thread feedWriterThread;
    private readonly Thread readerThread;
    private volatile bool running;
    private int errorRecorded;

    private readonly Stopwatch sessionStopwatch = Stopwatch.StartNew();

    private long framesDecoded;
    private long droppedChunks;
    private long lastFedPtsMs;

    public int Width { get; }

    public int Height { get; }

    public nint SharedHandle { get; }

    /// <summary>Names the codec ffmpeg was asked for, which is as specific as this path can be -
    /// ffmpeg picks its own decoder internally and never says which. The Media Foundation path
    /// reports the actual MFT's name.</summary>
    public string DecoderName { get; }

    public long FramesDecoded => Interlocked.Read(ref framesDecoded);

    /// <summary>Average decode rate since this decoder was constructed - not an instantaneous
    /// rate, but a simple, cheap signal for "is decode actually keeping up with the stream's
    /// target FPS overall" without needing a rolling window. Directly comparable to
    /// H264Encoder.AverageFps on the host's side: if this is meaningfully lower than the
    /// configured target FPS while HostQueueDrops/DroppedChunks are both near zero, decode itself
    /// (not the network, not the encoder) is the bottleneck - most likely relevant for software
    /// decode of a CPU-expensive codec, though -hwaccel d3d11va should mostly rule that out.</summary>
    public double AverageFps => FramesDecoded / Math.Max(sessionStopwatch.Elapsed.TotalSeconds, 0.001);

    /// <summary>See PresentationScheduler.ReadyToDisplay.</summary>
    public event Action? ReadyToDisplay
    {
        add => scheduler.ReadyToDisplay += value;
        remove => scheduler.ReadyToDisplay -= value;
    }

    /// <summary>How many incoming network chunks got dropped because ffmpeg's decode couldn't
    /// keep up in real time (feedChannel filling up faster than FeedWriterLoop could hand chunks
    /// to the decoder's stdin) - the signal to check if playback looks like it's stalling and
    /// bursting rather than playing smoothly: a high value here means the decoder itself is the
    /// bottleneck (e.g. software HEVC decode being more expensive than the viewer's CPU can do in
    /// real time), not the network or the encoder.</summary>
    public long DroppedChunks => Interlocked.Read(ref droppedChunks);

    /// <summary>See PresentationScheduler.QueueDrops.</summary>
    public long DisplayQueueDrops => scheduler.QueueDrops;

    public Exception? LastError { get; private set; }

    /// <summary>See PresentationScheduler.AvDriftMs.</summary>
    public long AvDriftMs => scheduler.AvDriftMs;

    /// <summary><paramref name="getAudioPtsMs"/> lets DisplayLoop ask, at any moment, "what PTS is
    /// audio currently playing" (null if there's no audio, or it hasn't started yet) - queried
    /// fresh every frame rather than captured once, so it stays correct even if audio gets wired
    /// in or torn down mid-session. See MediaSessionService, which supplies this as
    /// <c>() =&gt; audioDecoder?.EstimatedCurrentPtsMs</c>.
    ///
    /// <paramref name="getSyncOffsetMs"/> is the user's A/V offset in milliseconds - positive holds
    /// video back. Passed as a provider rather than folded into the clock by the caller because it
    /// has to influence three things that all have to agree: the clock comparison, the cap on how
    /// long a frame may be held, and how deep the presentation queue is built. Shifting only the
    /// clock (which is what an earlier attempt did) moves the comparison while leaving the other two
    /// at their defaults, so the offset gets clamped away and appears to do almost nothing.</summary>
    public H264Decoder(
        GameAdapterDevice gameAdapterDevice,
        string ffmpegExePath,
        int width,
        int height,
        int fps,
        VideoCodec codec,
        Func<long?> getAudioPtsMs,
        Func<int>? getSyncOffsetMs = null,
        int audioLatencyBudgetMs = PresentationBufferMilliseconds)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        Width = width;
        Height = height;
        DecoderName = $"ffmpeg ({codec})";

        Device device = gameAdapterDevice.Device;
        decodedTexture = SharedTextureFactory.CreateEmpty(device, Width, Height);
        SharedHandle = SharedTextureFactory.GetSharedHandle(decodedTexture);

        // Deep enough to cover the deliberate offset on top of the normal jitter budget. Delaying
        // video by N ms means N ms of frames have to be held somewhere, and this queue is the only
        // place they can be - at the default depth, anything past ~200ms was simply dropped as
        // drop-oldest before it could ever be shown late.
        // Capped at 45 frames because these are raw BGRA - roughly 8MB each at 1080p - so the
        // queue's memory cost is the real limit on how far video can be held back, not the logic.
                // Also tracks the AUDIO latency budget, because audio is the master clock: if the clock is
        // allowed to sit 500ms behind live, this queue must still be holding frames that old, or
        // the display loop asks for frames that were already discarded. The two genuinely have to
        // move together, which is why the audio budget is passed in rather than assumed.
        int bufferedMs = Math.Max(PresentationBufferMilliseconds, audioLatencyBudgetMs)
                         + Math.Max(0, getSyncOffsetMs?.Invoke() ?? 0);
        scheduler = new PresentationScheduler<byte[]>(
            fps,
            Math.Clamp(fps * bufferedMs / 1000, 2, 45),
            getAudioPtsMs,
            getSyncOffsetMs ?? (static () => 0),
            WriteFrameToTexture,
            discard: null,   // plain managed arrays; nothing to hand back
            RecordError,
            "ScreenShare-H264Display");

        feedChannel = Channel.CreateBounded<(long, byte[])>(new BoundedChannelOptions(FeedChannelCapacity)
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
        feedWriterThread.Start();
        readerThread.Start();
    }

    /// <summary>See PresentationScheduler.StartDisplay.</summary>
    public void StartDisplay() => scheduler.StartDisplay();

    /// <summary>Never blocks - safe to call from a network receive thread. <paramref name="ptsMs"/>
    /// is the chunk's PTS on the host's shared session clock (see MediaSocketClient/H264Encoder) -
    /// carried through to whichever decoded frame comes out the other side of ffmpeg, so DisplayLoop
    /// can compare it against audio's own position.</summary>
    public void Feed(long ptsMs, byte[] chunk)
    {
        if (feedChannel.Reader.Count >= FeedChannelCapacity)
            Interlocked.Increment(ref droppedChunks);

        feedChannel.Writer.TryWrite((ptsMs, chunk));
    }

    private void FeedWriterLoop()
    {
        try
        {
            while (running && feedChannel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (feedChannel.Reader.TryRead(out (long PtsMs, byte[] Chunk) entry))
                {
                    // Published for ReadLoop to snapshot against each decoded frame - see its own
                    // comment for why "most recently fed chunk's PTS" is a reasonable proxy for a
                    // frame's actual capture time without needing to parse NAL unit boundaries.
                    Interlocked.Exchange(ref lastFedPtsMs, entry.PtsMs);
                    decoderProcess.StandardInput.Write(entry.Chunk, 0, entry.Chunk.Length);
                }

                // Not optional, and the reason decode used to stall in bursts. Process redirects
                // stdin through a FileStream with a 4096-byte write buffer, so a write smaller
                // than that is simply held - measured, not assumed: 500 bytes written to a child's
                // BaseStream never arrived until an explicit Flush. It went unnoticed while chunks
                // came off ffmpeg's stdout in large reads that mostly exceeded the buffer, but the
                // Media Foundation encoder sends one access unit per chunk, and a P-frame on a
                // quiet screen is a few hundred bytes. Worse than latency alone: a large write
                // whose tail lands under the threshold leaves a PARTIAL access unit stranded, so
                // the decoder sits waiting for the rest of a frame it will not see until the next
                // one pushes it through.
                //
                // Once per drained batch rather than per chunk - a batch is the work that just
                // arrived together, so this costs one syscall per wake-up, not one per frame.
                decoderProcess.StandardInput.Flush();
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
    /// actually showing frames, on its own steady clock.
    ///
    /// Each frame is paired with a PTS snapshot from FeedWriterLoop ("the most recently fed
    /// chunk's timestamp"), not a frame-exact one - ffmpeg's decode pipeline doesn't preserve
    /// arbitrary per-chunk metadata through to its raw output, and chunks aren't NAL-unit-aligned
    /// to begin with. Given -tune ll's small encode/decode buffering, this approximation is off by
    /// at most a few frames' worth of latency, well within the tolerance DisplayLoop's
    /// reconciliation already applies - good enough to correct real drift without needing full
    /// bitstream parsing to get frame-perfect timestamps.</summary>
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
                scheduler.Enqueue(Interlocked.Read(ref lastFedPtsMs), (byte[])frame.Clone());
            }
        }
        catch (Exception ex) when (running)
        {
            RecordError("ReadLoop (decoder stdout -> presentation buffer)", ex);
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

    /// <summary>See the matching comment on H264Encoder.Dispose() - same reasoning, mirrored here:
    /// displayThread is the one thread that touches the shared GPU texture (via
    /// WriteFrameToTexture), so its Join result specifically gates whether decodedTexture.Dispose()
    /// is safe to run. If the GPU is stalled and displayThread is still stuck past the 1s timeout,
    /// disposing decodedTexture out from under it risks a native use-after-dispose crash once the
    /// stuck call eventually returns - leaking it in that rare case is the safer outcome.</summary>
    public void Dispose()
    {
        running = false;
        feedChannel.Writer.TryComplete();
        scheduler.Stop();
        feedWriterThread.Join(1000);
        readerThread.Join(1000);
        bool displayExited = scheduler.Join(1000);
        scheduler.Dispose();
        decoderProcess.Dispose();

        if (!displayExited)
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare H264Decoder's display thread didn't exit within 1s (likely a stalled/hung GPU) - " +
                "leaking its shared texture rather than risking a use-after-dispose crash.");
            return;
        }

        decodedTexture.Dispose();
    }
}
