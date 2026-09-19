using System.Security.Cryptography;

namespace ScreenShare.Plugin.Networking;

internal static class ClientIdentity
{
    private const string Salt = "screenshare-user:";

    public static string ToPublicId(string clientId)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Salt + clientId));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }
}
