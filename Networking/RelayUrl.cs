namespace ScreenShare.Plugin.Networking;

internal static class RelayUrl
{
    public const string HubPath = "/hubs/room";

    public static bool TryNormalize(string? input, out string url)
    {
        url = string.Empty;
        string trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = (IsLocal(trimmed) ? "http://" : "https://") + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var builder = new UriBuilder(uri);
        if (builder.Path is "" or "/")
            builder.Path = HubPath;

        url = builder.Uri.ToString();
        return true;
    }

    private static bool IsLocal(string hostAndRest)
        => hostAndRest.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
           || hostAndRest.StartsWith("127.", StringComparison.Ordinal);
}
