using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Tests.Networking;

public class ClientIdentityTests
{
    [Fact]
    public void ToPublicId_MatchesTheRelaysPinnedVector()
    {
        Assert.Equal("a6380450-ff77-6d23-b2f1-c718c13eab5f", ClientIdentity.ToPublicId("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void ToPublicId_NeverEchoesTheClientId()
    {
        const string clientId = "0123456789abcdef0123456789abcdef";

        Assert.DoesNotContain(clientId, ClientIdentity.ToPublicId(clientId).Replace("-", string.Empty));
    }
}
