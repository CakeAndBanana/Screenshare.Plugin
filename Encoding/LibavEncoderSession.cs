using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ScreenShare.Plugin.Encoding.Enums;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Encoding;

public sealed unsafe class LibavEncoderSession : IDisposable
{
    private const int SurfacePoolSize = 8;

    private const int HwConfigMethodHwFramesContext = 0x02;

    private static readonly Lock LoadLock = new();
    private static string? loadedFrom;

    private readonly int fps;
    private AVCodecContext* context;
    private AVFrame* frame;
    private AVPacket* packet;
    private AVBufferRef* surfaceFrames;
    private Texture2D? surfaceTexture;

    private int pendingBitrateKbps;
    private long lastPtsMs = long.MinValue;

    public string EncoderName { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameSize => Width * Height * 3 / 2;

    public bool SupportsLiveBitrate { get; }

    public bool UsesD3D11Surfaces => surfaceFrames is not null;

    public static void EnsureLoaded(string libraryDirectory)
    {
        lock (LoadLock)
        {
            if (loadedFrom is not null)
                return;

            ffmpeg.RootPath = libraryDirectory;
            uint version = ffmpeg.avcodec_version();
            if (version >> 16 != ffmpeg.LIBAVCODEC_VERSION_MAJOR)
                throw new NotSupportedException(
                    $"avcodec in '{libraryDirectory}' is major version {version >> 16}, the bindings expect {ffmpeg.LIBAVCODEC_VERSION_MAJOR}.");

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            loadedFrom = libraryDirectory;
        }
    }

    public LibavEncoderSession(
        string encoderName,
        IReadOnlyList<(string Name, string Value)> options,
        VideoCodec codec,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        Device? d3d11Device = null)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"NV12 requires even dimensions, got {width}x{height}.");

        EncoderName = encoderName;
        Width = width;
        Height = height;
        this.fps = fps;
        SupportsLiveBitrate = VideoEncoderSelector.SupportsLiveBitrate(encoderName);

        try
        {
            AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(encoderName);
            if (encoder is null)
                throw new NotSupportedException($"This ffmpeg build has no '{encoderName}' encoder.");

            AVCodecID expectedId = codec == VideoCodec.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
            if (encoder->id != expectedId)
                throw new ArgumentException($"'{encoderName}' does not encode {codec}.");

            context = ffmpeg.avcodec_alloc_context3(encoder);
            if (context is null)
                throw new OutOfMemoryException("avcodec_alloc_context3 failed.");

            context->width = width;
            context->height = height;
            context->time_base = new AVRational { num = 1, den = 1000 };
            context->framerate = new AVRational { num = fps, den = 1 };
            if (d3d11Device is not null)
            {
                surfaceFrames = CreateSurfacePool(encoder, d3d11Device, width, height);
                context->hw_frames_ctx = ffmpeg.av_buffer_ref(surfaceFrames);
                context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                context->sw_pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            }
            else
            {
                context->pix_fmt = PickPixelFormat(context, encoder);
            }

            context->gop_size = fps * 2;
            context->max_b_frames = 0;
            ApplyBitrate(bitrateKbps);

            foreach ((string name, string value) in options)
                Check(ffmpeg.av_opt_set(context->priv_data, name, value, 0), $"setting {encoderName} option {name}={value}");

            Check(ffmpeg.avcodec_open2(context, encoder, null), $"opening {encoderName}");

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame is null || packet is null)
                throw new OutOfMemoryException("av_frame_alloc/av_packet_alloc failed.");

