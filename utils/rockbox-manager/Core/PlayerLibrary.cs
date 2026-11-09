namespace RockboxManager.Core;

public static class PlayerLibrary
{
    internal static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));
    // Rockbox's built-in audio extensions (apps/filetypes.c). Unsupported tag formats still appear.
    private static readonly HashSet<string> AudioExtensions = new(
        ("mp3 mp2 mpa mp1 ogg oga wma wmv asf wav flac ac3 a52 mpc wv m4a m4b mp4 mod mpga " +
         "shn aif aiff spx opus sid adx nsf nsfe spc ape mac sap rm ra rmvb cmc cm3 cmr cms dmc " +
         "dlt mpt mpd rmt tmc tm8 tm2 oma aa3 at3 mmf au snd vox w64 tta ay vtx gbs hes sgc vgm vgz kss aac dsf dff iso")
        .Split(' ').Select(s => "." + s), StringComparer.OrdinalIgnoreCase);

    public static List<Track> Scan(PlayerDevice device, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var tracks = new List<Track>();
        foreach (var volume in device.Volumes)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden };
            foreach (var file in Directory.EnumerateFiles(volume.Root, "*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(volume.Root, file).Replace('\\', '/');
                if (relative.StartsWith(".rockbox/", StringComparison.OrdinalIgnoreCase) ||
                    !AudioExtensions.Contains(Path.GetExtension(file))) continue;
                var title = Path.GetFileNameWithoutExtension(file);
                string artist = "", album = "", genre = "", albumArtist = "";
                uint disc = 0, number = 0;
                bool? hasArtwork = null;
                string release = "", releaseGroup = "", videoId = "";
                var duration = TimeSpan.Zero;
                try
                {
                    using var tags = TagLib.File.Create(new ReadOnlyAudio(file));
                    title = string.IsNullOrWhiteSpace(tags.Tag.Title) ? title : tags.Tag.Title;
                    artist = string.Join(", ", tags.Tag.Performers);
                    album = tags.Tag.Album ?? "";
                    albumArtist = string.Join(", ", tags.Tag.AlbumArtists);
                    disc = tags.Tag.Disc; number = tags.Tag.Track;
                    hasArtwork = tags.Tag.Pictures.Length > 0;
                    release = tags.Tag.MusicBrainzReleaseId ?? "";
                    releaseGroup = tags.Tag.MusicBrainzReleaseGroupId ?? "";
                    if (tags.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                        videoId = YouTubeId(TagLib.Id3v2.UrlLinkFrame.Get(id3, "WOAS", false)?.Text.FirstOrDefault() ?? "") ?? "";
                    // ID3v2.3 treats slash as a separator, including inside our parent genres.
                    genre = string.Join(", ", tags.Tag.Genres)
                        .Replace("Hip-Hop, Rap-", "Hip-Hop/Rap-", StringComparison.OrdinalIgnoreCase)
                        .Replace("R&B, Soul-", "R&B/Soul-", StringComparison.OrdinalIgnoreCase);
                    duration = tags.Properties.Duration;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    TagLib.UnsupportedFormatException or TagLib.CorruptFileException or ArgumentException or NotSupportedException)
                { /* A failed tag read must not hide the audio file. */ }
                tracks.Add(new(new(volume.Storage, relative), title, artist, album, genre, duration,
                    AlbumArtist: albumArtist, DiscNumber: disc, TrackNumber: number)
                { YouTubeVideoId = videoId, HasEmbeddedArtwork = hasArtwork, ArtworkReleaseId = release, ArtworkReleaseGroupId = releaseGroup });
                if (tracks.Count % 25 == 0) progress?.Report($"Reading music… {tracks.Count:N0} songs");
            }
        }
        return tracks.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string? YouTubeId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return null;
        string id = "";
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = uri.AbsolutePath.Trim('/');
        else if (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath == "/watch") id = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == "v").Select(p => p[1]).FirstOrDefault() ?? "";
            else if (uri.AbsolutePath.StartsWith("/shorts/") || uri.AbsolutePath.StartsWith("/embed/")) id = uri.AbsolutePath.Split('/').Last();
        }
        return System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$") ? id : null;
    }

    internal sealed class ReadOnlyAudio(string path) : TagLib.File.IFileAbstraction
    {
        public string Name => path;
        public Stream ReadStream => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        public Stream WriteStream => throw new NotSupportedException("Audio is read-only in Rockbox Manager.");
        public void CloseStream(Stream stream) => stream.Dispose();
    }
}
