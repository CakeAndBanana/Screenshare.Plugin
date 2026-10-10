using System.Security.Cryptography;

namespace ScreenShare.Plugin.Networking;

/// <summary>
/// End-to-end encryption for a password-protected room's media. Every member derives the same key from
/// the room password and the room's public key salt, so the relay - which never sees the password - only
/// ever forwards ciphertext.
/// </summary>
/// <remarks>
/// An encrypted message is <c>header || nonce (12) || ciphertext || tag (16)</c>. The header stays in the
/// clear because the relay needs it (message type, PTS, keyframe flag), and it is authenticated as AAD so
/// it can't be altered without the message failing to open. Shared by the room session and its media sockets
/// for as long as any of them is alive, so it isn't disposable; the key handle is released when collected.
/// </remarks>
public sealed class RoomCrypto
{
    public const int KeySaltBytes = 16;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int Overhead = NonceBytes + TagBytes;

    private static readonly byte[] AuthSalt = "ScreenShare/room-auth/v1"u8.ToArray();
    private const int AuthIterations = 100_000;
    private const int KeyIterations = 200_000;

    private readonly AesGcm aes;
    private readonly Lock aesLock = new();

    private RoomCrypto(byte[] key)
    {
        aes = new AesGcm(key, TagBytes);
        CryptographicOperations.ZeroMemory(key);
    }

    public static bool IsSupported => AesGcm.IsSupported;

    /// <summary>A fresh random salt for a new room, base64-encoded for the relay to store and hand out.</summary>
    public static string NewKeySalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySaltBytes));

    /// <summary>The value sent to the relay in place of the room password.</summary>
    public static string DeriveAuth(string password)
        => Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, AuthSalt, AuthIterations, HashAlgorithmName.SHA256, 32));

    /// <summary>Derives the room's media key. Slow by design (PBKDF2), so call it off the UI thread.</summary>
    /// <exception cref="FormatException">The key salt isn't valid base64.</exception>
    public static RoomCrypto Create(string password, string keySalt)
    {
        byte[] salt = Convert.FromBase64String(keySalt);
        return new RoomCrypto(Rfc2898DeriveBytes.Pbkdf2(password, salt, KeyIterations, HashAlgorithmName.SHA256, 32));
    }

    /// <summary>Encrypts <paramref name="plaintext"/> and returns <c>header || nonce || ciphertext || tag</c>.</summary>
    public byte[] Seal(ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        byte[] message = new byte[header.Length + Overhead + plaintext.Length];
        header.CopyTo(message);

        Span<byte> nonce = message.AsSpan(header.Length, NonceBytes);
        Span<byte> ciphertext = message.AsSpan(header.Length + NonceBytes, plaintext.Length);
        Span<byte> tag = message.AsSpan(header.Length + NonceBytes + plaintext.Length, TagBytes);
        RandomNumberGenerator.Fill(nonce);

        lock (aesLock)
            aes.Encrypt(nonce, plaintext, ciphertext, tag, header);

        return message;
    }

    /// <summary>
    /// Opens a message produced by <see cref="Seal"/> whose first <paramref name="headerLength"/> bytes are the
    /// cleartext header. Returns false - never throws - for a message that's too short, was tampered with, or
    /// was sealed with a different key.
    /// </summary>
    public bool TryOpen(ReadOnlySpan<byte> message, int headerLength, out byte[] plaintext)
    {
        plaintext = [];
        if (message.Length < headerLength + Overhead)
            return false;

        ReadOnlySpan<byte> header = message[..headerLength];
        ReadOnlySpan<byte> nonce = message.Slice(headerLength, NonceBytes);
        ReadOnlySpan<byte> ciphertext = message[(headerLength + NonceBytes)..^TagBytes];
        ReadOnlySpan<byte> tag = message[^TagBytes..];

        byte[] output = new byte[ciphertext.Length];
        try
        {
            lock (aesLock)
                aes.Decrypt(nonce, ciphertext, tag, output, header);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        plaintext = output;
        return true;
    }
}
