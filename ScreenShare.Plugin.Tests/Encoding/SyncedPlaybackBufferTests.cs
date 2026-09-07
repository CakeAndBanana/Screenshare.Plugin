using NAudio.Wave;
using ScreenShare.Plugin.Encoding;

namespace ScreenShare.Plugin.Tests.Encoding;

public class SyncedPlaybackBufferTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    // 48000 * 2ch * 4 bytes = 384000 B/s, so exactly 384 bytes per millisecond and 7680 per 20ms
    // Opus frame. Every figure below is chosen to stay integral, so an assertion that fails is a
    // real defect rather than rounding.
    private const int BytesPerMs = 384;
    private const int PacketBytes = 20 * BytesPerMs;

    private static SyncedPlaybackBuffer NewBuffer(int maxBufferedMs = 200) =>
        new(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels), maxBufferedMs);

    private static byte[] Packet(byte marker)
    {
        byte[] pcm = new byte[PacketBytes];
        Array.Fill(pcm, marker);
        return pcm;
    }

    private static byte[] Drain(SyncedPlaybackBuffer buffer, int milliseconds)
    {
        byte[] scratch = new byte[milliseconds * BytesPerMs];
        buffer.Read(scratch, 0, scratch.Length);
        return scratch;
    }

    [Fact]
    public void GetPtsAtPlayedBytes_BeforeAnythingIsPlayed_ReturnsNull()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));

        Assert.Null(buffer.GetPtsAtPlayedBytes(0));
    }

    [Fact]
    public void GetPtsAtPlayedBytes_AtStartOfContent_ReturnsThatContentsPts()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));
        Drain(buffer, 20);

        Assert.Equal(1000, buffer.GetPtsAtPlayedBytes(0));
    }

    [Fact]
    public void GetPtsAtPlayedBytes_PartWayThroughContent_AdvancesWithTheDevice()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));
        buffer.Add(1020, Packet(2));
        Drain(buffer, 40);

        Assert.Equal(1005, buffer.GetPtsAtPlayedBytes(5 * BytesPerMs));
        Assert.Equal(1030, buffer.GetPtsAtPlayedBytes(30 * BytesPerMs));
    }

    [Fact]
    public void GetPtsAtPlayedBytes_WithAudioStillInsideTheDevice_ReportsWhatWasHeardNotWhatWasHandedOver()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        for (int i = 0; i < 10; i++)
            buffer.Add(1000 + (i * 20), Packet((byte)i));

        // The device takes 200ms of audio up front but has only played 60ms of it.
        Drain(buffer, 200);

        Assert.Equal(1060, buffer.GetPtsAtPlayedBytes(60 * BytesPerMs));
    }

    [Fact]
    public void GetPtsAtPlayedBytes_InsideAnUnderrun_HoldsInsteadOfAdvancing()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));

        // Asks for 60ms with only 20ms available: 40ms of the result is zero-filled silence.
        Drain(buffer, 60);

        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(20 * BytesPerMs));
        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(40 * BytesPerMs));
        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(60 * BytesPerMs));
        Assert.Equal(40, buffer.UnderrunMilliseconds);
    }

    [Fact]
    public void GetPtsAtPlayedBytes_AfterAnUnderrun_ResumesAtTheNewContentsOwnPts()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));
        Drain(buffer, 60); // 20ms of content, then 40ms of silence

        // Content resumes 500ms later on the sender's timeline - a gap, not a continuation.
        buffer.Add(1500, Packet(2));
        Drain(buffer, 20);

        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(50 * BytesPerMs));
        Assert.Equal(1505, buffer.GetPtsAtPlayedBytes(65 * BytesPerMs));
    }

    [Fact]
    public void GetPtsAtPlayedBytes_WithPacketsLostUpstream_FollowsTheTimestampsNotTheByteCount()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));
        buffer.Add(2000, Packet(2)); // ~1s of packets never arrived
        Drain(buffer, 40);

        Assert.Equal(1010, buffer.GetPtsAtPlayedBytes(10 * BytesPerMs));
        Assert.Equal(2010, buffer.GetPtsAtPlayedBytes(30 * BytesPerMs));
    }

    [Fact]
    public void Add_PastTheCap_DropsTheOldestAudioNotTheNewest()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 40);
        buffer.Add(1000, Packet(1));
        buffer.Add(1020, Packet(2));
        buffer.Add(1040, Packet(3));

        Assert.Equal(40, buffer.BufferedMilliseconds);
        Assert.Equal(20, buffer.DroppedMilliseconds);

        // The survivors are the NEWEST two. BufferedWaveProvider kept the other end.
        byte[] played = Drain(buffer, 40);
        Assert.Equal(2, played[0]);
        Assert.Equal(3, played[^1]);
    }

    [Fact]
    public void Add_PastTheCap_KeepsTheClockOnTheAudioThatSurvived()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 40);
        buffer.Add(1000, Packet(1));
        buffer.Add(1020, Packet(2));
        buffer.Add(1040, Packet(3));
        Drain(buffer, 40);

        // Playback starts at the second packet, so that is what the clock must report - not the
        // PTS of audio that was thrown away.
        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(0));
    }

    [Fact]
    public void Add_DroppingPartOfAPacket_AdvancesThatPacketsPtsToMatch()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 30);
        buffer.Add(1000, Packet(1));
        buffer.Add(1020, Packet(2));
        Drain(buffer, 30);

        Assert.Equal(1010, buffer.GetPtsAtPlayedBytes(0));
        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(10 * BytesPerMs));
    }

    [Fact]
    public void Add_WhilePlaybackIsHeldBack_LeavesPlaybackAtTheLiveEdge()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 40);

        // Three seconds of audio arriving while ffmpeg is still starting up.
        for (int i = 0; i < 150; i++)
            buffer.Add(1000 + (i * 20), Packet((byte)(i % 250)));

        Assert.Equal(40, buffer.BufferedMilliseconds);

        Drain(buffer, 40);

        // 150 packets 20ms apart from 1000 end at 1000 + 149*20 = 3980, so the surviving two are
        // 3960 and 3980 - the live edge, three seconds past where playback would otherwise start.
        Assert.Equal(3960, buffer.GetPtsAtPlayedBytes(0));
        Assert.Equal(3980, buffer.GetPtsAtPlayedBytes(20 * BytesPerMs));
    }

    [Fact]
    public void Read_AlwaysFillsTheWholeRequest_SoTheDeviceIsNeverStarved()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));

        byte[] scratch = new byte[100 * BytesPerMs];
        Array.Fill(scratch, (byte)0xFF);
        int read = buffer.Read(scratch, 0, scratch.Length);

        Assert.Equal(scratch.Length, read);
        Assert.Equal(1, scratch[0]);
        Assert.Equal(0, scratch[^1]); // the shortfall is zero-filled, not left as stale bytes
    }

    [Fact]
    public void Read_HonoursTheOffsetItIsGiven()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(7));

        byte[] scratch = new byte[(20 * BytesPerMs) + 16];
        Array.Fill(scratch, (byte)0xEE);
        buffer.Read(scratch, 16, 20 * BytesPerMs);

        Assert.Equal(0xEE, scratch[15]); // untouched
        Assert.Equal(7, scratch[16]);
        Assert.Equal(7, scratch[^1]);
    }

    [Fact]
    public void GetPtsAtPlayedBytes_OverASustainedSteadyStream_StaysExact()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);
        long playedBytes = 0;

        for (int i = 0; i < 3_000; i++) // 60 seconds of 20ms packets
        {
            buffer.Add(1000 + (i * 20), Packet((byte)(i % 250)));
            Drain(buffer, 20);
            playedBytes += 20 * BytesPerMs;

            Assert.Equal(1000 + (i * 20) + 20, buffer.GetPtsAtPlayedBytes(playedBytes));
        }

        Assert.Equal(0, buffer.UnderrunMilliseconds);
        Assert.Equal(0, buffer.DroppedMilliseconds);
    }

    // --- Adaptive depth ---

    private static byte[] TonePacket(float amplitude = 0.5f)
    {
        float[] samples = new float[PacketBytes / sizeof(float)];
        Array.Fill(samples, amplitude);
        byte[] pcm = new byte[PacketBytes];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    private static float[] AsSamples(byte[] pcm)
    {
        float[] samples = new float[pcm.Length / sizeof(float)];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        return samples;
    }

    [Fact]
    public void TargetMilliseconds_StartsAtTheConfiguredBudget()
    {
        Assert.Equal(200, NewBuffer(maxBufferedMs: 200).TargetMilliseconds);
    }

    [Fact]
    public void Underrun_GrowsTheTarget()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);

        Drain(buffer, 20); // nothing buffered - underruns

        Assert.True(buffer.TargetMilliseconds > 200, $"expected growth, got {buffer.TargetMilliseconds}ms");
    }

    [Fact]
    public void RepeatedUnderruns_StopGrowingAtACeiling()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);

        for (int i = 0; i < 200; i++)
            Drain(buffer, 20);

        Assert.InRange(buffer.TargetMilliseconds, 201, 600);
    }

    [Fact]
    public void SustainedCleanPlayback_NarrowsTheTargetBackDown()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);
        Drain(buffer, 20);
        int inflated = buffer.TargetMilliseconds;

        // 6 seconds of clean 20ms reads, past the narrowing interval.
        for (int i = 0; i < 300; i++)
        {
            buffer.Add(1000 + (i * 20), TonePacket());
            Drain(buffer, 20);
        }

        Assert.True(buffer.TargetMilliseconds < inflated,
            $"expected narrowing from {inflated}ms, got {buffer.TargetMilliseconds}ms");
    }

    // --- Underrun concealment ---

    [Fact]
    public void Underrun_AfterRealAudio_ConcealsWithSignalRatherThanSilence()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);
        buffer.Add(1000, TonePacket(0.5f));
        Drain(buffer, 20);

        float[] concealed = AsSamples(Drain(buffer, 5));

        Assert.Contains(concealed, sample => MathF.Abs(sample) > 0.1f);
    }

    [Fact]
    public void Underrun_SustainedGap_DecaysToSilence()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);
        buffer.Add(1000, TonePacket(0.5f));
        Drain(buffer, 20);

        Drain(buffer, 200); // well past the fade window
        float[] late = AsSamples(Drain(buffer, 20));

        Assert.All(late, sample => Assert.True(MathF.Abs(sample) < 0.001f, $"expected silence, got {sample}"));
    }

    [Fact]
    public void Underrun_BeforeAnyAudio_IsSilent()
    {
        float[] samples = AsSamples(Drain(NewBuffer(maxBufferedMs: 200), 20));

        Assert.All(samples, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Concealment_DoesNotAdvanceTheClock()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);
        buffer.Add(1000, TonePacket());
        Drain(buffer, 20);
        long afterContent = 20 * BytesPerMs;
        long? clockAtContentEnd = buffer.GetPtsAtPlayedBytes(afterContent);

        Drain(buffer, 40);

        Assert.Equal(clockAtContentEnd, buffer.GetPtsAtPlayedBytes(afterContent + (40 * BytesPerMs)));
    }
}
