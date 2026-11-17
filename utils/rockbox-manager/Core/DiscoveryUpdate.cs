using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace RockboxManager.Core;

public sealed record DiscoveryGroup(string Genre, string[] Terms);
public sealed record DiscoveryChanges(List<SongPath> Removed, List<SongPath> Added, List<string> Errors);

/// <summary>Retires low-scoring Discovery songs and installs a fresh pop and rap batch.</summary>
public sealed class DiscoveryUpdate(DiscoveryService catalog, PortableToolPaths tools, string historyPath, IProcessRunner? runner = null)
{
    public const string Folder = "MusicLibrary/Discovery/";
    public const string Playlist = "Discovery";
    // Roughly one full listen; a played song below this was skipped more than enjoyed.
    public const double Threshold = 1;
    public const int PerGroup = 2;
    public static readonly DiscoveryGroup[] Groups = [new("Pop", ["dance-pop", "pop"]), new("Hip-Hop/Rap", ["hip hop", "trap"])];
    private readonly bool portable = runner is null;
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();

    public static bool IsDiscovery(SongPath path) =>
        path.Storage != Storage.Unknown && path.RelativePath.StartsWith(Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a library genre to a Discovery group: mainstream pop or any rap.</summary>
    public static string? Group(string genre)
    {
        var g = genre.Split(',')[0].Trim().ToLowerInvariant();
        if (g.Contains("hip-hop") || g.Contains("hip hop") || g.Contains("rap") || g.Contains("trap")) return "Hip-Hop/Rap";
        if ((g.StartsWith("pop") || g.StartsWith("dance-pop") || g.StartsWith("electropop")) &&
            !new[] { "indie", "rock", "alternative", "folk", "r&b", "soul" }.Any(g.Contains)) return "Pop";
        return null;
    }

    /// <summary>Played, unliked Discovery songs whose score fell below the threshold. Unplayed songs keep their chance.</summary>
    public static List<SongPath> Prune(IEnumerable<Track> library, IReadOnlyDictionary<string, ListeningScore> scores,
        ISet<SongPath> liked, long now) =>
        library.Where(t => IsDiscovery(t.Path) && !liked.Contains(t.Path) &&
            scores.TryGetValue(t.Path.RockboxPath, out var s) && s.HasListening && s.PointsAt(now) < Threshold)
            .Select(t => t.Path).Distinct().ToList();

    public static string[] FavoriteArtists(IEnumerable<Track> library, IReadOnlyDictionary<string, ListeningScore> scores, string group, long now) =>
        library.Where(t => Group(t.Genre) == group && !string.IsNullOrWhiteSpace(t.GroupArtist) &&
                !t.GroupArtist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) &&
                scores.TryGetValue(t.Path.RockboxPath, out var s) && s.PointsAt(now) > 0)
            .GroupBy(t => t.GroupArtist, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(t => scores[t.Path.RockboxPath].PointsAt(now))).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(PerGroup).Select(g => g.Key).ToArray();

    /// <summary>Drops removed songs from every editable playlist and appends new songs to Discovery.</summary>
    public static void ApplyPlaylists(EditSession session, IReadOnlyCollection<SongPath> removed, IEnumerable<SongPath> added)
    {
        var discovery = session.VisiblePlaylists.FirstOrDefault(p => !p.IsLiked && p.Stem.Equals(Playlist, StringComparison.OrdinalIgnoreCase))
            ?? session.Create(Playlist);
        if (discovery.HasConflict) session.ResolveConflict(discovery, useSd: false);
        var gone = removed.ToHashSet();
        foreach (var playlist in session.VisiblePlaylists.Where(p => !p.IsLiked && !p.HasConflict)) playlist.Entries.RemoveAll(gone.Contains);
        session.Add(discovery, added);
    }

