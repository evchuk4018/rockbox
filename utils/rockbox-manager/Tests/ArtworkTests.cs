using System.Net;
using System.Text;
using System.Text.Json;
using RockboxManager.Core;

static class ArtworkTests
{
    private static RgbaImage Solid(byte value, byte alpha = 255, int width = 40, int height = 40) =>
        new(width, height, Enumerable.Range(0, width * height).SelectMany(_ => new byte[] { value, value, value, alpha }).ToArray());
    private static Track Song(string album = "Album", string artist = "Artist", string file = "a.mp3") =>
        new(new(Storage.Sd, "M/" + file), "Title", artist, album, "Pop-Dance", TimeSpan.FromSeconds(5));

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("Cover BMP dimensions, row order, palette, padding and deterministic default conversion", () =>
        {
            var white = CoverConverter.Convert(Solid(255)); var black = CoverConverter.Convert(Solid(0));
            check(CoverConverter.IsCover(white) && white.SequenceEqual(CoverConverter.Convert(Solid(255))));
            check(white[54] == 0 && white[58] == 255 && white.Length == 414);
            check(black.Skip(62).All(b => b == 0));
            check(white[62 + 2 * 8] == 0x3f && white[62 + 2 * 8 + 5] == 0xc0);
            check(Enumerable.Range(0, 44).All(y => white[62 + y * 8 + 6] == 0 && white[62 + y * 8 + 7] == 0));
            var pixels = new bool[44 * 44]; pixels[0] = true;
            check(CoverConverter.Bitmap(pixels)[62 + 43 * 8] == 128 && CoverConverter.Bitmap(pixels)[62] == 0);
        });
        test("Transparent art composites on black and centered crop excludes wide borders", () =>
        {
            check(CoverConverter.Convert(Solid(255, 0)).SequenceEqual(CoverConverter.Convert(Solid(0))));
            var wide = Solid(0, width: 80);
            for (int y = 0; y < 40; y++) for (int x = 20; x < 60; x++)
                for (int c = 0; c < 3; c++) wide.Pixels[(y * 80 + x) * 4 + c] = 255;
            check(CoverConverter.Convert(wide).SequenceEqual(CoverConverter.Convert(Solid(255))));
            var gradient = Solid(0);
            for (int i = 0; i < 1600; i++) for (int c = 0; c < 3; c++) gradient.Pixels[i * 4 + c] = (byte)(i % 40 * 255 / 39);
            var bmp = CoverConverter.Convert(gradient);
            check(bmp.Skip(62).Any(b => b is > 0 and < 255));
        });
        test("Album identity, guest artist search, genre boundaries and disc/track order", () =>
        {
            Track[] tracks = [Song() with { Artist = "Singer feat. Guest", AlbumArtist = "Band", DiscNumber = 2, TrackNumber = 1 },
                Song() with { AlbumArtist = "Band", DiscNumber = 1, TrackNumber = 9 },
                Song() with { AlbumArtist = "Other", DiscNumber = 1, TrackNumber = 1 }];
            var albums = LibraryViews.Albums(tracks); check(albums.Count == 2);
            check(albums.Single(a => a.Artist == "Band").Tracks[0].TrackNumber == 9);
            check(LibraryViews.Matches(tracks[0], "Guest Band", "Pop-Dance"));
            check(!LibraryViews.Matches(tracks[0], "", "Pop"));
            check(LibraryViews.Identity(" A   Band ", "Café") == LibraryViews.Identity("a band", "Cafe\u0301"));
        });
        test("Online matching requires one exact album and full artist credit, preserving versions", () =>
        {
            var id = Guid.NewGuid().ToString();
            string Group(string name, string artist, string uuid) => JsonSerializer.Serialize(new { id = uuid, title = name,
                artist_credit = new[] { new { name = artist } } }).Replace("artist_credit", "artist-credit");
            using var exact = JsonDocument.Parse("{\"release-groups\":[" + Group("Album", "Artist", id) + "]}");
            check(ArtworkService.ExactAlbumMatch(exact.RootElement, "artist", "ALBUM") == id);
            check(ArtworkService.ExactAlbumMatch(exact.RootElement, "Artist", "Album (Live)") is null);
            check(ArtworkService.ExactAlbumMatch(exact.RootElement, "Other", "Album") is null);
            using var ambiguous = JsonDocument.Parse("{\"release-groups\":[" + Group("Album", "Artist", id) + "," + Group("Album", "Artist", Guid.NewGuid().ToString()) + "]}");
            check(ArtworkService.ExactAlbumMatch(ambiguous.RootElement, "Artist", "Album") is null);
        });
        test("Mixed-album folders never reuse generic folder artwork; track images take priority", () =>
        {
            using var fixture = new Fixture(); var folder = Path.Combine(fixture.Device.Sd!.Root, "M");
            File.WriteAllBytes(Path.Combine(folder, "cover.png"), [2]);
            File.WriteAllBytes(Path.Combine(folder, "a.png"), [1]);
            using var http = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.NotFound)));
            var service = new ArtworkService(Path.Combine(fixture.Root, "Cache"), bytes => Solid(bytes[0] == 1 ? (byte)255 : (byte)0), http);
            var tracks = new[] { Song(), Song("Other Album", file: "b.mp3") };
            var report = service.Prepare(fixture.Device, tracks).GetAwaiter().GetResult();
            check(report[0].Status == "Ready" && report[0].Source.EndsWith("a.png"));
            check(report[1].Status == "Missing" && !fixture.Device.Volumes.SelectMany(v => Directory.EnumerateFiles(v.Root, "*.bmp", SearchOption.AllDirectories)).Any());
            var session = EditSession.Load(fixture.Device); session.ArtworkChanges.Add(report[0].Change!);
            check(new SyncEngine().Execute(SyncBatch.Build(session, fixture.Backups), () => fixture.Device).Success);
            var again = service.Prepare(fixture.Device, tracks).GetAwaiter().GetResult();
            check(again[0].Status == "Installed" && again[0].Change is null);
        });
        test("Local corrupt artwork falls through and online covers are cached without repeat requests", () =>
        {
            using var fixture = new Fixture();
            File.WriteAllBytes(Path.Combine(fixture.Device.Sd!.Root, "M/a.png"), [0]);
            var id = Guid.NewGuid().ToString();
            var handler = new FakeHttp(request => request.RequestUri!.Host == "musicbrainz.org"
                ? new(HttpStatusCode.OK) { Content = new StringContent("{\"release-groups\":[{\"id\":\"" + id + "\",\"title\":\"Album\",\"artist-credit\":[{\"name\":\"Artist\"}]}]}") }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent([255]) });
            using var http = new HttpClient(handler);
            RgbaImage Decode(byte[] bytes) => bytes[0] == 0 ? throw new NotSupportedException("Corrupt image") : Solid(255);
            var service = new ArtworkService(Path.Combine(fixture.Root, "Cache"), Decode, http);
            var result = service.Prepare(fixture.Device, [Song()]).GetAwaiter().GetResult();
            check(result[0].Status == "Ready" && handler.Calls == 2);
            var cached = new ArtworkService(Path.Combine(fixture.Root, "Cache"), Decode, http).Prepare(fixture.Device, [Song()]).GetAwaiter().GetResult();
            check(cached[0].Hash == result[0].Hash && handler.Calls == 2);
        });
        test("Artwork cancellation stops requests and HTTP errors remain retryable", () =>
        {
            using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var http = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.BadGateway)));
            var service = new ArtworkService(Path.Combine(fixture.Root, "Cache"), _ => Solid(0), http);
            bool cancelled = false;
            try { service.Prepare(fixture.Device, [Song()], cancellation: cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled);
            var result = service.Prepare(fixture.Device, [Song()]).GetAwaiter().GetResult();
            check(result[0].Status == "Failed" && result[0].Message.Contains("retried"));
        });
        test("Long artwork paths fall back centrally and sanitized album collisions are refused", () =>
        {
            var track = Song(file: new string('é', 119) + ".mp3");
            check(ArtworkService.Destination(track, [track]) is { Item1: Storage.Internal, Item2: ".rockbox/albumart/Artist-Album.44x44.bmp" });
            var a = track with { Album = "A/B" }; var b = Song("A?B");
            check(ArtworkService.Destination(a, [a, b]) is null);
            var huge = track with { Album = new string('é', 150) };
            check(ArtworkService.Destination(huge, [huge]) is null);
        });
        test("Artwork sends retain source identity, hash checks and resumable progress", () =>
        {
            using var fixture = new Fixture(); var session = EditSession.Load(fixture.Device); var audio = fixture.AudioHashes();
            var bitmap = CoverConverter.Convert(Solid(255));
            session.ArtworkChanges.Add(new() { Storage = Storage.Sd, RelativePath = "M/a.44x44.bmp", ArtworkTrack = Song().Path, Content = bitmap });
            session.ArtworkChanges.Add(new() { Storage = Storage.Sd, RelativePath = "M/b.44x44.bmp", ArtworkTrack = Song(file: "b.mp3").Path, Content = bitmap });
            var batch = SyncBatch.Build(session, fixture.Backups); check(batch.RequiresSd);
            var first = new SyncEngine(new FaultFiles { FailAtWrite = 2 }).Execute(batch, () => fixture.Device); check(!first.Success && first.Completed == 1);
            var saved = JsonSerializer.Deserialize<SyncBatch>(File.ReadAllText(Path.Combine(batch.BackupFolder, "operation.json")))!;
            check(new SyncEngine().Execute(saved, () => fixture.Device).Success && audio.SequenceEqual(fixture.AudioHashes()));
            session.Discard(); check(!session.IsDirty);
        });
        test("Artwork write boundary refuses forged formats, traversal and firmware targets", () =>
        {
            using var fixture = new Fixture();
            foreach (var relative in new[] { ".rockbox/config.cfg", ".rockbox/firmware.44x44.bmp", "M/../b.44x44.bmp", "M/other.44x44.bmp" })
            {
                bool rejected = false;
                try { SyncEngine.SafePath(fixture.Device, new() { Storage = Storage.Sd, RelativePath = relative, ArtworkTrack = Song().Path, Content = CoverConverter.Convert(Solid(0)) }); }
                catch (InvalidOperationException) { rejected = true; }
                check(rejected);
            }
            bool bad = false;
            try { SyncEngine.SafePath(fixture.Device, new() { Storage = Storage.Sd, RelativePath = "M/a.44x44.bmp", ArtworkTrack = Song().Path, Content = [1, 2, 3] }); }
            catch (InvalidOperationException) { bad = true; }
            check(bad);
        });
        test("An installed generated cover stays available offline without being converted again", () =>
        {
            using var fixture = new Fixture(); var bmp = CoverConverter.Convert(Solid(255));
            File.WriteAllBytes(Path.Combine(fixture.Device.Sd!.Root, "M/a.44x44.bmp"), bmp);
            var handler = new FakeHttp(_ => throw new Exception("Offline cover should not make a request"));
            using var http = new HttpClient(handler);
            var service = new ArtworkService(Path.Combine(fixture.Root, "Cache"), _ => throw new NotSupportedException(), http);
            var report = service.Prepare(fixture.Device, [Song()]).GetAwaiter().GetResult();
            check(report[0].Status == "Installed" && report[0].Bitmap!.SequenceEqual(bmp) && handler.Calls == 0);
        });
        test("Rate-limited requests retry and retain a successful exact match", () =>
        {
            using var fixture = new Fixture(); int calls = 0; var id = Guid.NewGuid().ToString();
            using var http = new HttpClient(new FakeHttp(request =>
            {
                if (++calls == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new(TimeSpan.Zero); return limited;
                }
                return request.RequestUri!.Host == "musicbrainz.org"
                    ? new(HttpStatusCode.OK) { Content = new StringContent("{\"release-groups\":[{\"id\":\"" + id + "\",\"title\":\"Album\",\"artist-credit\":[{\"name\":\"Artist\"}]}]}") }
                    : new(HttpStatusCode.OK) { Content = new ByteArrayContent([255]) };
            }));
            var service = new ArtworkService(Path.Combine(fixture.Root, "Cache"), _ => Solid(255), http);
            check(service.Prepare(fixture.Device, [Song()]).GetAwaiter().GetResult()[0].Status == "Ready" && calls == 3);
        });
        test("Verified release hints require matching present tags and duration", () =>
        {
            using var fixture = new Fixture(); var review = Path.Combine(fixture.Root, "review.json");
            File.WriteAllText(review, JsonSerializer.Serialize(new { sample = new { status = "verified", title = "Title",
                release_id = Guid.NewGuid().ToString(), duration_ms = 5000,
                tags = new { artist = new[] { "Artist" }, albumartist = new[] { "Artist" }, album = new[] { "Album" } } } }));
            using var http = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.NotFound)));
            var service = new ArtworkService(Path.Combine(fixture.Root, "Cache"), _ => Solid(0), http);
            check(service.ImportVerifiedReview(review, [Song() with { Album = "Wrong" }]) == 0);
            check(service.ImportVerifiedReview(review, [Song() with { Duration = TimeSpan.FromSeconds(10) }]) == 0);
            check(service.ImportVerifiedReview(review, [Song()]) == 1);
            check(File.Exists(Path.Combine(fixture.Root, "Cache/verified-release-hints.json")));
        });
    }
}

sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return Task.FromResult(respond(request)); }
}
