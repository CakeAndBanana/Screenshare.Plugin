using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX;
using SharpDX.Direct3D11;

namespace ScreenShare.Plugin.Encoding.H264;

/// <summary>
/// H.264 video decoder that uses ffmpeg to decode H.264 video streams and presents them using a shared Direct3D texture.
/// </summary>
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
    private volatile bool disposed;
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

    /// <summary>
    /// Creates a new H.264 decoder that uses ffmpeg to decode H.264 video streams and presents them using a shared Direct3D texture.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device.</param>
    /// <param name="ffmpegExePath">The path to the ffmpeg executable.</param>
    /// <param name="width">The width of the decoded video.</param>
    /// <param name="height">The height of the decoded video.</param>
    /// <param name="fps">The frame rate of the decoded video.</param>
    /// <param name="codec">The video codec to decode.</param>
    /// <param name="getAudioPtsMs">A function to get the audio presentation timestamp in milliseconds.</param>
    /// <param name="getSyncOffsetMs">A function to get the synchronization offset in milliseconds.</param>
    /// <param name="audioLatencyBudgetMs">The audio latency budget in milliseconds.</param>
    public H264Decoder(
        GameAdapterDevice gameAdapterDevice,
        string ffmpegExePath,
        int width,
        int height,
        int fps,
        SupportedVideoCodec codec,
        Func<long?> getAudioPtsMs,
        Func<int>? getSyncOffsetMs = null,
        int audioLatencyBudgetMs = PresentationBufferMilliseconds)
    {
        // Validate input parameters
        this.gameAdapterDevice = gameAdapterDevice;
        Width = width;
        Height = height;
        DecoderName = $"ffmpeg ({codec})";

        // Create a shared texture for the decoded video frames
        Device device = gameAdapterDevice.Device;
        decodedTexture = SharedTextureFactory.CreateEmpty(device, Width, Height);
        SharedHandle = SharedTextureFactory.GetSharedHandle(decodedTexture);

        // Calculate the buffered milliseconds for the presentation scheduler
        int bufferedMs = Math.Max(PresentationBufferMilliseconds, audioLatencyBudgetMs)
                         + Math.Max(0, getSyncOffsetMs?.Invoke() ?? 0);
        scheduler = new PresentationScheduler<byte[]>(
            fps,
            Math.Clamp(fps * bufferedMs / 1000, 2, 45),
            getAudioPtsMs,
            getSyncOffsetMs ?? (static () => 0),
            WriteFrameToTexture,
            discard: null,
            RecordError,
            "ScreenShare-H264Display");

        // Create a bounded channel for feeding video chunks to the decoder process
        feedChannel = Channel.CreateBounded<(long, byte[])>(new BoundedChannelOptions(FeedChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // Prepare the ffmpeg command-line arguments for decoding
        string decodeArgs = $"-hide_banner -loglevel error -hwaccel d3d11va -f {VideoEncoderSelector.RawFormatName(codec)} -i - -f rawvideo -pix_fmt bgra -";
        decoderProcess = new FfmpegProcessRunner(ffmpegExePath, decodeArgs);

        // Start the feed writer and reader threads
        running = true;
        feedWriterThread = new Thread(FeedWriterLoop) { IsBackground = true, Name = "ScreenShare-H264DecFeed" };
        readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "ScreenShare-H264DecRead" };
        feedWriterThread.Start();
        readerThread.Start();
    }

    /// <summary>
    /// Starts the display of decoded video frames.
    /// </summary>
    public void StartDisplay() => scheduler.StartDisplay();

    /// <summary>
    /// Feeds a video chunk to the decoder process with the specified presentation timestamp in milliseconds.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp in milliseconds.</param>
    /// <param name="chunk">The video chunk to feed.</param>
    public void Feed(long ptsMs, byte[] chunk)
    {
        if (feedChannel.Reader.Count >= FeedChannelCapacity)
            Interlocked.Increment(ref droppedChunks);

        feedChannel.Writer.TryWrite((ptsMs, chunk));
    }

    /// <summary>
    /// Feeds video chunks from the feed channel to the decoder process's standard input in a loop.
    /// </summary>
    private void FeedWriterLoop()
    {
        try
        {
            // Feed video chunks from the feed channel to the decoder process's standard input in a loop
            while (running && feedChannel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                // Read all available entries from the feed channel and write them to the decoder process's standard input
                while (feedChannel.Reader.TryRead(out (long PtsMs, byte[] Chunk) entry))
                {
                    Interlocked.Exchange(ref lastFedPtsMs, entry.PtsMs);
                    decoderProcess.StandardInput.Write(entry.Chunk, 0, entry.Chunk.Length);
                }

                decoderProcess.StandardInput.Flush();
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                RecordError("FeedWriterLoop (network -> decoder stdin)", ex);
        }
        finally
        {
            TryClose(decoderProcess.StandardInput);
        }
    }

    /// <summary>
    /// Reads decoded video frames from the decoder process's standard output and enqueues them for presentation in a loop.
    /// </summary>
    private void ReadLoop()
    {
        // Calculate the size of a single video frame in bytes (Width * Height * 4 for BGRA format)
        int frameSize = Width * Height * 4;
        byte[] frame = new byte[frameSize];

        // Read decoded video frames from the decoder process's standard output and enqueue them for presentation in a loop
        try
        {
            // Read decoded video frames from the decoder process's standard output and enqueue them for presentation in a loop
            while (running)
            {
                int offset = 0;

                // Read a single video frame from the decoder process's standard output
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

                // Enqueue the decoded video frame for presentation and increment the frames decoded counter
                Interlocked.Increment(ref framesDecoded);
                scheduler.Enqueue(Interlocked.Read(ref lastFedPtsMs), (byte[])frame.Clone());
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                RecordError("ReadLoop (decoder stdout -> presentation buffer)", ex);
        }
    }

    /// <summary>
    /// Write the decoded video frame to the shared Direct3D texture for presentation.
    /// </summary>
    /// <param name="frame">The decoded video frame to write to the texture.</param>
    private void WriteFrameToTexture(byte[] frame)
    {
        GCHandle pinned = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            // Write the decoded video frame to the shared Direct3D texture for presentation
            lock (gameAdapterDevice.ImmediateContextLock)
            {
                var dataBox = new DataBox(pinned.AddrOfPinnedObject(), Width * 4, 0);
                gameAdapterDevice.Device.ImmediateContext.UpdateSubresource(dataBox, decodedTexture);

                gameAdapterDevice.Device.ImmediateContext.Flush();
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// Records an error that occurred during a specific stage of the decoding process and logs it. 
    /// If this is the first error recorded, it sets the LastError property and stops the decoder.
    /// </summary>
    /// <param name="stage">The stage of the decoding process where the error occurred.</param>
    /// <param name="ex">The exception that occurred.</param>
    private void RecordError(string stage, Exception ex)
    {
        ServiceCollector.PluginLog.Error(ex, $"ScreenShare H264Decoder error in {stage}");
        if (Interlocked.CompareExchange(ref errorRecorded, 1, 0) == 0)
            LastError = ex;
        running = false;
    }

    /// <summary>
    /// Tries to close the specified stream, ignoring any exceptions that may occur during the close operation.
    /// </summary>
    /// <param name="stream">The stream to close.</param>
    private static void TryClose(Stream stream)
    {
        try
        {
            stream.Close();
        }
        catch
        {
            // Ignore exceptions when closing the stream, as we don't want to throw during cleanup.
        }
    }

    /// <summary>
    /// Disposes of the H264Decoder, stopping the decoder process, cleaning up resources, and ensuring that the display thread exits properly.
    /// </summary>
    public void Dispose()
    {
        // Stop the decoder and clean up resources
        disposed = true;
        running = false;
        feedChannel.Writer.TryComplete();
        scheduler.Stop();
        feedWriterThread.Join(1000);
        readerThread.Join(1000);
        bool displayExited = scheduler.Join(1000);
        scheduler.Dispose();
        decoderProcess.Dispose();

        // If the display thread didn't exit within 1 second, log a warning and leak the shared texture to avoid a use-after-dispose crash.
        if (!displayExited)
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare H264Decoder's display thread didn't exit within 1s (likely a stalled/hung GPU)");
            return;
        }

        decodedTexture.Dispose();
    }
}
