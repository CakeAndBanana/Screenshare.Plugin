using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Tests.Networking;

public class ClientIdentityTests
{
    /// <summary>Same vector as the relay's ClientIdentity test. The relay computes everyone else's
    /// ID and this copy computes your own, so if the two drift apart you'd see a different ID for
    /// yourself than everyone else sees.</summary>
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
