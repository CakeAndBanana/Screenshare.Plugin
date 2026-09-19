using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Tests.Networking;

public class RelayUrlTests
{
    [Theory]
    [InlineData("relay.example.com", "https://relay.example.com/hubs/room")]
    [InlineData("  relay.example.com/  ", "https://relay.example.com/hubs/room")]
    [InlineData("https://relay.example.com", "https://relay.example.com/hubs/room")]
    [InlineData("https://relay.example.com:8443", "https://relay.example.com:8443/hubs/room")]
    [InlineData("http://localhost:5178/hubs/room", "http://localhost:5178/hubs/room")]
    [InlineData("localhost:5178", "http://localhost:5178/hubs/room")]
    [InlineData("127.0.0.1:5178", "http://127.0.0.1:5178/hubs/room")]
    [InlineData("https://example.com/custom/hub", "https://example.com/custom/hub")]
    public void TryNormalize_FillsInSchemeAndHubPath(string input, string expected)
    {
        Assert.True(RelayUrl.TryNormalize(input, out string url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ftp://relay.example.com")]
    [InlineData("https://")]
    public void TryNormalize_RejectsWhatIsntARelayAddress(string? input)
    {
        Assert.False(RelayUrl.TryNormalize(input, out _));
    }
}
