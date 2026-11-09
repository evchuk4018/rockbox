using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RockboxManager.Core;

public sealed record MusicSuggestion(string Kind, string Title, string Artist, string Genre, string Reason, string Url)
{
    public string Label => $"{Kind} · {Genre}";
}

public sealed record DiscoveryResult(List<MusicSuggestion> Suggestions, string Status);

public sealed class DiscoveryService(HttpClient http)
{
    private sealed record Candidate(string Id, string Title, string Artist, string[] Artists, int Votes);

    public async Task<DiscoveryResult> Find(List<GenrePreference> preferences, IReadOnlyList<Track> library,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var suggestions = new List<MusicSuggestion>();
        var ownedAlbums = library.Select(t => t.AlbumKey).ToHashSet();
        var ownedGroups = library.Select(t => t.ArtworkReleaseGroupId).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedSongs = library.SelectMany(t => new[] { LibraryViews.Identity(t.Artist, t.Title),
            LibraryViews.Identity(t.GroupArtist, t.Title) }).ToHashSet();
        var libraryArtists = library.SelectMany(t => new[] { LibraryViews.Normalize(t.Artist), LibraryViews.Normalize(t.GroupArtist) }).ToHashSet();
        var selectedArtists = new HashSet<string>();
        var selectedItems = new HashSet<string>();
        var selectedIds = new HashSet<string>();
        int failures = 0;
        foreach (var preference in preferences.Where(p => p.Points > 0).Take(2))
        foreach (var kind in new[] { "Album", "Song" })
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report($"Finding {kind.ToLowerInvariant()} suggestions for {preference.Genre}…");
            var entity = kind == "Album" ? "release-group" : "recording";
            var terms = GenreTerms(preference.Genre);
            var searches = terms.Select(term => (Query: "tag:" + Quote(term),
                Reason: $"Your {preference.Genre} listening: {preference.Summary}. Catalog tag: {term}." )).ToList();
            if (preference.FavoriteArtist.Length > 0)
                searches.Add(("artist:" + Quote(preference.FavoriteArtist),
                    $"More from {preference.FavoriteArtist}, your highest-scoring artist in {preference.Genre}."));
            for (int search = 0; search < searches.Count; search++)
            {
                var (query, reason) = searches[search];
                var artistSearch = search >= terms.Length;
                query += kind == "Album" ? " AND primarytype:album AND NOT secondarytype:compilation AND NOT secondarytype:live AND NOT secondarytype:remix" : " AND NOT video:true";
                List<Candidate> candidates;
                try { candidates = await Search(entity, query, cancellation); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException ||
                    ex is OperationCanceledException && !cancellation.IsCancellationRequested)
                { failures++; break; }
                var candidate = candidates.Where(c => c.Artists.All(a => !a.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)) &&
                        (kind != "Album" || !ownedGroups.Contains(c.Id)) && !selectedIds.Contains(c.Id) &&
                        (!artistSearch || c.Artists.Any(a => LibraryViews.Normalize(a) == LibraryViews.Normalize(preference.FavoriteArtist))) &&
                        !(kind == "Album" ? ownedAlbums : ownedSongs).Contains(LibraryViews.Identity(c.Artist, c.Title)) &&
                        !c.Artists.Any(a => (kind == "Album" ? ownedAlbums : ownedSongs).Contains(LibraryViews.Identity(a, c.Title))) &&
                        !selectedItems.Contains(LibraryViews.Identity(c.Artist, c.Title)))
                    .OrderBy(c => c.Artists.Any(a => selectedArtists.Contains(LibraryViews.Normalize(a))))
                    .ThenBy(c => c.Artists.Any(a => libraryArtists.Contains(LibraryViews.Normalize(a))))
                    .ThenByDescending(c => c.Votes).ThenBy(c => c.Title, StringComparer.Ordinal).FirstOrDefault();
                if (candidate is null) continue;
                suggestions.Add(new(kind, candidate.Title, candidate.Artist, preference.Genre, reason,
                    $"https://musicbrainz.org/{entity}/{candidate.Id}"));
                selectedItems.Add(LibraryViews.Identity(candidate.Artist, candidate.Title));
                selectedIds.Add(candidate.Id);
                foreach (var artist in candidate.Artists) selectedArtists.Add(LibraryViews.Normalize(artist));
                break;
            }
        }
        var status = suggestions.Count == 0 ? "No new matches found. Try again after more Home listening or after checking your music tags." :
            $"{suggestions.Count} suggestions outside your connected library · MusicBrainz catalog matches, based on your listening scores.";
        if (failures > 0) status += " Some catalog requests failed. Check your connection and try again.";
        return new(suggestions, status);
    }

    public static string[] GenreTerms(string genre)
    {
        // Preserve the hyphen inside the library's Hip-Hop/Rap parent genre.
        var separator = genre.IndexOf('-', genre.StartsWith("Hip-Hop/Rap-", StringComparison.OrdinalIgnoreCase) ? "Hip-Hop/Rap".Length : 0);
        if (separator < 0 || genre.Equals("hip-hop", StringComparison.OrdinalIgnoreCase)) return [genre.Trim()];
        var parent = genre[..separator].Trim().ToLowerInvariant() switch
        { "hip-hop/rap" => "hip hop", "r&b/soul" => "r&b", var value => value };
        var subgenre = genre[(separator + 1)..].Trim().ToLowerInvariant();
        // Local labels such as Pop-Dance and Rock-Alternative need their parent
        // to become useful catalog genres, rather than the broad "dance" or "alternative".
        var specific = (parent, subgenre) switch
        {
            ("pop", "dance") => "dance-pop",
            ("pop", "rock") or ("rock", "pop") => "pop rock",
            ("pop", "soul") => "pop soul",
            ("country", "pop") => "country pop",
            ("hip hop", "emo") => "emo rap",
            (_, "indie" or "alternative" or "contemporary" or "conscious" or "acoustic") => subgenre + " " + parent,
            _ => subgenre
        };
        return (parent == "other" ? Array.Empty<string>() : new[] { specific, parent })
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Quote(string value) => "\"" + Regex.Replace(value, @"([+\-!(){}\[\]^""~*?:\\/|&])", @"\$1") + "\"";
    private static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private async Task<List<Candidate>> Search(string entity, string query, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            await MusicBrainzRequests.Wait(cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://musicbrainz.org/ws/2/{entity}/?fmt=json&limit=50&query=" + Uri.EscapeDataString(query));
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
            if (!data.RootElement.TryGetProperty(entity == "recording" ? "recordings" : "release-groups", out var items) || items.ValueKind != JsonValueKind.Array) return candidates;
            foreach (var item in items.EnumerateArray())
            {
                if (!Guid.TryParse(Text(item, "id"), out var id) || string.IsNullOrWhiteSpace(Text(item, "title")) ||
                    !item.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) continue;
                if (entity == "release-group" && (Text(item, "primary-type") != "Album" ||
                    item.TryGetProperty("secondary-types", out var secondary) && secondary.ValueKind == JsonValueKind.Array && secondary.GetArrayLength() > 0)) continue;
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
