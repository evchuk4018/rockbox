using System.Net;
using System.Text.Json;
using RockboxManager.Core;

static class DiscoveryTests
{
    private static Track Song(string title, string artist = "Artist", string genre = "Pop-Dance Pop", string album = "Owned", Storage storage = Storage.Sd) =>
        new(new(storage, "M/" + title + ".mp3"), title, artist, album, genre, TimeSpan.FromMinutes(3));
    private static object Item(string title, string artist, string? secondary = null, int votes = 1) => new Dictionary<string, object>
    {
        ["id"] = Guid.NewGuid().ToString(), ["title"] = title, ["primary-type"] = "Album",
        ["secondary-types"] = secondary is null ? Array.Empty<string>() : new[] { secondary },
        ["artist-credit"] = new[] { new { name = artist } }, ["tags"] = new[] { new { name = "pop", count = votes } }
    };
    private static HttpResponseMessage Response(HttpRequestMessage request, params object[] items) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> {
        [request.RequestUri!.AbsolutePath.Contains("release-group") ? "release-groups" : "recordings"] = items })) };

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("Score snapshots use the last complete valid row and decode paths once", () =>
        {
            var scores = ListeningProfile.Parse("\ufeff65536\t100\t1\t0\t0\t/M/Été%2509.mp3\n" +
                "131072\t200\t2\t0\t0\t/M/Été%2509.mp3\n" +
                "7\t0\t0\t0\t0\t/M/été%2509.mp3\n" +
                "9\t0\t0\t0\t0\t/M/Tab%09New%0ALine%0D.mp3\r\n" +
                "999\t0\t0\t0\t0\t/M/bad%ZZ.mp3\n" +
                "4294967296\t0\t0\t0\t0\t/M/overflow.mp3\n" +
                "-1\t0\t0\t0\t0\t/M/negative.mp3\n" +
                "2\t0\t0\t0\t0\trelative.mp3\n" +
                "999\t0\t0\t0\t0\t/M/Été%2509.mp3");
            check(scores.Count == 3 && scores["/M/Été%09.mp3"].Balance == 131072 && scores["/M/été%09.mp3"].Balance == 7);
            check(scores.ContainsKey("/M/Tab\tNew\nLine\r.mp3"));
            check(ListeningProfile.Parse("1\t0\t0\t0\t0\t/a").Count == 0);
        });
        test("Score decay halves at 30 days and preserves invalid or backward clocks", () =>
        {
            var score = new ListeningScore(4 * 65536, 100, 2, 0, 0);
            check(score.PointsAt(100 + 2592000) == 2 && score.PointsAt(100 + 2 * 2592000) == 1);
            check(score.PointsAt(0) == 4 && score.PointsAt(99) == 4 && (score with { Updated = 0 }).PointsAt(9999999) == 4);
        });
        test("Listening imports map exact internal and SD paths and leave the player unchanged", () =>
        {
            using var f = new Fixture();
            var sd = Song("a"); var internalSong = Song("a", storage: Storage.Internal);
            f.Write(Storage.Internal, ".rockbox/home-scores.tsv", "65536\t0\t1\t0\t0\t/<microSD1>/M/a.mp3\n" +
                "131072\t0\t1\t0\t0\t/M/a.mp3\n" + "5\t0\t0\t0\t0\t/<microSD1>/M/A.mp3\n");
            var before = f.Hashes(); var profile = ListeningProfile.Load(f.Device, [sd, internalSong]);
            check(profile.Scores.Count == 2 && profile.Scores[sd.Path] == 1 && profile.Scores[internalSong.Path] == 2);
            check(profile.Status.Contains("1 paths outside") && before.SequenceEqual(f.Hashes()));
            check(ListeningProfile.Load(f.Device with { Internal = f.Device.Sd! }, [sd]).Genres.Count == 0);
        });
        test("Genre preferences account for early skips and reduce sparse-genre bias", () =>
        {
            Track[] tracks = [Song("a"), Song("b"), Song("c", "Skipped"), Song("d", genre: "Rock-Indie Rock"), Song("new", genre: "Jazz")];
            var text = "262144\t0\t2\t0\t0\t/<microSD1>/M/a.mp3\n" +
                "262144\t0\t2\t0\t0\t/<microSD1>/M/b.mp3\n" +
                "0\t0\t0\t0\t8\t/<microSD1>/M/c.mp3\n" +
                "131072\t0\t1\t0\t0\t/<microSD1>/M/d.mp3\n";
            var profile = ListeningProfile.FromText(text, tracks, 1);
            check(profile.Genres.Count == 2 && profile.Genres[0].Genre == "Pop-Dance Pop");
            check(profile.Genres[0].SongCount == 3 && profile.Genres[0].Points == 8 && profile.Genres[0].FavoriteArtist == "Artist");
            check(profile.Genres[0].Strength == 1.6);
        });
        test("Library scans preserve compound parent genres for listening preferences", () =>
        {
            using var f = new Fixture();
            var path = f.PathFor(Storage.Sd, "M/genre.mp3");
            var frame = new byte[417]; frame[0] = 0xff; frame[1] = 0xfb; frame[2] = 0x90; frame[3] = 0x64;
            using (var output = File.Create(path)) for (int i = 0; i < 12; i++) output.Write(frame);
            foreach (var genre in new[] { "Hip-Hop/Rap-Trap", "R&B/Soul-Contemporary" })
            {
                using (var audio = TagLib.File.Create(path)) { audio.Tag.Genres = [genre]; audio.Save(); }
                var track = PlayerLibrary.Scan(f.Device).Single(t => t.Path.RelativePath == "M/genre.mp3");
                var profile = ListeningProfile.FromText("65536\t0\t1\t0\t0\t/<microSD1>/M/genre.mp3\n", [track], 0);
                check(track.Genre == genre && profile.Genres.Single().Genre == genre);
            }
        });
        test("Discovery prefers subgenres and makes no catalog requests without positive scores", () =>
        {
            check(DiscoveryService.GenreTerms("Rock-Indie Rock").SequenceEqual(new[] { "indie rock", "rock" }));
            check(DiscoveryService.GenreTerms("Hip-Hop/Rap-Trap").SequenceEqual(new[] { "trap", "hip hop" }));
            check(DiscoveryService.GenreTerms("Hip-Hop/Rap-Conscious")[0] == "conscious hip hop");
            check(DiscoveryService.GenreTerms("R&B/Soul-Contemporary")[0] == "contemporary r&b");
            check(DiscoveryService.GenreTerms("Pop-Dance")[0] == "dance-pop");
            check(DiscoveryService.GenreTerms("Pop-Indie")[0] == "indie pop");
            using var http = new HttpClient(new DiscoveryHttp(_ => throw new Exception("Unexpected network call")));
            var result = new DiscoveryService(http).Find([], []).GetAwaiter().GetResult();
            check(result.Suggestions.Count == 0);
        });
        test("Discovery returns a small varied set and excludes owned music and compilation albums", () =>
        {
            var requests = new List<string>();
            var ownedGroup = (Dictionary<string, object>)Item("Alternate edition title", "Artist", votes: 1000);
            var owned = Song("Favourite");
            typeof(Track).GetProperty("ArtworkReleaseGroupId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(owned, (string)ownedGroup["id"]);
            using var http = new HttpClient(new DiscoveryHttp(request =>
            {
                requests.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
                check(request.Headers.UserAgent.ToString().Contains("RockboxManager/"));
                var album = request.RequestUri.AbsolutePath.Contains("release-group");
                return album ? Response(request, ownedGroup, Item(" owned ", "ARTIST", votes: 999), Item("Compilation", "Other", "Compilation", 999),
                    Item("New album", "New artist", votes: 10), Item("Another album", "Another artist")) :
                    Response(request, Item(" favourite ", "ARTIST", votes: 999), Item("New song", "New artist", votes: 99), Item("Fresh song", "Fresh artist"));
            }));
            var result = new DiscoveryService(http).Find([new("Pop-Dance Pop", 8, 3, "Artist"), new("Rock-Indie Rock", 5, 2, "Artist"),
                new("Jazz", 1, 1, "Artist")], [owned]).GetAwaiter().GetResult();
            check(result.Suggestions.Count == 4 && requests.Count == 4 && requests.All(q => !q.Contains("Jazz")));
            check(result.Suggestions.All(s => s.Title.Trim().ToLowerInvariant() is not ("owned" or "favourite" or "compilation" or "alternate edition title")));
            check(result.Suggestions[1].Artist == "Fresh artist" && result.Suggestions.Select(s => s.Title).Distinct().Count() == 4);
            check(result.Suggestions.Count(s => s.Kind == "Album") == 2 && result.Suggestions.All(s => s.Url.StartsWith("https://musicbrainz.org/")));
        });
        test("Discovery falls back to a scored artist and rejects unrelated artist matches", () =>
        {
            var queries = new List<string>();
            using var http = new HttpClient(new DiscoveryHttp(request =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query); queries.Add(query);
                var title = request.RequestUri.AbsolutePath.Contains("release-group") ? "Album idea" : "Song idea";
                return query.Contains("artist:") ? Response(request, Item("Wrong", "Other", votes: 999), Item(title, "Favourite")) : Response(request);
            }));
            var result = new DiscoveryService(http).Find([new("Rock-Unknown subgenre", 5, 2, "Favourite")], []).GetAwaiter().GetResult();
            check(result.Suggestions.Count == 2 && result.Suggestions.All(s => s.Artist == "Favourite" && s.Reason.Contains("highest-scoring")));
            check(queries.Count == 6 && queries[0].Contains("unknown subgenre") && queries[1].Contains("tag:\"rock\""));
        });
        test("Catalog failures return an actionable status and cancellation stops work", () =>
        {
            using var http = new HttpClient(new DiscoveryHttp(_ => new(HttpStatusCode.Forbidden)));
            var service = new DiscoveryService(http);
            var result = service.Find([new("Pop", 1, 1, "")], []).GetAwaiter().GetResult();
            check(result.Suggestions.Count == 0 && result.Status.Contains("requests failed"));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { service.Find([new("Pop", 1, 1, "")], [], cancellation: canceled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Expected cancellation");
        });
        test("Temporary catalog failures retry once and malformed JSON stays recoverable", () =>
        {
            int calls = 0;
            using var http = new HttpClient(new DiscoveryHttp(request => ++calls == 1 ? new(HttpStatusCode.ServiceUnavailable) :
                calls == 2 ? Response(request, Item("A new album", "Fresh")) : new(HttpStatusCode.OK) { Content = new StringContent("not json") }));
            var result = new DiscoveryService(http).Find([new("Pop", 1, 1, "")], []).GetAwaiter().GetResult();
            check(result.Suggestions.Count == 1 && calls == 3 && result.Status.Contains("requests failed"));
        });
    }
}

sealed class DiscoveryHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
