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
            check(before.SequenceEqual(f.Hashes()));
            check(ListeningProfile.Load(f.Device with { Internal = f.Device.Sd! }, [sd]).Scores.Count == 0);
        });
        test("Library scans preserve compound parent genres for Discovery groups", () =>
        {
            using var f = new Fixture();
            var path = f.PathFor(Storage.Sd, "M/genre.mp3");
            var frame = new byte[417]; frame[0] = 0xff; frame[1] = 0xfb; frame[2] = 0x90; frame[3] = 0x64;
            using (var output = File.Create(path)) for (int i = 0; i < 12; i++) output.Write(frame);
            foreach (var (genre, group) in new[] { ("Hip-Hop/Rap-Trap", "Hip-Hop/Rap"), ("R&B/Soul-Contemporary", null) })
            {
                using (var audio = TagLib.File.Create(path)) { audio.Tag.Genres = [genre]; audio.Save(); }
                var track = PlayerLibrary.Scan(f.Device).Single(t => t.Path.RelativePath == "M/genre.mp3");
                check(track.Genre == genre && DiscoveryUpdate.Group(track.Genre) == group);
            }
        });
        test("Discovery groups are mainstream pop and any rap", () =>
        {
            foreach (var pop in new[] { "Pop", "Pop-Dance", "Dance-pop", "dance-pop; pop", "pop; electropop", "Pop-Dance Pop" })
                check(DiscoveryUpdate.Group(pop) == "Pop");
            foreach (var rap in new[] { "Hip-Hop/Rap", "hip hop; pop rap", "Hip-Hop/Rap-Pop Rap", "hip hop; trap" })
                check(DiscoveryUpdate.Group(rap) == "Hip-Hop/Rap");
            foreach (var other in new[] { "Pop-Indie", "Pop-Rock", "pop; pop rock", "pop; alternative pop", "Pop-R&B", "Rock", "" })
                check(DiscoveryUpdate.Group(other) is null);
        });
        test("Only played, unliked, low-scoring Discovery songs are retired", () =>
        {
            Track D(string name) => new(new(Storage.Sd, DiscoveryUpdate.Folder + name + ".mp3"), name, "A", "Discovery", "Pop", TimeSpan.Zero);
            Track[] tracks = [D("skipped"), D("liked"), D("unplayed"), D("loved"), Song("library-skip")];
            var scores = ListeningProfile.Parse(
                $"0\t0\t0\t0\t3\t/<microSD1>/{DiscoveryUpdate.Folder}skipped.mp3\n" +
                $"0\t0\t0\t0\t3\t/<microSD1>/{DiscoveryUpdate.Folder}liked.mp3\n" +
                $"196608\t0\t3\t0\t0\t/<microSD1>/{DiscoveryUpdate.Folder}loved.mp3\n" +
                "0\t0\t0\t0\t3\t/<microSD1>/M/library-skip.mp3\n");
            var prune = DiscoveryUpdate.Prune(tracks, scores, new HashSet<SongPath> { tracks[1].Path }, 0);
            check(prune.Count == 1 && prune[0] == tracks[0].Path);
        });
        test("Favourite artists rank scored songs within a group", () =>
        {
            Track[] tracks = [Song("a", "Katy"), Song("b", "Katy"), Song("c", "Gaga"), Song("d", "Rapper", "Hip-Hop/Rap"),
                Song("e", "Various Artists"), Song("f", "Quiet")];
            var scores = ListeningProfile.Parse(string.Concat(new[] { ("a", 1), ("b", 2), ("c", 4), ("d", 9), ("e", 9), ("f", 0) }
                .Select(x => $"{x.Item2 * 65536}\t0\t1\t0\t0\t/<microSD1>/M/{x.Item1}.mp3\n")));
            check(DiscoveryUpdate.FavoriteArtists(tracks, scores, "Pop", 0).SequenceEqual(new[] { "Gaga", "Katy" }));
            check(DiscoveryUpdate.FavoriteArtists(tracks, scores, "Hip-Hop/Rap", 0).SequenceEqual(new[] { "Rapper" }));
        });
        test("Song search alternates artists and tags and skips owned, seen and alternate versions", () =>
        {
            var queries = new List<string>();
            using var http = new HttpClient(new DiscoveryHttp(request =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query); queries.Add(query);
                check(request.RequestUri.AbsolutePath == "/ws/2/recording/" && request.Headers.UserAgent.ToString().Contains("RockboxManager/"));
                return query.Contains("artist:") ? Response(request, Item("Owned", "Katy", votes: 99), Item("Song (Live)", "Katy", votes: 50),
                        Item("Seen", "Katy", votes: 40), Item("Wrong", "Other", votes: 30), Item("Roar", "Katy", votes: 1))
                    : Response(request, Item("Firework", "Katy", votes: 99), Item("Fresh", "New", votes: 2));
            }));
            var seen = new HashSet<string> { LibraryViews.Identity("Katy", "Seen") };
            var songs = new DiscoveryService(http).Songs("Pop", ["dance-pop", "pop"], ["Katy"], 2, [Song("Owned", "Katy")], seen)
                .GetAwaiter().GetResult();
            check(songs.Count == 2 && songs[0] == new MusicSuggestion("Pop", "Roar", "Katy") && songs[1] == new MusicSuggestion("Pop", "Fresh", "New"));
            check(queries.Count == 2 && queries[0].Contains("artist:\"Katy\"") && queries[1].Contains(@"tag:""dance\-pop"""));
            check(seen.Contains(LibraryViews.Identity("Katy", "Roar")) && seen.Contains(LibraryViews.Identity("New", "Fresh")));
        });
        test("Catalog failures skip a query and cancellation stops the search", () =>
        {
            int calls = 0;
            using var http = new HttpClient(new DiscoveryHttp(request => ++calls switch
            {
                1 => new(HttpStatusCode.ServiceUnavailable), 2 => Response(request, Item("Retried", "A")),
                3 => new(HttpStatusCode.Forbidden), _ => new(HttpStatusCode.OK) { Content = new StringContent("not json") }
            }));
            var service = new DiscoveryService(http);
            var songs = service.Songs("Pop", ["a", "b", "c"], [], 3, [], new HashSet<string>()).GetAwaiter().GetResult();
            check(songs.Count == 1 && songs[0].Title == "Retried" && calls == 4);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { service.Songs("Pop", ["a"], [], 1, [], new HashSet<string>(), canceled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Expected cancellation");
        });
        test("Discovery playlist accumulates new songs and retired songs leave every playlist except likes", () =>
        {
            using var f = new Fixture();
            var a = new SongPath(Storage.Sd, "M/a.mp3"); var b = new SongPath(Storage.Sd, "M/b.mp3");
            var added = new SongPath(Storage.Sd, DiscoveryUpdate.Folder + "x.mp3");
            var session = EditSession.Load(f.Device);
            DiscoveryUpdate.ApplyPlaylists(session, [a], [added, b]);
            var discovery = session.VisiblePlaylists.Single(p => p.Name == "Discovery");
            check(discovery.IsNew && discovery.Entries.SequenceEqual(new[] { added, b }));
            check(session.VisiblePlaylists.Single(p => p.Name == "Mix").Entries.SequenceEqual(new[] { b }) && session.Liked.Entries.SequenceEqual(new[] { a }));
            var sent = new SyncEngine().Execute(SyncBatch.Build(session, f.Backups), () => f.Device);
            check(sent.Success);
            var reloaded = EditSession.Load(f.Device);
            DiscoveryUpdate.ApplyPlaylists(reloaded, [b], [added, a]);
            check(reloaded.VisiblePlaylists.Single(p => p.Name == "Discovery").Entries.SequenceEqual(new[] { added, a }));
        });
        test("Update retires low scorers, installs new songs on SD and never repeats a suggestion", () =>
        {
            using var f = new Fixture();
            var old = DiscoveryUpdate.Folder + "Old - Song.mp3";
            f.Write(Storage.Sd, old, "old"); f.Write(Storage.Sd, Path.ChangeExtension(old, null) + CoverConverter.Suffix, "cover");
            f.Write(Storage.Internal, ".rockbox/home-scores.tsv", $"0\t0\t0\t0\t2\t/<microSD1>/{old}\n");
            var library = PlayerLibrary.Scan(f.Device);
            var runs = new List<ProcessSpec>();
            var runner = new FakeRunner(spec =>
            {
                runs.Add(spec);
                var args = spec.Arguments;
                if (spec.FileName.EndsWith("yt-dlp.exe"))
                {
                    if (args[^1].Contains("Missing")) return new(1, "", "no results");
                    File.WriteAllText(args[args.ToList().IndexOf("--output") + 1].Replace("%(ext)s", "webm"), "source");
                }
                else File.WriteAllText(args[^1], "mp3:" + args[args.ToList().IndexOf("-i") + 1].Length);
                return new(0, "", "");
            });
            using var http = new HttpClient(new DiscoveryHttp(request =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                return query.Contains("pop") ? Response(request, Item("Pop One", "P1", votes: 9), Item("Pop Two", "P2", votes: 8), Item("Pop Three", "P3"))
                    : Response(request, Item("Rap One", "R1", votes: 9), Item("Missing", "R2", votes: 8), Item("Rap Three", "R3"));
            }));
            var history = Path.Combine(f.Root, "history.json");
            var update = new DiscoveryUpdate(new DiscoveryService(http), new PortableToolPaths(Path.Combine(f.Root, "tools")), history, runner);
            var liked = EditSession.Load(f.Device).Liked.Entries.ToHashSet();
            var changes = update.RunAsync(f.Device, library, liked).GetAwaiter().GetResult();
            check(changes.Removed.Single().RelativePath == old && !File.Exists(f.PathFor(Storage.Sd, old)));
            check(!File.Exists(f.PathFor(Storage.Sd, Path.ChangeExtension(old, null) + CoverConverter.Suffix)));
            check(changes.Added.Count == 3 && changes.Added.All(p => p.Storage == Storage.Sd && File.Exists(f.PathFor(p.Storage, p.RelativePath))));
            check(changes.Added[0].RelativePath == DiscoveryUpdate.Folder + "P1 - Pop One.mp3");
            check(changes.Errors.Single().StartsWith("R2 — Missing: no results"));
            check(runs.Any(r => r.Arguments.Contains("genre=Hip-Hop/Rap")) && runs.Any(r => r.Arguments.Contains("ytsearch1:P1 - Pop One official audio")));
            var again = update.RunAsync(f.Device, PlayerLibrary.Scan(f.Device), liked).GetAwaiter().GetResult();
            // History holds only installed songs: a failed download is offered again, an installed one never is.
            check(again.Added.Select(p => p.RelativePath).SequenceEqual(new[] { "P3 - Pop Three.mp3", "R3 - Rap Three.mp3" }.Select(n => DiscoveryUpdate.Folder + n)));
            check(again.Errors.Count == 2 && runs.Count(r => r.Arguments.Contains("ytsearch1:R2 - Missing official audio")) == 2);
        });
        test("Discovery file names are safe and bounded", () =>
        {
            check(DiscoveryUpdate.FileName(new("Pop", "What? / Why: \"Now\"", "A*B")) == "A_B - What_ _ Why_ _Now_.mp3");
            check(DiscoveryUpdate.FileName(new("Pop", new string('x', 300), "A")).Length == 104);
        });
    }
}

sealed class FakeRunner(Func<ProcessSpec, ProcessResult> run) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellation = default) => Task.FromResult(run(spec));
}

sealed class DiscoveryHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
