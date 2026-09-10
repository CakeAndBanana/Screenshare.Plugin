using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Interfaces;
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

        feedChannel = Channel.CreateBounded<(long, byte[])>(new BoundedChannelOptions(FeedChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

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
                    Interlocked.Exchange(ref lastFedPtsMs, entry.PtsMs);
                    decoderProcess.StandardInput.Write(entry.Chunk, 0, entry.Chunk.Length);
                }

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
