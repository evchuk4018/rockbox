using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using RockboxManager.Core;

static class ArtworkLookupTests
{
    private static Track Song(string title = "Title", string file = "a.mp3") =>
        new(new(Storage.Sd, "M/" + file), title, "Artist", "Album", "", TimeSpan.FromSeconds(5));
    private static Track Video(Track track, string id)
    {
        typeof(Track).GetProperty("YouTubeVideoId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(track, id);
        return track;
    }
    private static RgbaImage Image(byte[] bytes) => new(bytes[0] == 1 ? 120 : 320, bytes[0] == 1 ? 90 : 180,
        new byte[(bytes[0] == 1 ? 120 * 90 : 320 * 180) * 4]);
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("YouTube links require an exact trusted video ID", () =>
        {
            check(PlayerLibrary.YouTubeId("https://www.youtube.com/watch?v=abcDEF_12-3&t=1") == "abcDEF_12-3");
            check(PlayerLibrary.YouTubeId("https://youtu.be/abcDEF_12-3") == "abcDEF_12-3");
            check(PlayerLibrary.YouTubeId("https://youtube.com.evil.test/watch?v=abcDEF_12-3") is null);
            check(PlayerLibrary.YouTubeId("https://www.youtube.com/watch?v=bad") is null);
        });
        test("Recording match preserves title version, duration, and ambiguity", () =>
        {
            var first = Guid.NewGuid().ToString(); var second = Guid.NewGuid().ToString();
            string Recording(string title, int length, string ids) => $$"""{"title":"{{title}}","length":{{length}},"artist-credit":[{"name":"Artist"}],"releases":[{{ids}}]}""";
            string Release(string id, string name) => "{\"release-group\":{\"id\":\"" + id + "\",\"title\":\"" + name + "\"}}";
            using var exact = JsonDocument.Parse("{\"recordings\":[" + Recording("Title", 8500, Release(first, "Album")) + "]}");
            check(ArtworkService.ExactRecordingMatch(exact.RootElement, Song()) == first);
            using var wrong = JsonDocument.Parse("{\"recordings\":[" + Recording("Title (Live)", 5000, Release(first, "Album")) + "]}");
            check(ArtworkService.ExactRecordingMatch(wrong.RootElement, Song()) is null);
            using var ambiguous = JsonDocument.Parse("{\"recordings\":[" + Recording("Title", 5000, Release(first, "One") + "," + Release(second, "Two")) + "]}");
            check(ArtworkService.ExactRecordingMatch(ambiguous.RootElement, Song()) is null);
        });
        test("Thumbnail fallback skips placeholder and uses independent video caches", () =>
        {
            using var f = new Fixture(); var calls = new List<string>();
            using var http = new HttpClient(new FakeHttp(request =>
            {
                var url = request.RequestUri!.ToString(); calls.Add(url);
                if (request.RequestUri.Host != "i.ytimg.com") return new(HttpStatusCode.NotFound);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([url.Contains("maxresdefault") ? (byte)1 : (byte)2]) };
            }));
            var tracks = new[] { Video(Song(), "abcDEF_12-3"), Video(Song(file: "b.mp3"), "xyzDEF_12-3") };
            var cache = Path.Combine(f.Root, "VideoCache");
            var result = new ArtworkService(cache, Image, http).Prepare(f.Device, tracks).GetAwaiter().GetResult();
            check(result.All(r => r.SourceKind == "YouTube" && r.Source.Contains("sddefault") && File.Exists(r.OriginalImagePath) && File.Exists(r.CacheMetadataPath)));
            check(result[0].OriginalImagePath != result[1].OriginalImagePath);
            var before = calls.Count; var again = new ArtworkService(cache, Image, http).Prepare(f.Device, tracks).GetAwaiter().GetResult();
            check(again.All(r => r.Status == "Ready") && calls.Count == before);
        });
        test("All tracks sharing a conflicting artwork target are rejected", () =>
        {
            using var f = new Fixture();
            using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "i.ytimg.com"
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) } : new(HttpStatusCode.NotFound)));
            var tracks = new[] { Video(Song(), "abcDEF_12-3"), Video(Song(file: "a.flac"), "xyzDEF_12-3") };
            var results = new ArtworkService(Path.Combine(f.Root, "conflicts"), Image, http).Prepare(f.Device, tracks).GetAwaiter().GetResult();
            check(results.All(r => r.Status == "PathConflict" && r.Change is null));
        });
        test("Library scan reads an exact WOAS video URL from an ID3-tagged MP3", () =>
        {
            using var f = new Fixture(); var path = f.Device.FilePath(Storage.Sd, "M/woas.mp3");
            var frame = new byte[417]; frame[0] = 0xff; frame[1] = 0xfb; frame[2] = 0x90; frame[3] = 0x64;
            using (var output = File.Create(path)) for (int i = 0; i < 12; i++) output.Write(frame);
            using (var file = TagLib.File.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
                TagLib.Id3v2.UrlLinkFrame.Get(id3, "WOAS", true).Text = ["https://www.youtube.com/watch?v=abcDEF_12-3"];
                file.Save();
            }
            var track = PlayerLibrary.Scan(f.Device).Single(t => t.Path.RelativePath == "M/woas.mp3");
            var id = typeof(Track).GetProperty("YouTubeVideoId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(track);
            check((string?)id == "abcDEF_12-3");
        });
        test("Legacy album misses do not suppress the exact-video fallback", () =>
        {
            using var f = new Fixture(); var track = Video(Song(), "abcDEF_12-3");
            var cache = Path.Combine(f.Root, "legacy"); Directory.CreateDirectory(cache);
            var key = FileSnapshot.Digest(Encoding.UTF8.GetBytes(track.AlbumKey));
            File.WriteAllText(Path.Combine(cache, key + ".json"), JsonSerializer.Serialize(new { Status = "Missing", Source = "", Message = "old miss", Checked = DateTime.UtcNow, Image = (string?)null }));
            var urls = new List<string>();
            using var http = new HttpClient(new FakeHttp(request =>
            {
                urls.Add(request.RequestUri!.ToString());
                return request.RequestUri.Host == "i.ytimg.com" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) } : new(HttpStatusCode.NotFound);
            }));
            var result = new ArtworkService(cache, Image, http).Prepare(f.Device, [track]).GetAwaiter().GetResult().Single();
            check(result.Status == "Ready" && result.SourceKind == "YouTube" && urls.Any(u => u.Contains("musicbrainz.org")) && urls.Any(u => u.Contains("i.ytimg.com")));
        });
        test("Retry upgrades an installed thumbnail to album art", () =>
        {
            using var f = new Fixture(); var track = Video(Song(), "abcDEF_12-3"); var id = Guid.NewGuid().ToString(); bool albumAvailable = false;
            using var http = new HttpClient(new FakeHttp(request =>
            {
                if (request.RequestUri!.Host == "i.ytimg.com") return new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) };
                if (albumAvailable && request.RequestUri.Host == "musicbrainz.org") return new(HttpStatusCode.OK) { Content = new StringContent("{\"release-groups\":[{\"id\":\"" + id + "\",\"title\":\"Album\",\"artist-credit\":[{\"name\":\"Artist\"}]}]}") };
                if (albumAvailable && request.RequestUri.Host == "coverartarchive.org") return new(HttpStatusCode.OK) { Content = new ByteArrayContent([3]) };
                return new(HttpStatusCode.NotFound);
            }));
            var service = new ArtworkService(Path.Combine(f.Root, "retry"), Image, http);
            var first = service.Prepare(f.Device, [track]).GetAwaiter().GetResult().Single();
            check(first.SourceKind == "YouTube" && first.Bitmap is not null);
            File.WriteAllBytes(f.Device.FilePath(first.Storage, first.RelativePath), first.Bitmap!);
            albumAvailable = true;
            var upgraded = service.Prepare(f.Device, [track], retry: true).GetAwaiter().GetResult().Single();
            check(upgraded.SourceKind == "Album" && upgraded.Source.Contains(id));
        });
        test("Retry keeps installed artwork when lookups fail", () =>
        {
            using var f = new Fixture(); var track = Video(Song(), "abcDEF_12-3"); var installed = CoverConverter.Placeholder();
            File.WriteAllBytes(f.Device.FilePath(Storage.Sd, "M/a.61x61.bmp"), installed);
            using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "i.ytimg.com"
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) } : throw new HttpRequestException("offline")));
            var result = new ArtworkService(Path.Combine(f.Root, "installed"), Image, http).Prepare(f.Device, [track], retry: true).GetAwaiter().GetResult().Single();
            check(result.Status == "Installed" && result.SourceKind == "Player" && result.Change is null && result.Bitmap!.SequenceEqual(installed));
        });
        test("A manual crop is bound to the original image hash", () =>
        {
            using var f = new Fixture(); var track = Video(Song(), "abcDEF_12-3");
            using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "i.ytimg.com"
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) } : new(HttpStatusCode.NotFound)));
            var cache = Path.Combine(f.Root, "crop"); var service = new ArtworkService(cache, Image, http);
            var first = service.Prepare(f.Device, [track]).GetAwaiter().GetResult().Single();
            var json = JsonNode.Parse(File.ReadAllText(first.CacheMetadataPath!))!.AsObject();
            json["Crop"] = JsonSerializer.SerializeToNode(new CropRectangle(1, 2, 100)); File.WriteAllText(first.CacheMetadataPath!, json.ToJsonString());
            var manual = service.Prepare(f.Device, [track]).GetAwaiter().GetResult().Single();
            check(manual.Crop == new CropRectangle(1, 2, 100));
            File.WriteAllBytes(first.OriginalImagePath!, [3]);
            var changed = service.Prepare(f.Device, [track]).GetAwaiter().GetResult().Single();
            check(changed.Crop is null && changed.OriginalImageHash == FileSnapshot.Digest([3]));
        });
        test("Recording artwork is cached per track, not shared album label", () =>
        {
            using var f = new Fixture(); var firstId = Guid.NewGuid().ToString(); var secondId = Guid.NewGuid().ToString();
            using var http = new HttpClient(new FakeHttp(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "coverartarchive.org") return new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) };
                if (uri.AbsolutePath.Contains("recording"))
                {
                    bool second = Uri.UnescapeDataString(uri.Query).Contains("Second"); var title = second ? "Second" : "Title"; var id = second ? secondId : firstId;
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"recordings\":[{\"title\":\"" + title + "\",\"length\":5000,\"artist-credit\":[{\"name\":\"Artist\"}],\"releases\":[{\"release-group\":{\"id\":\"" + id + "\",\"title\":\"Album\"}}]}]}") };
                }
                return new(HttpStatusCode.NotFound);
            }));
            var tracks = new[] { Song(), Song("Second", "b.mp3") };
            var result = new ArtworkService(Path.Combine(f.Root, "recordings"), Image, http).Prepare(f.Device, tracks).GetAwaiter().GetResult();
            check(result[0].Source.Contains(firstId) && result[1].Source.Contains(secondId) && result[0].OriginalImagePath != result[1].OriginalImagePath);
        });
        test("Transient album and recording errors remain visible when thumbnail succeeds", () =>
        {
            using var f = new Fixture(); var track = Video(Song(), "abcDEF_12-3");
            using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "i.ytimg.com"
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) } : throw new HttpRequestException("temporary outage")));
            var result = new ArtworkService(Path.Combine(f.Root, "warning"), Image, http).Prepare(f.Device, [track]).GetAwaiter().GetResult().Single();
            check(result.Status == "Ready" && result.SourceKind == "YouTube" && result.Message.Contains("temporary outage") && result.Message.Contains("retried"));
        });
        test("A recording lookup error remains visible when the album is missing and thumbnail succeeds", () =>
        {
            using var f = new Fixture(); var track = Video(Song(), "abcDEF_12-3");
            using var http = new HttpClient(new FakeHttp(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "i.ytimg.com") return new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) };
                if (uri.AbsolutePath.Contains("recording")) throw new HttpRequestException("recording outage");
                return new(HttpStatusCode.NotFound);
            }));
            var result = new ArtworkService(Path.Combine(f.Root, "recording-warning"), Image, http).Prepare(f.Device, [track]).GetAwaiter().GetResult().Single();
            check(result.Status == "Ready" && result.SourceKind == "YouTube" && result.Message.Contains("recording outage") && result.Message.Contains("retried"));
        });
        test("Central artwork never shares a video thumbnail with a different album track", () =>
        {
            using var f = new Fixture();
            using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "i.ytimg.com"
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) } : new(HttpStatusCode.NotFound)));
            var longName = new string('\u00e9', 118) + ".mp3";
            var first = Video(Song(file: longName), "abcDEF_12-3");
            PlaylistCodec.ValidatePath(first.Path.RockboxPath);
            bool SidecarTooLong(SongPath path)
            {
                try { PlaylistCodec.ValidatePath((path with { RelativePath = Path.ChangeExtension(path.RelativePath, null) + ".61x61.bmp" }).RockboxPath); return false; }
                catch (InvalidOperationException) { return true; }
            }
            check(SidecarTooLong(first.Path));
            var different = Video(Song(file: "b.mp3"), "xyzDEF_12-3");
            var service = new ArtworkService(Path.Combine(f.Root, "central-conflict"), Image, http);
            var result = service.Prepare(f.Device, [first, different]).GetAwaiter().GetResult();
            check(result[0].Status == "PathConflict" && result[0].Change is null && result[1].Status == "Ready");

            var sameVideo = Video(Song(file: "b.mp3"), "abcDEF_12-3");
            var allowed = service.Prepare(f.Device, [first, sameVideo]).GetAwaiter().GetResult();
            check(allowed[0].Status == "Ready" && allowed[0].RelativePath.StartsWith(".rockbox/albumart/"));

            var folder = f.Device.FilePath(Storage.Sd, "Solo"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, longName), "audio");
            var single = first with { Path = new(Storage.Sd, "Solo/" + longName) };
            PlaylistCodec.ValidatePath(single.Path.RockboxPath);
            check(SidecarTooLong(single.Path));
            var fallback = service.Prepare(f.Device, [single, different]).GetAwaiter().GetResult();
            check(fallback[0].Status == "Ready" && fallback[0].RelativePath == "Solo/cover.61x61.bmp");
        });
    }
}
