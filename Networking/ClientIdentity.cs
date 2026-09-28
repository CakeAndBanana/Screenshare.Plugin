using System.Security.Cryptography;

namespace ScreenShare.Plugin.Networking;

/// <summary>
/// Clients are identified by a public ID that is derived from their client ID. 
/// This class provides methods to convert a client ID to a public ID.
/// </summary>
internal static class ClientIdentity
{
    private const string Salt = "screenshare-user:";

    public static string ToPublicId(string clientId)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Salt + clientId));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }
}
