using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RockboxManager.Core;

public sealed record ArtworkResult(SongPath Track, string Status, string Source = "", string Message = "",
    Storage Storage = Storage.Unknown, string RelativePath = "", string? Hash = null)
{
    public string SourceKind { get; init; } = "";
    public string? OriginalImagePath { get; init; }
    public string? OriginalImageHash { get; init; }
    public string? CacheMetadataPath { get; init; }
    public CropRectangle? Crop { get; init; }
    [JsonIgnore] public byte[]? Bitmap { get; init; }
    [JsonIgnore] public FileChange? Change { get; init; }
}

public sealed class ArtworkService
{
    private readonly string cacheRoot;
    private readonly Func<byte[], RgbaImage> decode;
    private readonly HttpClient http;
    private readonly Dictionary<string, CoverSource> resolved = [];
    private readonly Dictionary<string, string> releaseHints = [];
    private DateTime nextMusicBrainz;
    private sealed record CoverSource(string Status, string Source = "", string Message = "", byte[]? Bytes = null,
        bool Converted = false, int SourceInset = 0, string Kind = "Album", string? OriginalPath = null,
        string? MetadataPath = null, CropRectangle? Crop = null);
    private sealed record CacheEntry(string Status, string Source, string Message, DateTime Checked, string? Image, CropRectangle? Crop = null, int Version = 0, string? OriginalImageHash = null);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    public ArtworkService(string cacheRoot, Func<byte[], RgbaImage> decode, HttpClient http)
    {
        this.cacheRoot = cacheRoot; this.decode = decode; this.http = http;
        try
        {
            var path = Path.Combine(cacheRoot, "verified-release-hints.json");
            if (File.Exists(path))
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [])
                    if (Guid.TryParse(value, out _)) releaseHints[key] = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public int ImportVerifiedReview(string path, IReadOnlyList<Track> tracks)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in document.RootElement.EnumerateObject().Select(p => p.Value))
        {
            if (Text(row, "status") != "verified" || !Guid.TryParse(Text(row, "release_id"), out var id) ||
                !row.TryGetProperty("tags", out var tags)) continue;
            static string First(JsonElement tags, string key) => tags.TryGetProperty(key, out var array) && array.GetArrayLength() > 0
                ? array[0].GetString() ?? "" : "";
            var artist = First(tags, "albumartist"); var performer = First(tags, "artist");
            var key = LibraryViews.Identity(artist.Length == 0 ? performer : artist, First(tags, "album"));
            var title = LibraryViews.Normalize(Text(row, "title"));
            if (tracks.Any(t => t.AlbumKey == key && LibraryViews.Normalize(t.Artist) == LibraryViews.Normalize(performer) &&
                LibraryViews.Normalize(t.Title) == title && row.TryGetProperty("duration_ms", out var duration) &&
                Math.Abs(t.Duration.TotalMilliseconds - duration.GetDouble()) < 1500)) releaseHints.TryAdd(key, id.ToString());
        }
        Directory.CreateDirectory(cacheRoot);
        AtomicWrite(Path.Combine(cacheRoot, "verified-release-hints.json"), JsonSerializer.SerializeToUtf8Bytes(releaseHints));
        return releaseHints.Count;
    }

    public async Task<List<ArtworkResult>> Prepare(PlayerDevice device, IReadOnlyList<Track> tracks,
        IProgress<string>? progress = null, CancellationToken cancellation = default, bool retry = false)
    {
        Directory.CreateDirectory(cacheRoot);
        if (retry) resolved.Clear();
        var singleAlbumFolders = tracks.GroupBy(t => (t.Path.Storage, DirectoryOf(t.Path.RelativePath)))
            .Where(g => g.Select(t => t.AlbumKey).Distinct().Count() == 1).Select(g => g.Key).ToHashSet();
        var results = new List<ArtworkResult>();
        var destinations = new Dictionary<string, (string Album, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            cancellation.ThrowIfCancellationRequested();
            ArtworkResult result;
            try
            {
                var target = Destination(track, tracks, device);
                byte[]? installed = target is { } destination && File.Exists(device.FilePath(destination.Item1, destination.Item2))
                    ? File.ReadAllBytes(device.FilePath(destination.Item1, destination.Item2)) : null;
                if (installed is not null && !CoverConverter.IsCover(installed)) installed = null;
                var source = await Resolve(device, track, singleAlbumFolders.Contains((track.Path.Storage, DirectoryOf(track.Path.RelativePath))), installed, cancellation, retry);
                if (source.Bytes is null) result = new(track.Path, source.Status, source.Source, source.Message);
                else
                {
                    var bitmap = source.Converted ? source.Bytes : CoverConverter.Convert(decode(source.Bytes), source.SourceInset, source.Kind == "YouTube", source.Crop);
                    bool sharedVideo = source.Kind == "YouTube" && target is { Item1: Storage.Internal } central &&
                        central.Item2.StartsWith(".rockbox/albumart/", StringComparison.OrdinalIgnoreCase) &&
                        tracks.Any(t => t.AlbumKey == track.AlbumKey && t.YouTubeVideoId != track.YouTubeVideoId);
                    if (sharedVideo) target = GenericDestination(track, device);
                    if (target is null) result = sharedVideo
                        ? new(track.Path, "PathConflict", source.Source, "A video thumbnail cannot be shared with different tracks through central album art.")
                        : new(track.Path, "PathTooLong", source.Source, "No unambiguous artwork path fits Rockbox's 259-byte limit.");
                    else
                    {
                        var (storage, relative) = target.Value;
                        string identity = storage + ":" + relative;
                        var hash = FileSnapshot.Digest(bitmap);
                        if (destinations.TryGetValue(identity, out var previous) && (previous.Album != (source.Kind + ":" + source.Source) || previous.Hash != hash))
                            result = new(track.Path, "PathConflict", source.Source, "Different artwork would share this filename.", storage, relative, hash);
                        else
                        {
                            destinations[identity] = (source.Kind + ":" + source.Source, hash);
                            var snapshot = FileSnapshot.Read(device, storage, relative);
                            result = new(track.Path, snapshot.Hash == hash ? "Installed" : "Ready", source.Source, source.Message,
                                Storage: storage, RelativePath: relative, Hash: hash)
                            {
                                SourceKind = source.Kind, OriginalImagePath = source.OriginalPath,
                                OriginalImageHash = source.Converted ? null : FileSnapshot.Digest(source.Bytes),
                                CacheMetadataPath = source.MetadataPath, Crop = source.Crop,
                                Bitmap = bitmap,
                                Change = snapshot.Hash == hash ? null : new FileChange { Storage = storage,
                                    RelativePath = relative, BeforeHash = snapshot.Hash, Content = bitmap, ArtworkTrack = track.Path }
                            };
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or FormatException or JsonException or OverflowException)
            { result = new(track.Path, "Failed", Message: ex.Message); }
            results.Add(result);
            progress?.Report($"Artwork {results.Count:N0}/{tracks.Count:N0} · {track.Artist} — {track.Album} · {result.Status}");
        }
        // A collision invalidates every affected result, including the first one.
        var conflicts = results.Where(r => r.Status == "PathConflict").Select(r => r.Track.Key).ToHashSet();
        var targets = results.Where(r => r.RelativePath.Length > 0).GroupBy(r => r.Storage + ":" + r.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var group in targets)
        {
            var members = group.ToList();
            if (members.Select(r => r.Hash).Distinct().Count() > 1 ||
                members.Any(r => conflicts.Contains(r.Track.Key)))
                foreach (var member in members) conflicts.Add(member.Track.Key);
        }
        // Conflict results carry their target so the prior result can also be invalidated.
        foreach (var conflict in results.Where(r => r.Status == "PathConflict" && r.RelativePath.Length > 0))
            foreach (var prior in results.Where(r => r.Storage == conflict.Storage && r.RelativePath == conflict.RelativePath)) conflicts.Add(prior.Track.Key);
        for (int i = 0; i < results.Count; i++)
            if (conflicts.Contains(results[i].Track.Key)) results[i] = results[i] with { Status = "PathConflict", Change = null, Bitmap = null };
        return results;
    }

    public static string DirectoryOf(string relative) => relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
    private static string FixPart(string value) => new(value.Select(c => c == '"' ? '\'' : "*/:<>?\\|".Contains(c) ? '_' : c).ToArray());
    public static (Storage, string)? Destination(Track track, IReadOnlyList<Track> tracks, PlayerDevice? device = null)
    {
        var sidecar = Path.ChangeExtension(track.Path.RelativePath, null) + ".61x61.bmp";
        try
        {
            PlaylistCodec.ValidatePath((track.Path with { RelativePath = sidecar }).RockboxPath);
            if (!tracks.Any(t => t.Path.Storage == track.Path.Storage && t.AlbumKey != track.AlbumKey &&
                (Path.ChangeExtension(t.Path.RelativePath, null) + ".61x61.bmp").Equals(sidecar, StringComparison.OrdinalIgnoreCase)))
                return (track.Path.Storage, sidecar);
        }
        catch (InvalidOperationException) { }
        if (string.IsNullOrWhiteSpace(track.GroupArtist) || string.IsNullOrWhiteSpace(track.Album)) return GenericDestination(track, device);
        var name = FixPart(track.GroupArtist + "-" + track.Album) + ".61x61.bmp";
        var central = ".rockbox/albumart/" + name;
        if (tracks.Any(t => t.AlbumKey != track.AlbumKey &&
            FixPart(t.GroupArtist + "-" + t.Album).Equals(name[..^10], StringComparison.OrdinalIgnoreCase))) return GenericDestination(track, device);
        try { PlaylistCodec.ValidatePath("/" + central); return (Storage.Internal, central); }
        catch (InvalidOperationException) { return GenericDestination(track, device); }
    }
    private static (Storage, string)? GenericDestination(Track track, PlayerDevice? device)
    {
        if (device is null || !SyncEngine.IsSingleSongFolder(device, track.Path)) return null;
        var directory = DirectoryOf(track.Path.RelativePath);
        var path = (directory.Length == 0 ? "" : directory + "/") + "cover.61x61.bmp";
        try { PlaylistCodec.ValidatePath((track.Path with { RelativePath = path }).RockboxPath); return (track.Path.Storage, path); }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<CoverSource> Resolve(PlayerDevice device, Track track, bool singleAlbumFolder, byte[]? installed, CancellationToken cancellation, bool retry)
    {
        var audioPath = device.FilePath(track.Path.Storage, track.Path.RelativePath);
        string release = track.ArtworkReleaseId, group = track.ArtworkReleaseGroupId;
        CoverSource? PlayerCover()
        {
            if (installed is not null) return new("Ready", "Player cover", Bytes: installed, Converted: true, Kind: "Player");
            string[] legacyPaths = [device.FilePath(track.Path.Storage, Path.ChangeExtension(track.Path.RelativePath, null) + CoverConverter.LegacySuffix),
                device.FilePath(Storage.Internal, ".rockbox/albumart/" + FixPart(track.GroupArtist + "-" + track.Album) + CoverConverter.LegacySuffix)];
            foreach (var path in legacyPaths)
            {
                if (!File.Exists(path)) continue;
                var bytes = ReadImage(path);
                if (CoverConverter.IsCover(bytes, 44)) return new("Ready", "Legacy player cover", Bytes: bytes, SourceInset: 2, Kind: "Player");
            }
            return null;
        }
        if (track.HasEmbeddedArtwork != false) try
        {
            using var audio = TagLib.File.Create(new PlayerLibrary.ReadOnlyAudio(audioPath));
            var picture = audio.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? audio.Tag.Pictures.FirstOrDefault();
            if (picture is not null && picture.Data.Count > 0)
            {
                var bytes = picture.Data.Data;
                try { _ = decode(bytes); return new("Ready", "Embedded cover", Bytes: bytes, Kind: "Embedded"); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or FormatException) { }
            }
            release = audio.Tag.MusicBrainzReleaseId ?? ""; group = audio.Tag.MusicBrainzReleaseGroupId ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.UnsupportedFormatException or
            TagLib.CorruptFileException or ArgumentException or NotSupportedException) { }
        var directory = Path.GetDirectoryName(audioPath)!;
        var names = new List<string> { Path.GetFileNameWithoutExtension(audioPath) };
        if (!string.IsNullOrWhiteSpace(track.Album)) names.Add(FixPart(track.Album));
        if (singleAlbumFolder) names.AddRange(["cover", "folder", "front"]);
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var extension in ImageExtensions)
            {
                var path = Path.Combine(directory, name + extension);
                if (!File.Exists(path)) continue;
                try { var bytes = ReadImage(path); _ = decode(bytes); return new("Ready", path, Bytes: bytes, Kind: "Local", OriginalPath: path); }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or FormatException) { }
            }
        if (!retry)
        {
            var albumKey = FileSnapshot.Digest(Encoding.UTF8.GetBytes(track.AlbumKey));
            if (ReadCachedImage(albumKey, "Album") is { } cachedAlbum) return cachedAlbum;
            if (ReadCachedImage("youtube-" + track.YouTubeVideoId, "YouTube") is { } cachedVideo) return cachedVideo;
            if (PlayerCover() is { } player) return player;
        }
        var priorVideo = retry ? ReadCachedImage("youtube-" + track.YouTubeVideoId, "YouTube") : null;
        bool installedVideo = installed is not null && priorVideo?.Bytes is not null &&
            CoverConverter.Convert(decode(priorVideo.Bytes), thumbnail: true, crop: priorVideo.Crop).SequenceEqual(installed);
        var album = await ResolveAlbum(track, release, group, cancellation, retry);
        if (album.Bytes is not null) return album;
        var recording = await ResolveRecording(track, cancellation, retry);
        if (recording.Bytes is not null) return recording;
        var fallback = await ResolveVideo(track.YouTubeVideoId, cancellation, retry);
        if (fallback.Bytes is not null)
        {
            if (installed is not null && retry && !installedVideo) return PlayerCover()!;
            return album.Status == "Failed" ? fallback with { Message = album.Message } :
                recording.Status == "Failed" ? fallback with { Message = recording.Message } : fallback;
        }
        return PlayerCover() ?? (album.Status == "Failed" ? album : recording.Status == "Failed" ? recording : fallback.Status == "Failed" ? fallback : album);
    }

    private CoverSource? ReadCachedImage(string key, string kind)
    {
        var metadataPath = Path.Combine(cacheRoot, key + ".json");
        try
        {
            if (!File.Exists(metadataPath)) return null;
            var cached = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metadataPath));
            if (cached?.Image is not { } image || Path.GetFileName(image) != image) return null;
            var path = Path.Combine(cacheRoot, image); var bytes = ReadImage(path); var decoded = decode(bytes);
            if (kind == "YouTube" && (decoded.Width <= 120 || decoded.Height <= 90)) return null;
            var crop = cached.OriginalImageHash == FileSnapshot.Digest(bytes) ? cached.Crop : null;
            return new("Ready", cached.Source, Bytes: bytes, Kind: kind, OriginalPath: path, MetadataPath: metadataPath, Crop: crop);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException or FormatException) { return null; }
    }

    private async Task<CoverSource> ResolveAlbum(Track track, string release, string group, CancellationToken cancellation, bool retry)
    {
        if (resolved.TryGetValue(track.AlbumKey, out var existing)) return existing;
        var key = FileSnapshot.Digest(Encoding.UTF8.GetBytes(track.AlbumKey));
        var metadataPath = Path.Combine(cacheRoot, key + ".json");
        try
        {
            if (File.Exists(metadataPath))
            {
                var cached = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metadataPath));
                if (cached?.Image is { } image && Path.GetFileName(image) == image)
                {
                    var bytes = ReadImage(Path.Combine(cacheRoot, image)); _ = decode(bytes);
                    return resolved[track.AlbumKey] = new("Ready", cached.Source, Bytes: bytes, OriginalPath: Path.Combine(cacheRoot, image), MetadataPath: metadataPath, Crop: cached.OriginalImageHash == FileSnapshot.Digest(bytes) ? cached.Crop : null);
                }
                if (!retry && cached is { Version: 2, Status: "Missing" } && DateTime.UtcNow - cached.Checked < TimeSpan.FromHours(24))
                    return resolved[track.AlbumKey] = new(cached.Status, cached.Source, cached.Message);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException or FormatException) { }
        CoverSource found;
        try
        {
            if (!Guid.TryParse(release, out _) && releaseHints.TryGetValue(track.AlbumKey, out var hint)) release = hint;
            if (!Guid.TryParse(release, out _) && !Guid.TryParse(group, out _)) group = await MatchAlbum(track, cancellation) ?? "";
            found = new("Missing", Message: "No unambiguous release cover found.");
            foreach (var (kind, id) in new[] { ("release", release), ("release-group", group) })
            {
                if (!Guid.TryParse(id, out var uuid)) continue;
                var url = $"https://coverartarchive.org/{kind}/{uuid}/front-250";
                var bytes = await Get(url, cancellation);
                if (bytes is null) continue;
                _ = decode(bytes); found = new("Ready", url, Bytes: bytes); break;
            }
            if (found.Bytes is null && Guid.TryParse(release, out _) && !Guid.TryParse(group, out _))
            {
                var metadata = await Get($"https://musicbrainz.org/ws/2/release/{release}?fmt=json&inc=release-groups", cancellation);
                if (metadata is not null)
                {
                    using var doc = JsonDocument.Parse(metadata);
                    if (doc.RootElement.TryGetProperty("release-group", out var rg) && Guid.TryParse(Text(rg, "id"), out var id))
                    {
                        var url = $"https://coverartarchive.org/release-group/{id}/front-250";
                        var bytes = await Get(url, cancellation);
                        if (bytes is not null) { _ = decode(bytes); found = new("Ready", url, Bytes: bytes); }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        { found = new("Failed", Message: "Artwork lookup can be retried: " + ex.Message); }
        string? imageName = null;
        if (found.Bytes is not null)
        { imageName = key + ".image"; AtomicWrite(Path.Combine(cacheRoot, imageName), found.Bytes); }
        AtomicWrite(metadataPath, JsonSerializer.SerializeToUtf8Bytes(new CacheEntry(found.Status, found.Source, found.Message, DateTime.UtcNow, imageName, Version: 2, OriginalImageHash: found.Bytes is null ? null : FileSnapshot.Digest(found.Bytes)), JsonOptions));
        return resolved[track.AlbumKey] = found with { OriginalPath = imageName is null ? null : Path.Combine(cacheRoot, imageName), MetadataPath = metadataPath };
    }

    private async Task<CoverSource> ResolveRecording(Track track, CancellationToken cancellation, bool retry)
    {
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title) || track.Duration <= TimeSpan.Zero) return new("Missing");
        var key = "recording-" + FileSnapshot.Digest(Encoding.UTF8.GetBytes(track.Artist + "\n" + track.Title + "\n" + track.Album + "\n" + track.Duration.TotalMilliseconds));
        var metadataPath = Path.Combine(cacheRoot, key + ".json");
        try
        {
            if (File.Exists(metadataPath))
            {
                var cached = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metadataPath));
                if (cached?.Image is { } image && Path.GetFileName(image) == image)
                {
                    var path = Path.Combine(cacheRoot, image); var bytes = ReadImage(path); _ = decode(bytes);
                    return new("Ready", cached.Source, Bytes: bytes, OriginalPath: path, MetadataPath: metadataPath, Crop: cached.OriginalImageHash == FileSnapshot.Digest(bytes) ? cached.Crop : null);
                }
                if (!retry && cached is { Status: "Missing", Version: 2 } && DateTime.UtcNow - cached.Checked < TimeSpan.FromHours(24)) return new("Missing");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException or FormatException) { }
        try
        {
            var id = await MatchRecording(track, cancellation);
            if (id is not null)
            {
                var url = $"https://coverartarchive.org/release-group/{id}/front-250";
                var bytes = await Get(url, cancellation);
                if (bytes is not null)
                {
                    _ = decode(bytes); var image = key + ".image"; var path = Path.Combine(cacheRoot, image);
                    AtomicWrite(path, bytes);
                    AtomicWrite(metadataPath, JsonSerializer.SerializeToUtf8Bytes(new CacheEntry("Ready", url, "", DateTime.UtcNow, image, Version: 2, OriginalImageHash: FileSnapshot.Digest(bytes)), JsonOptions));
                    return new("Ready", url, Bytes: bytes, OriginalPath: path, MetadataPath: metadataPath);
                }
            }
            AtomicWrite(metadataPath, JsonSerializer.SerializeToUtf8Bytes(new CacheEntry("Missing", "", "", DateTime.UtcNow, null, Version: 2), JsonOptions));
            return new("Missing");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        { return new("Failed", Message: "Artwork lookup can be retried: " + ex.Message); }
    }

    private async Task<CoverSource> ResolveVideo(string id, CancellationToken cancellation, bool retry)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")) return new("Missing");
        var key = "youtube-" + id;
        var metadataPath = Path.Combine(cacheRoot, key + ".json");
        CacheEntry? cached = null;
        try
        {
            if (File.Exists(metadataPath)) cached = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metadataPath));
            if (!retry && cached?.Image is { } image && Path.GetFileName(image) == image)
            {
                var path = Path.Combine(cacheRoot, image); var bytes = ReadImage(path); var decoded = decode(bytes);
                if (decoded.Width > 120 && decoded.Height > 90)
                    return new("Ready", cached.Source, Bytes: bytes, Kind: "YouTube", OriginalPath: path, MetadataPath: metadataPath, Crop: cached.OriginalImageHash == FileSnapshot.Digest(bytes) ? cached.Crop : null);
            }
            if (!retry && cached is { Status: "Missing", Version: 2 } && DateTime.UtcNow - cached.Checked < TimeSpan.FromHours(24)) return new("Missing");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException or FormatException) { }
        try
        {
            foreach (var size in new[] { "maxresdefault", "sddefault", "hqdefault" })
            {
                var url = $"https://i.ytimg.com/vi/{id}/{size}.jpg";
                var bytes = await Get(url, cancellation);
                if (bytes is null || bytes.Length == 0) continue;
                try
                {
                    var image = decode(bytes);
                    if (image.Width <= 120 || image.Height <= 90) continue;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or FormatException) { continue; }
                var path = Path.Combine(cacheRoot, key + ".image");
                // Preserve a manual crop only when the original image is unchanged.
                var crop = cached?.Image is { } previous && Path.GetFileName(previous) == previous &&
                    File.Exists(Path.Combine(cacheRoot, previous)) &&
                    cached.OriginalImageHash == FileSnapshot.Digest(bytes) && FileSnapshot.Digest(ReadImage(Path.Combine(cacheRoot, previous))) == cached.OriginalImageHash ? cached.Crop : null;
                AtomicWrite(path, bytes);
                AtomicWrite(metadataPath, JsonSerializer.SerializeToUtf8Bytes(new CacheEntry("Ready", url, "", DateTime.UtcNow, key + ".image", crop, 2, FileSnapshot.Digest(bytes)), JsonOptions));
                return new("Ready", url, Bytes: bytes, Kind: "YouTube", OriginalPath: path, MetadataPath: metadataPath, Crop: crop);
            }
            AtomicWrite(metadataPath, JsonSerializer.SerializeToUtf8Bytes(new CacheEntry("Missing", "", "", DateTime.UtcNow, null, Version: 2), JsonOptions));
            return new("Missing");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        { return new("Failed", Message: "Artwork lookup can be retried: " + ex.Message); }
    }

    public static string? ExactRecordingMatch(JsonElement root, Track track)
    {
        if (track.Duration <= TimeSpan.Zero || !root.TryGetProperty("recordings", out var recordings)) return null;
        var matches = new List<(string Id, string Title)>();
        foreach (var recording in recordings.EnumerateArray())
        {
            if (recording.TryGetProperty("video", out var isVideo) && isVideo.ValueKind == JsonValueKind.True ||
                Text(recording, "disambiguation") is { Length: > 0 } qualifier && !LibraryViews.Normalize(track.Title).Contains(LibraryViews.Normalize(qualifier)) ||
                LibraryViews.Normalize(Text(recording, "title")) != LibraryViews.Normalize(track.Title) ||
                !recording.TryGetProperty("length", out var length) || !length.TryGetInt64(out var ms) ||
                Math.Abs(ms - track.Duration.TotalMilliseconds) > 3500 ||
                !recording.TryGetProperty("artist-credit", out var credit) ||
                LibraryViews.Normalize(Credit(credit)) != LibraryViews.Normalize(track.Artist) ||
                !recording.TryGetProperty("releases", out var releases)) continue;
            foreach (var release in releases.EnumerateArray())
                if (release.TryGetProperty("release-group", out var group) && EligibleRelease(release, group) && Guid.TryParse(Text(group, "id"), out var id))
                    matches.Add((id.ToString(), Text(group, "title")));
        }
        var named = matches.Where(m => !string.IsNullOrWhiteSpace(track.Album) && LibraryViews.Normalize(m.Title) == LibraryViews.Normalize(track.Album)).Select(m => m.Id).Distinct().ToArray();
        if (named.Length == 1) return named[0];
        if (named.Length > 1) return null;
        var all = matches.Select(m => m.Id).Distinct().ToArray();
        return all.Length == 1 ? all[0] : null;
    }
    private static bool EligibleRelease(JsonElement release, JsonElement group)
    {
        var status = Text(release, "status");
        if (status.Length > 0 && !status.Equals("Official", StringComparison.OrdinalIgnoreCase)) return false;
        if (release.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.True) return false;
        if (group.TryGetProperty("secondary-types", out var types) && types.EnumerateArray().Any()) return false;
        var type = Text(group, "primary-type");
        return type.Length == 0 || type is "Album" or "Single" or "EP";
    }
    private static string Credit(JsonElement credit) => string.Concat(credit.EnumerateArray().Select(c =>
        (Text(c, "name") is { Length: > 0 } n ? n : c.TryGetProperty("artist", out var a) ? Text(a, "name") : "") + Text(c, "joinphrase")));
    private async Task<string?> MatchRecording(Track track, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title) || track.Duration <= TimeSpan.Zero) return null;
        static string Escape(string value) => Regex.Replace(value, @"([+\-&|!(){}\[\]^""~*?:\\/])", @"\$1");
        var query = $"recording:\"{Escape(track.Title)}\" AND artist:\"{Escape(track.Artist)}\"";
        var bytes = await Get("https://musicbrainz.org/ws/2/recording/?fmt=json&limit=100&query=" + Uri.EscapeDataString(query), cancellation);
        if (bytes is null) return null;
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.TryGetProperty("count", out var count) && count.GetInt32() > 100) return null;
        return ExactRecordingMatch(document.RootElement, track);
    }
    public static string? ExactAlbumMatch(JsonElement root, string artist, string album)
    {
        if (!root.TryGetProperty("release-groups", out var groups)) return null;
        var matches = groups.EnumerateArray().Where(g => LibraryViews.Normalize(Text(g, "title")) == LibraryViews.Normalize(album) &&
            g.TryGetProperty("artist-credit", out var credit) && LibraryViews.Normalize(string.Concat(credit.EnumerateArray().Select(c =>
                (Text(c, "name") is { Length: > 0 } n ? n : c.TryGetProperty("artist", out var a) ? Text(a, "name") : "") + Text(c, "joinphrase")))) == LibraryViews.Normalize(artist))
            .Select(g => Text(g, "id")).Where(id => Guid.TryParse(id, out _)).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private async Task<string?> MatchAlbum(Track track, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(track.GroupArtist) || string.IsNullOrWhiteSpace(track.Album)) return null;
        static string Escape(string value) => Regex.Replace(value, @"([+\-&|!(){}\[\]^""~*?:\\/])", @"\$1");
        var query = $"releasegroup:\"{Escape(track.Album)}\" AND artist:\"{Escape(track.GroupArtist)}\"";
        var bytes = await Get("https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=100&query=" + Uri.EscapeDataString(query), cancellation);
        if (bytes is null) return null;
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.TryGetProperty("count", out var count) && count.GetInt32() > 100) return null;
        var group = ExactAlbumMatch(document.RootElement, track.GroupArtist, track.Album);
        if (group is not null) return group;
        var releaseQuery = $"release:\"{Escape(track.Album)}\" AND artist:\"{Escape(track.GroupArtist)}\"";
        var releaseBytes = await Get("https://musicbrainz.org/ws/2/release/?fmt=json&limit=100&query=" + Uri.EscapeDataString(releaseQuery), cancellation);
        if (releaseBytes is null) return null;
        using var releaseDocument = JsonDocument.Parse(releaseBytes);
        var root = releaseDocument.RootElement;
        if (root.TryGetProperty("count", out var releaseCount) && releaseCount.GetInt32() > 100 || !root.TryGetProperty("releases", out var releases)) return null;
        var ids = releases.EnumerateArray().Where(r => LibraryViews.Normalize(Text(r, "title")) == LibraryViews.Normalize(track.Album) &&
            r.TryGetProperty("artist-credit", out var credit) && LibraryViews.Normalize(Credit(credit)) == LibraryViews.Normalize(track.GroupArtist) &&
            r.TryGetProperty("release-group", out var g) && EligibleRelease(r, g))
            .Select(r => Text(r.GetProperty("release-group"), "id")).Where(id => Guid.TryParse(id, out _)).Distinct().ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private async Task<byte[]?> Get(string url, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (url.StartsWith("https://musicbrainz.org/", StringComparison.Ordinal))
            {
                var delay = nextMusicBrainz - DateTime.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
                nextMusicBrainz = DateTime.UtcNow.AddMilliseconds(1100);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("RockboxManager/1.1 (+https://github.com/evchuk4018/rockbox)");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is (HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) && attempt < 2)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                await Task.Delay(wait > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait, cancellation); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 12 * 1024 * 1024) throw new IOException("Artwork response is too large.");
            using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var output = new MemoryStream(); var buffer = new byte[16384];
            int read; while ((read = await stream.ReadAsync(buffer, cancellation)) != 0)
            { if (output.Length + read > 12 * 1024 * 1024) throw new IOException("Artwork response is too large."); output.Write(buffer, 0, read); }
            return output.ToArray();
        }
    }
    private static byte[] ReadImage(string path)
    { if (new FileInfo(path).Length > 12 * 1024 * 1024) throw new IOException("Artwork file is too large."); return File.ReadAllBytes(path); }
    private static void AtomicWrite(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, content); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
