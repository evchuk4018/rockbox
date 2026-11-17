using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RockboxManager.Core;

public sealed record MusicSuggestion(string Genre, string Title, string Artist);

public sealed class DiscoveryService(HttpClient http)
{
    private sealed record Candidate(string Id, string Title, string Artist, string[] Artists, int Votes);
    // Alternate versions are poor first listens and rarely match a clean YouTube upload.
    private static readonly Regex Version = new(@"\b(live|remix|mix|instrumental|demo|karaoke|acapella|a cappella|edit|version|sped up|slowed|reverb)\b",
        RegexOptions.IgnoreCase);

    /// <summary>Finds songs outside the library: favourite artists first, alternating with catalog genre tags.</summary>
    public async Task<List<MusicSuggestion>> Songs(string genre, string[] terms, string[] artists, int count,
        IReadOnlyList<Track> library, ISet<string> exclude, CancellationToken cancellation = default)
    {
        var owned = library.SelectMany(t => new[] { LibraryViews.Identity(t.Artist, t.Title),
            LibraryViews.Identity(t.GroupArtist, t.Title) }).ToHashSet();
        var queries = Enumerable.Range(0, Math.Max(artists.Length, terms.Length)).SelectMany(i =>
            (i < artists.Length ? new[] { ("artist:" + Quote(artists[i]), artists[i]) } : []).Concat(
             i < terms.Length ? new[] { ("tag:" + Quote(terms[i]), "") } : []));
        var picked = new List<MusicSuggestion>();
        var pickedArtists = new HashSet<string>();
        foreach (var (query, artist) in queries)
        {
            if (picked.Count >= count) break;
            List<Candidate> candidates;
            try { candidates = await Search(query + " AND NOT video:true", cancellation); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException ||
                ex is OperationCanceledException && !cancellation.IsCancellationRequested) { continue; }
            var candidate = candidates.Where(c => !Version.IsMatch(c.Title) &&
                    c.Artists.All(a => !a.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)) &&
                    (artist.Length == 0 || c.Artists.Any(a => LibraryViews.Normalize(a) == LibraryViews.Normalize(artist))) &&
                    !owned.Contains(LibraryViews.Identity(c.Artist, c.Title)) && !c.Artists.Any(a => owned.Contains(LibraryViews.Identity(a, c.Title))) &&
                    !exclude.Contains(LibraryViews.Identity(c.Artist, c.Title)) && !c.Artists.Any(a => pickedArtists.Contains(LibraryViews.Normalize(a))))
                .OrderByDescending(c => c.Votes).ThenBy(c => c.Title, StringComparer.Ordinal).FirstOrDefault();
            if (candidate is null) continue;
            picked.Add(new(genre, candidate.Title, candidate.Artist));
            exclude.Add(LibraryViews.Identity(candidate.Artist, candidate.Title));
            foreach (var a in candidate.Artists) pickedArtists.Add(LibraryViews.Normalize(a));
        }
        return picked;
    }

    private static string Quote(string value) => "\"" + Regex.Replace(value, @"([+\-!(){}\[\]^""~*?:\\/|&])", @"\$1") + "\"";
    private static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private async Task<List<Candidate>> Search(string query, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            await MusicBrainzRequests.Wait(cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://musicbrainz.org/ws/2/recording/?fmt=json&limit=50&query=" + Uri.EscapeDataString(query));
            request.Headers.UserAgent.ParseAdd("RockboxManager/1.2 (+https://github.com/evchuk4018/rockbox)");
            using var response = await http.SendAsync(request, cancellation);
            if (attempt == 0 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                await Task.Delay(wait > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait, cancellation);
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var candidates = new List<Candidate>();
            if (data.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Unexpected catalog response.");
            if (!data.RootElement.TryGetProperty("recordings", out var items) || items.ValueKind != JsonValueKind.Array) return candidates;
            foreach (var item in items.EnumerateArray())
            {
                if (!Guid.TryParse(Text(item, "id"), out var id) || string.IsNullOrWhiteSpace(Text(item, "title")) ||
                    !item.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) continue;
                var artists = credits.EnumerateArray().Select(c => Text(c, "name")).Where(a => a.Length > 0).ToArray();
                if (artists.Length == 0) continue;
                var artist = string.Concat(credits.EnumerateArray().Select(c => Text(c, "name") + Text(c, "joinphrase")));
                int votes = item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Sum(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("count", out var count) &&
                        count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) ? Math.Max(n, 0) : 0) : 0;
                candidates.Add(new(id.ToString(), Text(item, "title"), artist, artists, votes));
            }
            return candidates;
        }
    }
}
