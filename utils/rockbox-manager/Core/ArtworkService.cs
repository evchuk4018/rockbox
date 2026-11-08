using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RockboxManager.Core;

public sealed record ArtworkResult(SongPath Track, string Status, string Source = "", string Message = "",
    Storage Storage = Storage.Unknown, string RelativePath = "", string? Hash = null)
{
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
    private sealed record CoverSource(string Status, string Source = "", string Message = "", byte[]? Bytes = null, bool Converted = false);
    private sealed record CacheEntry(string Status, string Source, string Message, DateTime Checked, string? Image);
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
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(cacheRoot);
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
                var target = Destination(track, tracks);
                byte[]? installed = target is { } destination && File.Exists(device.FilePath(destination.Item1, destination.Item2))
                    ? File.ReadAllBytes(device.FilePath(destination.Item1, destination.Item2)) : null;
                if (installed is not null && !CoverConverter.IsCover(installed)) installed = null;
                var source = await Resolve(device, track, singleAlbumFolders.Contains((track.Path.Storage, DirectoryOf(track.Path.RelativePath))), installed, cancellation);
                if (source.Bytes is null) result = new(track.Path, source.Status, source.Source, source.Message);
                else
                {
                    var bitmap = source.Converted ? source.Bytes : CoverConverter.Convert(decode(source.Bytes));
                    if (target is null) result = new(track.Path, "PathTooLong", source.Source, "No unambiguous artwork path fits Rockbox's 259-byte limit.");
                    else
                    {
                        var (storage, relative) = target.Value;
                        string identity = storage + ":" + relative;
                        var hash = FileSnapshot.Digest(bitmap);
                        if (destinations.TryGetValue(identity, out var previous) && (previous.Album != track.AlbumKey || previous.Hash != hash))
                            result = new(track.Path, "PathConflict", source.Source, "Different albums would share this artwork filename.");
                        else
                        {
                            destinations[identity] = (track.AlbumKey, hash);
                            var snapshot = FileSnapshot.Read(device, storage, relative);
                            result = new(track.Path, snapshot.Hash == hash ? "Installed" : "Ready", source.Source,
                                Storage: storage, RelativePath: relative, Hash: hash)
                            {
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
        return results;
    }

    public static string DirectoryOf(string relative) => relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
    private static string FixPart(string value) => new(value.Select(c => c == '"' ? '\'' : "*/:<>?\\|".Contains(c) ? '_' : c).ToArray());
    public static (Storage, string)? Destination(Track track, IReadOnlyList<Track> tracks)
    {
        var sidecar = Path.ChangeExtension(track.Path.RelativePath, null) + ".44x44.bmp";
        try
        {
            PlaylistCodec.ValidatePath((track.Path with { RelativePath = sidecar }).RockboxPath);
            if (!tracks.Any(t => t.Path.Storage == track.Path.Storage && t.AlbumKey != track.AlbumKey &&
                (Path.ChangeExtension(t.Path.RelativePath, null) + ".44x44.bmp").Equals(sidecar, StringComparison.OrdinalIgnoreCase)))
                return (track.Path.Storage, sidecar);
        }
        catch (InvalidOperationException) { }
        if (string.IsNullOrWhiteSpace(track.GroupArtist) || string.IsNullOrWhiteSpace(track.Album)) return null;
        var name = FixPart(track.GroupArtist + "-" + track.Album) + ".44x44.bmp";
        var central = ".rockbox/albumart/" + name;
        if (tracks.Any(t => t.AlbumKey != track.AlbumKey &&
            FixPart(t.GroupArtist + "-" + t.Album).Equals(name[..^10], StringComparison.OrdinalIgnoreCase))) return null;
        try { PlaylistCodec.ValidatePath("/" + central); return (Storage.Internal, central); }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<CoverSource> Resolve(PlayerDevice device, Track track, bool singleAlbumFolder, byte[]? installed, CancellationToken cancellation)
    {
        var audioPath = device.FilePath(track.Path.Storage, track.Path.RelativePath);
        string release = track.ArtworkReleaseId, group = track.ArtworkReleaseGroupId;
        if (track.HasEmbeddedArtwork != false) try
        {
            using var audio = TagLib.File.Create(new PlayerLibrary.ReadOnlyAudio(audioPath));
            var picture = audio.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? audio.Tag.Pictures.FirstOrDefault();
            if (picture is not null && picture.Data.Count > 0)
            {
                var bytes = picture.Data.Data;
                try { _ = decode(bytes); return new("Ready", "Embedded cover", Bytes: bytes); }
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
                try { var bytes = ReadImage(path); _ = decode(bytes); return new("Ready", path, Bytes: bytes); }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or FormatException) { }
            }
        if (resolved.TryGetValue(track.AlbumKey, out var existing))
            return existing.Bytes is null && installed is not null ? new("Ready", "Player cover", Bytes: installed, Converted: true) : existing;
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
                    return resolved[track.AlbumKey] = new("Ready", cached.Source, Bytes: bytes);
                }
                if (installed is null && cached is not null && DateTime.UtcNow - cached.Checked < TimeSpan.FromHours(24) && cached.Status == "Missing")
                    return resolved[track.AlbumKey] = new(cached.Status, cached.Source, cached.Message);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException or FormatException) { }
        if (installed is not null) return new("Ready", "Player cover", Bytes: installed, Converted: true);
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
        AtomicWrite(metadataPath, JsonSerializer.SerializeToUtf8Bytes(new CacheEntry(found.Status, found.Source, found.Message, DateTime.UtcNow, imageName), JsonOptions));
        return resolved[track.AlbumKey] = found;
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
        return ExactAlbumMatch(document.RootElement, track.GroupArtist, track.Album);
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
