using NAudio.Wave;
using ScreenShare.Plugin.Encoding;

namespace ScreenShare.Plugin.Tests.Encoding;

public class SyncedPlaybackBufferTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

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

        Drain(buffer, 200);

        Assert.Equal(1060, buffer.GetPtsAtPlayedBytes(60 * BytesPerMs));
    }

    [Fact]
    public void GetPtsAtPlayedBytes_InsideAnUnderrun_HoldsInsteadOfAdvancing()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(1));

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
        Drain(buffer, 60);

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
        buffer.Add(2000, Packet(2));
        Drain(buffer, 40);

        Assert.Equal(1010, buffer.GetPtsAtPlayedBytes(10 * BytesPerMs));
        Assert.Equal(2010, buffer.GetPtsAtPlayedBytes(30 * BytesPerMs));
    }

    // Add trims at the target plus burst headroom, not at the target itself.
    private const int SmallTargetMs = 40;
    private const int SmallCapMs = SmallTargetMs + SyncedPlaybackBuffer.BurstHeadroomMs;

    private static SyncedPlaybackBuffer FillOnePacketPastTheCap()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: SmallTargetMs);
        for (int i = 0; i <= SmallCapMs / 20; i++)
            buffer.Add(1000 + (i * 20), Packet((byte)(i + 1)));
        return buffer;
    }

    [Fact]
    public void Add_WithinTheBurstHeadroom_KeepsEverything()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: SmallTargetMs);
        for (int i = 0; i < SmallCapMs / 20; i++)
            buffer.Add(1000 + (i * 20), Packet(1));

        Assert.Equal(SmallCapMs, buffer.BufferedMilliseconds);
        Assert.Equal(0, buffer.DroppedMilliseconds);
    }

    [Fact]
    public void Add_PastTheCap_DropsTheOldestAudioNotTheNewest()
    {
        SyncedPlaybackBuffer buffer = FillOnePacketPastTheCap();

        Assert.Equal(SmallCapMs, buffer.BufferedMilliseconds);
        Assert.Equal(20, buffer.DroppedMilliseconds);

        byte[] played = Drain(buffer, SmallCapMs);
        Assert.Equal(2, played[0]);
        Assert.Equal((SmallCapMs / 20) + 1, played[^1]);
    }

    [Fact]
    public void Add_PastTheCap_KeepsTheClockOnTheAudioThatSurvived()
    {
        SyncedPlaybackBuffer buffer = FillOnePacketPastTheCap();
        Drain(buffer, SmallCapMs);

        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(0));
    }

    [Fact]
    public void Add_DroppingPartOfAPacket_AdvancesThatPacketsPtsToMatch()
    {
        const int targetMs = 30;
        const int capMs = targetMs + SyncedPlaybackBuffer.BurstHeadroomMs;
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: targetMs);
        for (int i = 0; i < (capMs + 10) / 20; i++)
            buffer.Add(1000 + (i * 20), Packet((byte)(i + 1)));
        Drain(buffer, capMs);

        Assert.Equal(1010, buffer.GetPtsAtPlayedBytes(0));
        Assert.Equal(1020, buffer.GetPtsAtPlayedBytes(10 * BytesPerMs));
    }

    [Fact]
    public void Add_WhilePlaybackIsHeldBack_LeavesPlaybackAtTheLiveEdge()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: SmallTargetMs);

        for (int i = 0; i < 150; i++)
            buffer.Add(1000 + (i * 20), Packet((byte)(i % 250)));

        Assert.Equal(SmallCapMs, buffer.BufferedMilliseconds);

        Drain(buffer, 40);

        const int liveEdge = 1000 + (150 * 20);
        Assert.Equal(liveEdge - SmallCapMs, buffer.GetPtsAtPlayedBytes(0));
        Assert.Equal(liveEdge - SmallCapMs + 20, buffer.GetPtsAtPlayedBytes(20 * BytesPerMs));
    }

    [Fact]
    public void SustainedCleanPlayback_NeverNarrowsBelowTheConfiguredBudget()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);

        for (int i = 0; i < 3_000; i++)
        {
            buffer.Add(1000 + (i * 20), TonePacket());
            Drain(buffer, 20);
        }

        Assert.Equal(200, buffer.TargetMilliseconds);
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
        Assert.Equal(0, scratch[^1]);
    }

    [Fact]
    public void Read_HonoursTheOffsetItIsGiven()
    {
        SyncedPlaybackBuffer buffer = NewBuffer();
        buffer.Add(1000, Packet(7));

        byte[] scratch = new byte[(20 * BytesPerMs) + 16];
        Array.Fill(scratch, (byte)0xEE);
        buffer.Read(scratch, 16, 20 * BytesPerMs);

        Assert.Equal(0xEE, scratch[15]);
        Assert.Equal(7, scratch[16]);
        Assert.Equal(7, scratch[^1]);
    }

    [Fact]
    public void GetPtsAtPlayedBytes_OverASustainedSteadyStream_StaysExact()
    {
        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 200);
        long playedBytes = 0;

        for (int i = 0; i < 3_000; i++)
        {
            buffer.Add(1000 + (i * 20), Packet((byte)(i % 250)));
            Drain(buffer, 20);
            playedBytes += 20 * BytesPerMs;

            Assert.Equal(1000 + (i * 20) + 20, buffer.GetPtsAtPlayedBytes(playedBytes));
        }

        Assert.Equal(0, buffer.UnderrunMilliseconds);
        Assert.Equal(0, buffer.DroppedMilliseconds);
    }

    private const long WaveOutPositionPeriod = 1L << 32;

    [Fact]
    public void GetPtsAtPlayedBytes_AfterTheDevicePositionCounterWraps_KeepsFollowingPlayback()
    {
        const int ChunkMs = 2500;
        const int ChunkBytes = ChunkMs * BytesPerMs;

        SyncedPlaybackBuffer buffer = NewBuffer(maxBufferedMs: 10_000);

        byte[] pcm = new byte[ChunkBytes];
        byte[] scratch = new byte[ChunkBytes];

        long handedBytes = 0;
        long ptsMs = 1000;
        while (handedBytes <= WaveOutPositionPeriod)
        {
            buffer.Add(ptsMs, pcm);
            buffer.Read(scratch, 0, scratch.Length);
            handedBytes += ChunkBytes;
            ptsMs += ChunkMs;
        }

        long truePlayedBytes = handedBytes - (100 * BytesPerMs);
        long reportedPlayedBytes = truePlayedBytes % WaveOutPositionPeriod;
        Assert.True(reportedPlayedBytes < truePlayedBytes, "the device position has to have wrapped or this proves nothing");

        Assert.Equal(1000 + (truePlayedBytes / BytesPerMs), buffer.GetPtsAtPlayedBytes(reportedPlayedBytes));

        long jumpedPtsMs = ptsMs + 60_000;
        buffer.Add(jumpedPtsMs, pcm);
        buffer.Read(scratch, 0, scratch.Length);

        long insideJumpedChunk = handedBytes + (100 * BytesPerMs);
        Assert.Equal(jumpedPtsMs + 100, buffer.GetPtsAtPlayedBytes(insideJumpedChunk % WaveOutPositionPeriod));
    }

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

        Drain(buffer, 20);

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

        for (int i = 0; i < 300; i++)
        {
            buffer.Add(1000 + (i * 20), TonePacket());
            Drain(buffer, 20);
        }

        Assert.True(buffer.TargetMilliseconds < inflated,
            $"expected narrowing from {inflated}ms, got {buffer.TargetMilliseconds}ms");
    }

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

        Drain(buffer, 200);
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
