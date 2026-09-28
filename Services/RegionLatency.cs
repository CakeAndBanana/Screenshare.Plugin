using System.Diagnostics;
using System.Net.Http;
using ScreenShare.Plugin.Networking.Models;

namespace ScreenShare.Plugin.Services;

/// <summary>Finds the relay region nearest to this player by timing a request to one server in each,
/// so Automatic placement follows where the player really is rather than their character's data center.</summary>
internal sealed class RegionLatency : IDisposable
{
    // The first request also pays for DNS, TCP and TLS; the fastest of a few is the round trip itself.
    private const int SamplesPerRegion = 3;

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>The region whose server answered fastest, or null when none answered.</summary>
    public async Task<string?> FindNearestAsync(IReadOnlyList<RegionProbe> probes)
    {
        (string Region, TimeSpan? RoundTrip)[] results = await Task.WhenAll(
            probes.Select(async p => (p.Region, await MeasureAsync(p.Url).ConfigureAwait(false)))).ConfigureAwait(false);

        string? nearest = null;
        TimeSpan best = TimeSpan.MaxValue;
        foreach ((string region, TimeSpan? roundTrip) in results)
        {
            if (roundTrip is { } rtt && rtt < best)
            {
                best = rtt;
                nearest = region;
            }
        }

        return nearest;
    }

    private async Task<TimeSpan?> MeasureAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;

        var root = new Uri(uri, "/");
        TimeSpan? fastest = null;
        for (int i = 0; i < SamplesPerRegion; i++)
        {
            try
            {
                long start = Stopwatch.GetTimestamp();
                using HttpResponseMessage response = await http.GetAsync(root, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                if (fastest is null || elapsed < fastest)
                    fastest = elapsed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Unreachable this time; the other samples may still get through.
            }
        }

        return fastest;
    }

    public void Dispose() => http.Dispose();
}
