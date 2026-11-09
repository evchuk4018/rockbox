using System.Globalization;
using System.Text;

namespace RockboxManager.Core;

public sealed record ListeningScore(uint Balance, uint Updated, uint Completed, uint LateExits, uint EarlyExits)
{
    public double PointsAt(long now) => Balance / 65536d *
        (Updated == 0 || now <= Updated ? 1 : Math.Pow(2, -(now - Updated) / 2592000d));
    public bool HasListening => Balance > 0 || Completed > 0 || LateExits > 0 || EarlyExits > 0;
}

public sealed record GenrePreference(string Genre, double Points, int SongCount, string FavoriteArtist)
{
    // Shrink sparse genres toward zero; skipped-only songs lower the genre's average.
    public double Strength => Points / (SongCount + 2);
    public string Summary => $"{Points:0.#} {(Math.Round(Points, 1) == 1 ? "point" : "points")} across {SongCount:N0} {(SongCount == 1 ? "song" : "songs")}";
}

public sealed record ListeningProfile(Dictionary<SongPath, double> Scores, List<GenrePreference> Genres, string Status)
{
    public static ListeningProfile Empty(string status) => new([], [], status);

    public static ListeningProfile Load(PlayerDevice device, IReadOnlyList<Track> library)
    {
        try
        {
            var path = device.FilePath(Storage.Internal, ".rockbox/home-scores.tsv");
            return File.Exists(path) ? FromText(File.ReadAllText(path, new UTF8Encoding(false, true)), library,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : Empty("No listening scores yet. Listen through Home genre autoplay, then reconnect your player.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return Empty("Could not read listening scores: " + ex.Message); }
    }

    public static Dictionary<string, ListeningScore> Parse(string text)
    {
        var scores = new Dictionary<string, ListeningScore>(StringComparer.Ordinal);
        // Snapshots are cumulative. Never add rows or accept an interrupted final write.
        var end = text.LastIndexOf('\n');
        if (end < 0) return scores;
        foreach (var line in text[..end].TrimStart('\ufeff').Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 6) continue;
            var numbers = new uint[5];
            if (Enumerable.Range(0, 5).Any(i => !uint.TryParse(fields[i], NumberStyles.None,
                CultureInfo.InvariantCulture, out numbers[i]))) continue;
            var path = new StringBuilder();
            bool valid = true;
            for (int i = 0; i < fields[5].Length; i++)
            {
                var c = fields[5][i];
                if (c == '%')
                {
                    var escape = i + 3 <= fields[5].Length ? fields[5].Substring(i, 3) : "";
                    c = escape switch { "%25" => '%', "%09" => '\t', "%0D" => '\r', "%0A" => '\n', _ => '\0' };
                    if (c == '\0') { valid = false; break; }
                    i += 2;
                }
                if (c == '\0') { valid = false; break; }
                path.Append(c);
            }
            if (!valid || path.Length == 0 || path[0] != '/' || Encoding.UTF8.GetByteCount(path.ToString()) >= PlaylistCodec.MaxPathBytes) continue;
            scores[path.ToString()] = new(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]);
        }
        return scores;
    }

    public static ListeningProfile FromText(string text, IReadOnlyList<Track> library, long now)
    {
        var history = Parse(text);
        var matched = library.Where(t => t.Available && history.ContainsKey(t.Path.RockboxPath))
            .DistinctBy(t => t.Path).Select(t => (Track: t, Score: history[t.Path.RockboxPath])).ToList();
        var points = matched.ToDictionary(t => t.Track.Path, t => t.Score.PointsAt(now));
        var genres = matched.Where(t => t.Score.HasListening && !string.IsNullOrWhiteSpace(t.Track.Genre.Split(',')[0]))
            .GroupBy(t => LibraryViews.Normalize(t.Track.Genre.Split(',')[0]))
            .Select(g => new GenrePreference(g.First().Track.Genre.Split(',')[0].Trim(),
                g.Sum(t => points[t.Track.Path]), g.Count(),
                g.Where(t => points[t.Track.Path] > 0 && !string.IsNullOrWhiteSpace(t.Track.GroupArtist) &&
                    !t.Track.GroupArtist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(t => t.Track.GroupArtist, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(a => a.Sum(t => points[t.Track.Path])).FirstOrDefault()?.Key ?? ""))
            .Where(g => g.Points > 0).OrderByDescending(g => g.Strength).ThenBy(g => g.Genre, StringComparer.Ordinal).ToList();
        return new(points, genres, $"{matched.Count:N0} scored songs matched · {history.Count - matched.Count:N0} paths outside this library. " +
            "Scores halve every 30 days; larger scores reflect more listening.");
    }
}
