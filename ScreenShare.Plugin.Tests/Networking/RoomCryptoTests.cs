using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Networking.Enums;

namespace ScreenShare.Plugin.Tests.Networking;

public class RoomCryptoTests
{
    private static readonly string Salt = RoomCrypto.NewKeySalt();

    private static readonly byte[] Header = [0, 1, 2, 3, 4, 5, 6, 7, 8, 1];

    private static readonly byte[] Body = [0, 0, 0, 1, 0x65, 0xAA, 0xBB];

    [Fact]
    public void SealThenOpen_SameKey_RoundTrips()
    {
        RoomCrypto crypto = RoomCrypto.Create("taartje", Salt);

        byte[] message = crypto.Seal(Header, Body);

        Assert.Equal(Header, message[..Header.Length]);
        Assert.Equal(Header.Length + RoomCrypto.Overhead + Body.Length, message.Length);
        Assert.True(crypto.TryOpen(message, Header.Length, out byte[] opened));
        Assert.Equal(Body, opened);
    }

    [Fact]
    public void Seal_DoesNotLeaveTheBodyInTheClear()
    {
        byte[] message = RoomCrypto.Create("taartje", Salt).Seal(Header, Body);

        Assert.False(message.AsSpan(Header.Length).IndexOf(Body) >= 0);
    }

    [Fact]
    public void Seal_SameInputTwice_UsesAFreshNonce()
    {
        RoomCrypto crypto = RoomCrypto.Create("taartje", Salt);

        Assert.NotEqual(crypto.Seal(Header, Body), crypto.Seal(Header, Body));
    }

    [Fact]
    public void Open_WrongPassword_Fails()
    {
        byte[] message = RoomCrypto.Create("taartje", Salt).Seal(Header, Body);

        Assert.False(RoomCrypto.Create("taartje", Salt).TryOpen(message, Header.Length, out _));
    }

    [Fact]
    public void Open_WrongSalt_Fails()
    {
        byte[] message = RoomCrypto.Create("taartje", Salt).Seal(Header, Body);

        Assert.False(RoomCrypto.Create("taartje", RoomCrypto.NewKeySalt()).TryOpen(message, Header.Length, out _));
    }

    [Theory]
    [InlineData(0)]   // type
    [InlineData(4)]   // PTS
    [InlineData(9)]   // keyframe flag
    [InlineData(12)]  // nonce
    [InlineData(-1)]  // tag
    public void Open_AnyByteAltered_Fails(int index)
    {
        RoomCrypto crypto = RoomCrypto.Create("taartje", Salt);
        byte[] message = crypto.Seal(Header, Body);

        message[index < 0 ? message.Length + index : index] ^= 0x01;

        Assert.False(crypto.TryOpen(message, Header.Length, out _));
    }

    [Fact]
    public void Open_TooShort_FailsWithoutThrowing()
    {
        Assert.False(RoomCrypto.Create("taartje", Salt).TryOpen(new byte[Header.Length + RoomCrypto.Overhead - 1], Header.Length, out _));
    }

    [Fact]
    public void DeriveAuth_IsDeterministicAndNotThePassword()
    {
        string auth = RoomCrypto.DeriveAuth("taartje");

        Assert.Equal(auth, RoomCrypto.DeriveAuth("taartje"));
        Assert.NotEqual(auth, RoomCrypto.DeriveAuth("taartje2"));
        Assert.DoesNotContain("taartje", auth, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryOpenPayload_Video_RebuildsTheCleartextLayout()
    {
        RoomCrypto crypto = RoomCrypto.Create("taartje", Salt);
        long pts = 123_456;
        byte[] header = [(byte)MediaMessageType.Video, .. BitConverter.GetBytes(pts), 1];

        Assert.True(MediaSocketClient.TryOpenPayload(crypto, crypto.Seal(header, Body), out byte[] payload));

        Assert.Equal(pts, BitConverter.ToInt64(payload, 0));
        Assert.Equal(Body, payload[8..]);
    }

    [Fact]
    public void TryOpenPayload_Metadata_HasNoPts()
    {
        RoomCrypto crypto = RoomCrypto.Create("taartje", Salt);
        byte[] metadata = [0x00, 0x05, 0, 0, 0xD0, 0x02, 0, 0, 0, 60, 0];
        byte[] header = [(byte)MediaMessageType.VideoMetadata, 0];

        Assert.True(MediaSocketClient.TryOpenPayload(crypto, crypto.Seal(header, metadata), out byte[] payload));

        Assert.Equal(metadata, payload);
    }
}