    public async Task<DiscoveryChanges> RunAsync(PlayerDevice device, IReadOnlyList<Track> library, ISet<SongPath> liked,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        if (portable) tools.Require();
        var changes = new DiscoveryChanges([], [], []);
        var scores = ListeningProfile.Parse(ListeningProfile.Read(device));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var path in Prune(library, scores, liked, now))
        {
            try { Delete(device, path); changes.Removed.Add(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { changes.Errors.Add(ex.Message); }
        }
        var history = LoadHistory();
        var seen = new HashSet<string>(history);
        var picks = new List<MusicSuggestion>();
        foreach (var group in Groups)
            picks.AddRange(await catalog.Songs(group.Genre, group.Terms, FavoriteArtists(library, scores, group.Genre, now),
                PerGroup, library, seen, cancellation));
        progress?.Report(0.1);
        if (picks.Count < Groups.Length * PerGroup) changes.Errors.Add("MusicBrainz returned too few new songs.");
        for (var i = 0; i < picks.Count; i++)
        {
            try
            {
                changes.Added.Add(await Install(device, picks[i], cancellation));
                history.Add(LibraryViews.Identity(picks[i].Artist, picks[i].Title));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            { changes.Errors.Add($"{picks[i].Artist} — {picks[i].Title}: {ex.Message}"); }
            progress?.Report(0.1 + 0.9 * (i + 1) / picks.Count);
        }
        SaveHistory(history);
        return changes;
    }

    private async Task<SongPath> Install(PlayerDevice device, MusicSuggestion song, CancellationToken cancellation)
    {
        var volume = device.Sd ?? device.Internal;
        var path = new SongPath(volume.Storage, Folder + FileName(song));
        PlaylistCodec.ValidatePath(path.RockboxPath);
        var target = device.FilePath(path.Storage, path.RelativePath);
        if (File.Exists(target)) throw new IOException("Already on the player.");
        var temp = Directory.CreateTempSubdirectory("rbm-discovery-");
        try
        {
            var download = await runner.RunAsync(PodcastToolArguments.SongSearch(tools, $"{song.Artist} - {song.Title} official audio",
                Path.Combine(temp.FullName, "source.%(ext)s")), cancellation);
            var source = temp.EnumerateFiles("source.*").FirstOrDefault()?.FullName ?? throw new IOException(
                download.ExitCode == 0 ? "No YouTube match." : download.StandardError.Trim());
            var mp3 = Path.Combine(temp.FullName, "song.mp3");
            var transcode = await runner.RunAsync(PodcastToolArguments.SongTranscode(tools, source, mp3, song), cancellation);
            if (transcode.ExitCode != 0 || !File.Exists(mp3)) throw new IOException("Audio conversion failed. " + transcode.StandardError.Trim());
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < new FileInfo(mp3).Length + (16 << 20))
                throw new IOException("Not enough free space on the player.");
            var partial = target + ".rbmanager.tmp";
            File.Copy(mp3, partial, overwrite: true);
            File.Move(partial, target);
            return path;
        }
        finally { temp.Delete(recursive: true); }
    }

    private static void Delete(PlayerDevice device, SongPath path)
    {
        if (!IsDiscovery(path) || path.RelativePath.Split('/').Any(p => p is ".." or "." or "" || p.Contains(':')))
            throw new InvalidOperationException("Refusing to delete outside the Discovery folder.");
        var file = device.FilePath(path.Storage, path.RelativePath);
        var stem = Path.ChangeExtension(file, null);
        foreach (var existing in new[] { file, stem + CoverConverter.Suffix, stem + CoverConverter.LegacySuffix }.Where(File.Exists))
            File.Delete(existing);
    }

    public static string FileName(MusicSuggestion song)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("\"*:<>?|/\\").ToHashSet();
        var name = new string($"{song.Artist} - {song.Title}".Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return (name.Length > 100 ? name[..100] : name).Trim(' ', '.') + ".mp3";
    }

    // Every installed suggestion is remembered so a retired song is never offered again.
    private HashSet<string> LoadHistory()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(historyPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void SaveHistory(HashSet<string> history)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(historyPath))!);
        File.WriteAllText(historyPath, JsonSerializer.Serialize(history.Order(StringComparer.Ordinal)), new UTF8Encoding(false));
    }
}