            if (surfaceFrames is null)
            {
                frame->format = (int)context->pix_fmt;
                frame->width = width;
                frame->height = height;
                Check(ffmpeg.av_frame_get_buffer(frame, 0), "allocating the input frame");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static AVBufferRef* CreateSurfacePool(AVCodec* encoder, Device d3d11Device, int width, int height)
    {
        if (!AcceptsD3D11Frames(encoder))
            throw new NotSupportedException($"{Marshal.PtrToStringAnsi((nint)encoder->name)} does not take D3D11 frames.");

        AVBufferRef* device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (device is null)
            throw new OutOfMemoryException("av_hwdevice_ctx_alloc failed.");

        try
        {
            var deviceContext = (AVHWDeviceContext*)device->data;
            var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
            Marshal.AddRef(d3d11Device.NativePointer);
            d3d11Context->device = (ID3D11Device*)d3d11Device.NativePointer;
            Check(ffmpeg.av_hwdevice_ctx_init(device), "wrapping the D3D11 device");

            AVBufferRef* frames = ffmpeg.av_hwframe_ctx_alloc(device);
            if (frames is null)
                throw new OutOfMemoryException("av_hwframe_ctx_alloc failed.");

            var framesContext = (AVHWFramesContext*)frames->data;
            framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesContext->width = width;
            framesContext->height = height;
            framesContext->initial_pool_size = SurfacePoolSize;
            ((AVD3D11VAFramesContext*)framesContext->hwctx)->BindFlags = (uint)BindFlags.Decoder;

            int result = ffmpeg.av_hwframe_ctx_init(frames);
            if (result < 0)
            {
                ffmpeg.av_buffer_unref(&frames);
                Check(result, "allocating the D3D11 surface pool");
            }

            return frames;
        }
        finally
        {
            ffmpeg.av_buffer_unref(&device);
        }
    }

    private static bool AcceptsD3D11Frames(AVCodec* encoder)
    {
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(encoder, i);
            if (config is null)
                return false;

            if (config->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11 &&
                (config->methods & HwConfigMethodHwFramesContext) != 0)
                return true;
        }
    }

    private static AVPixelFormat PickPixelFormat(AVCodecContext* context, AVCodec* encoder)
    {
        void* configs = null;
        int count = 0;
        if (ffmpeg.avcodec_get_supported_config(context, encoder, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) < 0 ||
            configs is null)
            return AVPixelFormat.AV_PIX_FMT_NV12;

        var formats = (AVPixelFormat*)configs;
        for (int i = 0; i < count; i++)
        {
            if (formats[i] == AVPixelFormat.AV_PIX_FMT_NV12)
                return AVPixelFormat.AV_PIX_FMT_NV12;
        }

        for (int i = 0; i < count; i++)
        {
            if (formats[i] == AVPixelFormat.AV_PIX_FMT_YUV420P)
                return AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        throw new NotSupportedException("The encoder takes neither NV12 nor YUV420P input.");
    }

    private void ApplyBitrate(int bitrateKbps)
    {
        int maxRateKbps = EncoderName == "libx264" ? bitrateKbps : bitrateKbps * 4 / 3;

        context->bit_rate = bitrateKbps * 1000L;
        context->rc_max_rate = maxRateKbps * 1000L;
        context->rc_buffer_size = bitrateKbps * 2 * 1000;
    }

    public void SetBitrate(int bitrateKbps) => Interlocked.Exchange(ref pendingBitrateKbps, bitrateKbps);

    public void SendNv12(byte[] nv12, long ptsMs, Action<long, byte[]> onAccessUnit)
    {
        if (UsesD3D11Surfaces)
            throw new InvalidOperationException("This session takes D3D11 surfaces, not NV12 bytes.");

        if (nv12.Length < FrameSize)
            throw new ArgumentException($"Expected {FrameSize} bytes of NV12, got {nv12.Length}.", nameof(nv12));

        Check(ffmpeg.av_frame_make_writable(frame), "making the input frame writable");
        CopyIntoFrame(nv12);

        SendFrame(ptsMs, onAccessUnit);
    }

    public bool TryCopyToSurface(DeviceContext deviceContext, Texture2D nv12Source)
    {
        if (!UsesD3D11Surfaces)
            throw new InvalidOperationException("This session takes NV12 bytes, not D3D11 surfaces.");

        ffmpeg.av_frame_unref(frame);
        if (ffmpeg.av_hwframe_get_buffer(surfaceFrames, frame, 0) < 0)
            return false;

        var texture = (nint)frame->data[0];
        int slice = (int)(nint)frame->data[1];

        if (surfaceTexture?.NativePointer != texture)
        {
            surfaceTexture?.Dispose();
            Marshal.AddRef(texture);
            surfaceTexture = new Texture2D(texture);
        }

        deviceContext.CopySubresourceRegion(nv12Source, 0, null, surfaceTexture, slice);
        return true;
    }

    public void SendSurface(long ptsMs, Action<long, byte[]> onAccessUnit)
    {
        try
        {
            SendFrame(ptsMs, onAccessUnit);
        }
        finally
        {
            ffmpeg.av_frame_unref(frame);
        }
    }

    private void SendFrame(long ptsMs, Action<long, byte[]> onAccessUnit)
    {
        int bitrateKbps = Interlocked.Exchange(ref pendingBitrateKbps, 0);
        if (bitrateKbps > 0 && SupportsLiveBitrate)
            ApplyBitrate(bitrateKbps);

        if (ptsMs <= lastPtsMs)
            ptsMs = lastPtsMs + 1;
        lastPtsMs = ptsMs;

        frame->pts = ptsMs;
        frame->duration = 1000 / Math.Max(1, fps);

        Check(ffmpeg.avcodec_send_frame(context, frame), $"sending a frame to {EncoderName}");
        ReceivePackets(onAccessUnit);
    }

    private void CopyIntoFrame(byte[] nv12)
    {
        int lumaSize = Width * Height;

        fixed (byte* source = nv12)
        {
            CopyRows(source, Width, frame->data[0], frame->linesize[0], Width, Height);

            byte* chroma = source + lumaSize;
            int chromaRows = Height / 2;

            if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_NV12)
            {
                CopyRows(chroma, Width, frame->data[1], frame->linesize[1], Width, chromaRows);
                return;
            }

            int chromaWidth = Width / 2;
            for (int y = 0; y < chromaRows; y++)
            {
                byte* row = chroma + y * Width;
                byte* u = frame->data[1] + y * frame->linesize[1];
                byte* v = frame->data[2] + y * frame->linesize[2];
                for (int x = 0; x < chromaWidth; x++)
                {
                    u[x] = row[2 * x];
                    v[x] = row[2 * x + 1];
                }
            }
        }
    }

