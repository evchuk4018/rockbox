namespace RockboxManager.Core;

public static class PlayerLibrary
{
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
                string artist = "", album = "", genre = "";
                var duration = TimeSpan.Zero;
                try
                {
                    using var tags = TagLib.File.Create(new ReadOnlyAudio(file));
                    title = string.IsNullOrWhiteSpace(tags.Tag.Title) ? title : tags.Tag.Title;
                    artist = string.Join(", ", tags.Tag.Performers);
                    album = tags.Tag.Album ?? "";
                    genre = string.Join(", ", tags.Tag.Genres);
                    duration = tags.Properties.Duration;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    TagLib.UnsupportedFormatException or TagLib.CorruptFileException or ArgumentException or NotSupportedException)
                { /* A failed tag read must not hide the audio file. */ }
                tracks.Add(new(new(volume.Storage, relative), title, artist, album, genre, duration));
                if (tracks.Count % 25 == 0) progress?.Report($"Reading music… {tracks.Count:N0} songs");
            }
        }
        return tracks.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private sealed class ReadOnlyAudio(string path) : TagLib.File.IFileAbstraction
    {
        public string Name => path;
        public Stream ReadStream => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        public Stream WriteStream => throw new NotSupportedException("Audio is read-only in Rockbox Manager.");
        public void CloseStream(Stream stream) => stream.Dispose();
    }
}
