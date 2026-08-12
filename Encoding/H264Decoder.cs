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

    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly Texture2D decodedTexture;
    private readonly FfmpegProcessRunner decoderProcess;
    private readonly Channel<byte[]> feedChannel;
    private readonly Thread feedWriterThread;
    private readonly Thread readerThread;
    private volatile bool running;
    private int errorRecorded;

    private long framesDecoded;
    private long droppedChunks;

    public int Width { get; }

    public int Height { get; }

    public nint SharedHandle { get; }

    public long FramesDecoded => Interlocked.Read(ref framesDecoded);

    /// <summary>Fires exactly once, the first time a frame is actually decoded and written to
    /// <see cref="SharedHandle"/>'s texture - not the same moment this decoder is constructed
    /// (which happens as soon as VideoMetadata arrives, well before ffmpeg has actually spun up
    /// and produced anything). Used to synchronize audio playback start with video actually
    /// being visible, since video's startup latency (spawning ffmpeg, initializing hardware
    /// decode, waiting for the first keyframe) is normally much longer than audio's - starting
    /// audio the instant ITS OWN buffer is ready, independent of video, was producing an
    /// audible/visible desync at the start of playback.</summary>
    public event Action? FirstFrameDecoded;

    /// <summary>How many incoming network chunks got dropped because ffmpeg's decode couldn't
    /// keep up in real time (feedChannel filling up faster than FeedWriterLoop could hand chunks
    /// to the decoder's stdin) - the signal to check if playback looks like it's stalling and
    /// bursting rather than playing smoothly: a high value here means the decoder itself is the
    /// bottleneck (e.g. software HEVC decode being more expensive than the viewer's CPU can do in
    /// real time), not the network or the encoder.</summary>
    public long DroppedChunks => Interlocked.Read(ref droppedChunks);

    public Exception? LastError { get; private set; }

    public H264Decoder(GameAdapterDevice gameAdapterDevice, string ffmpegExePath, int width, int height, VideoCodec codec = VideoCodec.H264)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        Width = width;
        Height = height;

        Device device = gameAdapterDevice.Device;
        decodedTexture = SharedTextureFactory.CreateEmpty(device, Width, Height);
        SharedHandle = SharedTextureFactory.GetSharedHandle(decodedTexture);

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
        feedWriterThread.Start();
        readerThread.Start();
    }

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

    private void ReadLoop()
    {
        int frameSize = Width * Height * 4;
        byte[] frame = new byte[frameSize];
        GCHandle pinned = GCHandle.Alloc(frame, GCHandleType.Pinned);
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

                if (Interlocked.Increment(ref framesDecoded) == 1)
                    FirstFrameDecoded?.Invoke();
            }
        }
        catch (Exception ex) when (running)
        {
            RecordError("ReadLoop (decoder stdout -> shared texture)", ex);
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
        feedWriterThread.Join(1000);
        readerThread.Join(1000);
        decoderProcess.Dispose();
        decodedTexture.Dispose();
    }
}
