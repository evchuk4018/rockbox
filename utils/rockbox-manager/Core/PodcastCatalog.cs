using System.Globalization;
using System.Text;

namespace RockboxManager.Core;

public static class PodcastCatalogCodec
{
    private const string Header = "ROCKBOX-PODCASTS\t1";

    public static byte[] Write(PodcastCatalog catalog)
    {
        PodcastLimits.ValidateCatalogSize(catalog);
        var channels = catalog.Channels.Select(c => c.Normalize())
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        if (channels.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != channels.Count)
            throw new InvalidOperationException("Podcast channel IDs must be unique in a catalog.");
        var channelIds = channels.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var episodes = catalog.Episodes.Select(e => e with { ChannelId = PodcastId.Validate(e.ChannelId, "Channel ID") }).ToList();
        if (episodes.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != episodes.Count)
            throw new InvalidOperationException("Podcast episode IDs must be unique in a catalog.");
        foreach (var channel in channels) if (episodes.Any(e => e.ChannelId != channel.Id &&
            e.ChannelId.Equals(channel.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Podcast channel IDs must use one stable case.");
        if (episodes.Any(e => !channelIds.Contains(e.ChannelId))) throw new InvalidOperationException("Every podcast episode needs a catalog channel.");

        var lines = new List<string> { $"{Header}\t{catalog.Week.Monday:yyyy-MM-dd}\t{catalog.Week.Sunday:yyyy-MM-dd}" };
        lines.AddRange(channels.Select(c => $"C\t{c.Id}\t{Escape(c.Name)}"));
        lines.AddRange(episodes.OrderBy(e => e.PublishedAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e =>
            {
                PodcastLimits.ValidateText(e.ChannelName, "Channel name", PodcastLimits.NameMax);
                PodcastLimits.ValidateText(e.Title, "Episode title", PodcastLimits.TitleMax);
                if (e.PublicationUnixSeconds < 0) throw new InvalidOperationException("Podcast publication timestamps must be non-negative.");
                if (e.Duration < TimeSpan.Zero || e.Duration.TotalMilliseconds > uint.MaxValue)
                    throw new InvalidOperationException("Podcast episode duration exceeds the firmware limit.");
                var line = $"E\t{e.ChannelId}\t{PodcastId.Validate(e.Id, "Video ID")}\t{Escape(e.Title)}\t{e.PublicationUnixSeconds.ToString(CultureInfo.InvariantCulture)}\t{e.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)}\t{e.AudioPath}";
                ValidateLine(line);
                return line;
            }));
        foreach (var line in lines) ValidateLine(line);
        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }

    public static bool TryParse(byte[] bytes, out PodcastCatalog? catalog, out string? error)
    {
        catalog = null; error = null;
        try
        {
            if (bytes.Length == 0 || bytes[^1] != (byte)'\n') throw new InvalidOperationException("The podcast catalog must be LF-terminated.");
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Contains('\r')) throw new InvalidOperationException("The podcast catalog must use LF line endings.");
            var splitLines = text.Split('\n');
            var lines = splitLines[..^1].ToList();
            if (lines.Count == 0) throw new InvalidOperationException("The podcast catalog is empty.");
            if (lines.Any(line => line.Length == 0)) throw new InvalidOperationException("The podcast catalog contains a blank row.");
            foreach (var line in lines) ValidateLine(line);
            var header = lines[0].Split('\t');
            if (header.Length != 4 || header[0] != "ROCKBOX-PODCASTS" || header[1] != "1" ||
                !DateOnly.TryParseExact(header[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monday) ||
                !DateOnly.TryParseExact(header[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sunday))
                throw new InvalidOperationException("Unsupported podcast catalog header.");
            var week = new PodcastWeek(monday, sunday);
            var channels = new List<PodcastChannel>(); var episodes = new List<PodcastEpisode>();
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && parts[0] == "C")
                {
                    var channel = new PodcastChannel(PodcastId.Validate(parts[1], "Channel ID"), Unescape(parts[2])).Normalize();
                    if (channels.Any(c => c.Id.Equals(channel.Id, StringComparison.Ordinal))) throw new InvalidOperationException("Duplicate podcast channel.");
                    channels.Add(channel); continue;
                }
                if (parts.Length == 7 && parts[0] == "E")
                {
                    var channelId = PodcastId.Validate(parts[1], "Channel ID");
                    var id = PodcastId.Validate(parts[2], "Video ID");
                    if (!long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var publication) ||
                        !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) || publication < 0 || duration < 0 || duration > uint.MaxValue)
                        throw new InvalidOperationException("Invalid podcast episode numbers.");
                    var expected = $"/<microSD1>/Podcasts/RockboxManager/{id}.mp3";
                    if (parts[6] != expected || !IsManagedAudioPath(parts[6])) throw new InvalidOperationException("Invalid managed podcast audio path.");
                    if (episodes.Any(e => e.Id == id)) throw new InvalidOperationException("Duplicate podcast episode.");
                    var title = Unescape(parts[3]);
                    PodcastLimits.ValidateText(title, "Episode title", PodcastLimits.TitleMax);
                    episodes.Add(new PodcastEpisode(id, channelId, "", title, DateTimeOffset.FromUnixTimeSeconds(publication), TimeSpan.FromMilliseconds(duration)));
                    continue;
                }
                throw new InvalidOperationException("Malformed podcast catalog row.");
            }
            PodcastLimits.ValidateCatalogSize(new PodcastCatalog(week, channels, episodes));
            if (episodes.Any(e => !channels.Any(c => c.Id == e.ChannelId))) throw new InvalidOperationException("Podcast episode references an unknown channel.");
            var names = channels.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
            catalog = new PodcastCatalog(week, channels, episodes.Select(e => e with { ChannelName = names[e.ChannelId] }).ToList());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException or DecoderFallbackException)
        { error = ex.Message; return false; }
    }

    private static bool IsManagedAudioPath(string path)
    {
        if (!path.StartsWith("/<microSD1>/Podcasts/RockboxManager/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) return false;
        var id = path["/<microSD1>/Podcasts/RockboxManager/".Length..^4];
        try { PlaylistCodec.ValidatePath(path); }
        catch (InvalidOperationException) { return false; }
        return PodcastId.IsValid(id) && path.EndsWith(".mp3", StringComparison.Ordinal);
    }

    private static void ValidateLine(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) >= PodcastLimits.MaxCatalogLine)
            throw new InvalidOperationException("A podcast catalog line exceeds the firmware limit.");
    }

    public static string Escape(string value) => value.Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\t", "%09", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

    public static string Unescape(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || value.AsSpan(i + 1, 2).ToString().ToUpperInvariant() is not ("25" or "09" or "0D" or "0A"))
                throw new InvalidOperationException("Podcast catalog contains an invalid escape.");
            i += 2;
        }
        return value.Replace("%0A", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("%0D", "\r", StringComparison.OrdinalIgnoreCase)
            .Replace("%09", "\t", StringComparison.OrdinalIgnoreCase)
            .Replace("%25", "%", StringComparison.OrdinalIgnoreCase);
    }
}
