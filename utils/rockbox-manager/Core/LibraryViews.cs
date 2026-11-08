using System.Text;

namespace RockboxManager.Core;

public sealed record AlbumView(string Key, string Artist, string Name, List<Track> Tracks);

public static class LibraryViews
{
    // Preserve punctuation and version labels: a remix must not match its original.
    public static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    public static string Identity(string artist, string album) => Normalize(artist) + "\n" + Normalize(album);
    public static List<AlbumView> Albums(IEnumerable<Track> tracks) => tracks.GroupBy(t => t.AlbumKey)
        .Select(g => new AlbumView(g.Key, g.First().GroupArtist, g.First().Album,
            g.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title,
                StringComparer.CurrentCultureIgnoreCase).ToList()))
        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase).ToList();
    public static bool Matches(Track track, string query, string? genre = null) =>
        (genre is null || track.Genre.Equals(genre, StringComparison.Ordinal)) &&
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            $"{track.Title} {track.Artist} {track.AlbumArtist} {track.Album} {track.Genre} {track.Path.RelativePath}"
                .Contains(word, StringComparison.OrdinalIgnoreCase));
}