    private static void CopyRows(byte* source, int sourceStride, byte* destination, int destinationStride, int rowBytes, int rows)
    {
        for (int y = 0; y < rows; y++)
            System.Buffer.MemoryCopy(source + y * sourceStride, destination + y * destinationStride, rowBytes, rowBytes);
    }

    private void ReceivePackets(Action<long, byte[]> onAccessUnit)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(context, packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                return;

            Check(result, $"receiving a packet from {EncoderName}");

            try
            {
                byte[] accessUnit = new byte[packet->size];
                Marshal.Copy((nint)packet->data, accessUnit, 0, packet->size);

                long ptsMs = packet->pts == ffmpeg.AV_NOPTS_VALUE ? lastPtsMs : packet->pts;
                onAccessUnit(ptsMs, accessUnit);
            }
            finally
            {
                ffmpeg.av_packet_unref(packet);
            }
        }
    }

    public void Flush(Action<long, byte[]> onAccessUnit)
    {
        Check(ffmpeg.avcodec_send_frame(context, null), $"flushing {EncoderName}");
        ReceivePackets(onAccessUnit);
    }

    private static void Check(int result, string what)
    {
        if (result >= 0)
            return;

        byte* message = stackalloc byte[256];
        ffmpeg.av_strerror(result, message, 256);
        throw new InvalidOperationException($"libavcodec failed {what}: {Marshal.PtrToStringAnsi((nint)message)} ({result})");
    }

    public void Dispose()
    {
        if (packet is not null)
        {
            AVPacket* p = packet;
            ffmpeg.av_packet_free(&p);
            packet = null;
        }

        if (frame is not null)
        {
            AVFrame* f = frame;
            ffmpeg.av_frame_free(&f);
            frame = null;
        }

        if (context is not null)
        {
            AVCodecContext* c = context;
            ffmpeg.avcodec_free_context(&c);
            context = null;
        }

        surfaceTexture?.Dispose();
        surfaceTexture = null;

        if (surfaceFrames is not null)
        {
            AVBufferRef* f = surfaceFrames;
            ffmpeg.av_buffer_unref(&f);
            surfaceFrames = null;
        }
    }
}
