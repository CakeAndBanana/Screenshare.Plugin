namespace ScreenShare.Plugin.Networking;

/// <summary>
/// Relay URL utility class for normalizing and validating relay server URLs
/// </summary>
internal static class RelayUrl
{
    public const string HubPath = "/hubs/room";

    /// <summary>
    /// Tries to normalize the input string into a valid relay URL.
    /// If successful, returns true and outputs the normalized URL; otherwise, returns false.
    /// </summary>
    /// <param name="input">The input string to normalize.</param>
    /// <param name="url">The normalized URL, if successful.</param>
    /// <returns>true if the URL was normalized successfully; otherwise, false.</returns>
    public static bool TryNormalize(string? input, out string url)
    {
        url = string.Empty;
        string trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = (IsLocal(trimmed) ? "http://" : "https://") + trimmed;

        // Validate the URL and ensure it has a valid scheme and host
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // Ensure the path is set to the hub path if it's empty or just "/"
        var builder = new UriBuilder(uri);
        if (builder.Path is "" or "/")
            builder.Path = HubPath;

        url = builder.Uri.ToString();
        return true;
    }

    /// <summary>
    /// Is local host or loopback address (127.x.x.x)
    /// </summary>
    /// <param name="hostAndRest">The host and rest of the URL.</param>
    /// <returns>true if the host is local; otherwise, false.</returns>
    private static bool IsLocal(string hostAndRest)
        => hostAndRest.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
           || hostAndRest.StartsWith("127.", StringComparison.Ordinal);
}
