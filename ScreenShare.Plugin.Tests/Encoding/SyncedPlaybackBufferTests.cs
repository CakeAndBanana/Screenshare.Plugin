using NAudio.Wave;
using ScreenShare.Plugin.Encoding;

namespace ScreenShare.Plugin.Tests.Encoding;

/// <summary>
/// Drives SyncedPlaybackBuffer the way an output device would - Read() pulls bytes, and the
/// "device playback position" is supplied by hand - so the master clock can be checked without a
/// sound card. Every case here is one of the ways the old BufferedWaveProvider-plus-stopwatch
/// clock silently desynced audio from video.
/// </summary>
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

    /// <summary>One 20ms packet whose every byte is <paramref name="marker"/>, so consumed audio
    /// can be identified by content as well as by timestamp.</summary>
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

    /// <summary>The whole point of the rewrite. The device is handed audio well before it plays it
    /// (measured at 176-289ms on a default WaveOutEvent), and the clock has to report what has been
    /// HEARD, not what has been handed over - reporting the latter is a fixed, invisible offset
    /// that put audio permanently ahead of where video thought it was.</summary>
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

    /// <summary>An underrun is audible silence, not elapsed content. A wall-clock estimate marched
    /// straight through it and never came back; here the clock holds so video holds with it.</summary>
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

    /// <summary>Packets dropped upstream (the host and relay both have bounded drop-oldest audio
    /// queues) leave a hole in the PTS timeline that no byte count can reveal. Carrying each
    /// packet's own timestamp through to playback is what keeps the clock right across one.</summary>
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

    /// <summary>Half a packet is dropped, so the survivor starts 10ms into its own timeline. The
    /// clock has to reflect that, or every subsequent frame is 10ms out.</summary>
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

    /// <summary>What happens while playback is held back waiting for video. Nothing drains the
    /// buffer during that wait, so a buffer that kept the OLDEST audio would have playback later
    /// begin there - the startup offset that made audio late by however long video took to start.</summary>
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

    /// <summary>A steady stream produces no discontinuities, so the segment bookkeeping must
    /// collapse rather than grow with every Read - and the clock must stay exact across thousands
    /// of them, since drift is precisely the failure being fixed.</summary>
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
}
