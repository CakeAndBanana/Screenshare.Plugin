using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX;
using SharpDX.Direct3D11;

namespace ScreenShare.Plugin.Encoding;

public sealed class H264Decoder : IVideoDecoder
{
    private const int FeedChannelCapacity = 256;

    internal const int PresentationBufferMilliseconds = 200;

    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly Texture2D decodedTexture;
    private readonly FfmpegProcessRunner decoderProcess;
    private readonly Channel<(long PtsMs, byte[] Chunk)> feedChannel;

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

    public string DecoderName { get; }

    public long FramesDecoded => Interlocked.Read(ref framesDecoded);

    public double AverageFps => FramesDecoded / Math.Max(sessionStopwatch.Elapsed.TotalSeconds, 0.001);

    public event Action? ReadyToDisplay
    {
        add => scheduler.ReadyToDisplay += value;
        remove => scheduler.ReadyToDisplay -= value;
    }

    public long DroppedChunks => Interlocked.Read(ref droppedChunks);

    public long DisplayQueueDrops => scheduler.QueueDrops;

    public Exception? LastError { get; private set; }

    public long AvDriftMs => scheduler.AvDriftMs;

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

    public void StartDisplay() => scheduler.StartDisplay();

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
