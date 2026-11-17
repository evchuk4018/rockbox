using System.Globalization;
using System.Text;

namespace RockboxManager.Core;

public sealed record ListeningScore(uint Balance, uint Updated, uint Completed, uint LateExits, uint EarlyExits)
{
    public double PointsAt(long now) => Balance / 65536d *
        (Updated == 0 || now <= Updated ? 1 : Math.Pow(2, -(now - Updated) / 2592000d));
    public bool HasListening => Balance > 0 || Completed > 0 || LateExits > 0 || EarlyExits > 0;
}

public sealed record ListeningProfile(Dictionary<SongPath, double> Scores)
{
    public static ListeningProfile Empty => new([]);

    public static ListeningProfile Load(PlayerDevice device, IReadOnlyList<Track> library) =>
        FromText(Read(device), library, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>The player's raw score snapshot, or empty text when it is missing or unreadable.</summary>
    public static string Read(PlayerDevice device)
    {
        try
        {
            var path = device.FilePath(Storage.Internal, ".rockbox/home-scores.tsv");
            return File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false, true)) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { return ""; }
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
        return new(library.Where(t => t.Available && history.ContainsKey(t.Path.RockboxPath)).DistinctBy(t => t.Path)
            .ToDictionary(t => t.Path, t => history[t.Path.RockboxPath].PointsAt(now)));
    }
}
