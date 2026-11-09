using System.Net;
using System.Text;
using System.Text.Json;
using RockboxManager.Core;

static class PodcastsTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("Podcast week uses the previous New York Monday-Sunday week across DST", () =>
        {
            var beforeSpring = PodcastWeek.PreviousCompleted(new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero));
            var afterSpring = PodcastWeek.PreviousCompleted(new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero));
            var afterFall = PodcastWeek.PreviousCompleted(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
            check(beforeSpring == new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8)));
            check(afterSpring == new PodcastWeek(new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 15)));
            check(afterFall == new PodcastWeek(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8)));
            check(beforeSpring.Contains(DateTimeOffset.Parse("2026-03-08T23:59:59-04:00")));
            check(!beforeSpring.Contains(DateTimeOffset.Parse("2026-03-09T00:00:00-04:00")));
        });

        test("Podcast catalog escapes exactly once and rejects forged paths and IDs", () =>
        {
            var week = new PodcastWeek(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11));
            var catalog = new PodcastCatalog(week,
                [new PodcastChannel("UC_test-1", "A\tChannel")],
                [new PodcastEpisode("video_1", "UC_test-1", "A\tChannel", "Line\nTitle %", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(12))]);
            var bytes = PodcastCatalogCodec.Write(catalog);
            check(Encoding.UTF8.GetString(bytes).Contains("C\tUC_test-1\tA%09Channel\n"));
            check(PodcastCatalogCodec.TryParse(bytes, out var parsed, out _) && parsed!.Episodes[0].Title == "Line\nTitle %");
            check(parsed!.Episodes[0].AudioPath == "/<microSD1>/Podcasts/RockboxManager/video_1.mp3");
            check(!PodcastId.IsValid("../escape") && !PodcastId.IsValid("video/1"));
            var bad = bytes.ToArray();
            var text = Encoding.UTF8.GetString(bad).Replace("video_1.mp3", "../video_1.mp3", StringComparison.Ordinal);
            check(!PodcastCatalogCodec.TryParse(Encoding.UTF8.GetBytes(text), out _, out _));
        });

        test("Podcast catalogs enforce the firmware bounds", () =>
        {
            var week = new PodcastWeek(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11));
            var tooMany = Enumerable.Range(0, PodcastLimits.MaxEpisodes + 1)
                .Select(i => new PodcastEpisode("video_" + i, "UC_feed", "Feed", "Episode", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1))).ToList();
            var rejected = false;
            try { PodcastCatalogCodec.Write(new PodcastCatalog(week, [new("UC_feed", "Feed")], tooMany)); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected && !PodcastId.IsValid(new string('a', PodcastLimits.IdMax)));
        });

        test("Podcast subscriptions persist normalized stable IDs and deduplicate", () =>
        {
            using var temp = new PodcastTemp();
            var store = new PodcastSubscriptionStore(temp.Root);
            store.Add(new PodcastSubscription("UC_abc-1", "First", "https://www.youtube.com/@first"));
            store.Add(new PodcastSubscription(" UC_abc-1 ", "Duplicate", "https://www.youtube.com/channel/UC_abc-1"));
            check(store.Subscriptions.Count == 1 && store.Subscriptions[0].ChannelId == "UC_abc-1");
            store.Remove("UC_abc-1");
            check(store.Subscriptions.Count == 0);
            store.Add(new PodcastSubscription("UC_saved", "Saved", "https://www.youtube.com/channel/UC_saved"));
            var reloaded = new PodcastSubscriptionStore(temp.Root);
            check(reloaded.Subscriptions.Single().ChannelId == "UC_saved");
        });

        test("yt-dlp enumerates every playlist entry, gets full metadata, and filters video types", () =>
        {
            using var temp = new PodcastTemp();
            var calls = new List<ProcessSpec>();
            var runner = new FakeProcessRunner(spec =>
            {
                calls.Add(spec);
                if (spec.Arguments.Contains("--flat-playlist"))
                    return JsonResult("{\"channel_id\":\"UC_feed\",\"channel\":\"Feed\",\"entries\":[" +
                        "{\"id\":\"newer\",\"title\":\"Newer\",\"upload_date\":\"20260315\"}," +
                        "{\"id\":\"shorts\",\"title\":\"Short\",\"upload_date\":\"20260308\",\"webpage_url\":\"https://www.youtube.com/shorts/shorts\"}," +
                        "{\"id\":\"wanted\",\"title\":\"Wanted\",\"upload_date\":\"20260308\"}," +
                        "{\"id\":\"live\",\"title\":\"Live\",\"upload_date\":\"20260307\"}]}" );
                var id = spec.Arguments.Last().Split("v=", StringSplitOptions.None).Last();
                return id switch
                {
                    "newer" => JsonResult("{\"id\":\"newer\",\"channel_id\":\"UC_feed\",\"channel\":\"Feed\",\"title\":\"Newer\",\"timestamp\":1773532800,\"duration\":100,\"live_status\":\"not_live\",\"webpage_url\":\"https://youtu.be/newer\"}"),
                    "wanted" => JsonResult("{\"id\":\"wanted\",\"channel_id\":\"UC_feed\",\"channel\":\"Feed\",\"title\":\"Wanted\",\"timestamp\":1772928000,\"duration\":120,\"live_status\":\"not_live\",\"webpage_url\":\"https://youtu.be/wanted\"}"),
                    "shorts" => JsonResult("{\"id\":\"shorts\",\"channel_id\":\"UC_feed\",\"channel\":\"Feed\",\"title\":\"Short\",\"timestamp\":1772928000,\"duration\":30,\"live_status\":\"not_live\",\"webpage_url\":\"https://www.youtube.com/shorts/shorts\"}"),
                    _ => JsonResult("{\"id\":\"live\",\"channel_id\":\"UC_feed\",\"channel\":\"Feed\",\"title\":\"Live\",\"timestamp\":1772841600,\"duration\":120,\"live_status\":\"was_live\",\"webpage_url\":\"https://youtu.be/live\"}")
                };
            });
            var source = new YtDlpPodcastSource(new PortableToolPaths(temp.Root), runner);
            var week = new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8));
            var result = source.EnumerateAsync(new PodcastSubscription("UC_feed", "Feed", "https://www.youtube.com/channel/UC_feed"), week).GetAwaiter().GetResult();
            check(result.Checked && result.Error is null && result.Episodes.Count == 1 && result.Episodes[0].Id == "wanted");
             check(calls.Count == 4 && calls.Any(c => c.Arguments.Contains("--flat-playlist")) && calls.Any(c => c.Arguments.Last().Contains("wanted")));
            check(calls.All(c => c.Arguments.Contains("--ignore-config") && c.Arguments.Contains("--no-cookies")));
            check(calls[0].Arguments.Last().EndsWith("/videos", StringComparison.Ordinal));
        });

        test("Portable podcast media arguments use explicit sibling tools and no shell", () =>
        {
            using var temp = new PodcastTemp();
            var tools = new PortableToolPaths(temp.Root);
            var metadata = PodcastToolArguments.Metadata(tools, "https://www.youtube.com/channel/UC_public");
            var audio = PodcastToolArguments.Audio(tools, "https://youtu.be/video_1", Path.Combine(temp.Root, "pending", "%(id)s.%(ext)s"));
            check(metadata.FileName.EndsWith("yt-dlp.exe") && audio.FileName.EndsWith("yt-dlp.exe"));
            check(metadata.Arguments.Contains("--ignore-config") && metadata.Arguments.Contains("--no-cookies") &&
                metadata.Arguments.Contains("--js-runtimes") && metadata.Arguments.Any(a => a.StartsWith("deno:")) &&
                metadata.Arguments.Contains("--ffmpeg-location"));
            check(audio.Arguments.Contains("--format") && audio.Arguments.Contains("bestaudio/best") &&
                audio.Arguments.Contains("--no-playlist") && audio.Arguments.Contains("--output"));
            check(!metadata.UseShellExecute && !audio.UseShellExecute);
        });

        test("Portable tool verification rejects tampered executable bytes", () =>
        {
            using var temp = new PodcastTemp();
            var files = new Dictionary<string, object>();
            foreach (var name in new[] { "yt-dlp.exe", "deno.exe", "ffmpeg.exe", "ffprobe.exe" })
            {
                File.WriteAllText(Path.Combine(temp.Root, name), name);
                files[name] = new { version = "test", sha256 = FileSnapshot.Digest(Encoding.UTF8.GetBytes(name)) };
            }
            File.WriteAllText(Path.Combine(temp.Root, "manifest.json"), JsonSerializer.Serialize(new { schema = 1, files }));
            var tools = new PortableToolPaths(temp.Root); tools.Require();
            File.AppendAllText(tools.Deno, "tampered");
            var rejected = false;
            try { tools.Require(); } catch (IOException) { rejected = true; }
            check(rejected);
        });

        test("An interrupted staged retry preserves the existing catalog before backup", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var week = PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow);
            var episode = new PodcastEpisode("video_2", "UC_feed", "Feed", "Episode", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            var old = PodcastCatalogCodec.Write(new(week, [new("UC_old", "Old")], []));
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.InternalCatalogPath)!);
            File.WriteAllBytes(fixture.InternalCatalogPath, old);
            var cache = fixture.WriteCache(episode, [1, 2, 3]);
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
            var calls = 0;
            var mounts = new FakePodcastMountProvider(d => ++calls >= 3 ? null : d);
            var result = new PodcastTransferService(fixture.DataRoot, mounts: mounts)
                .PublishAsync(fixture.Device, new(week, [new("UC_feed", "Feed")], [episode]), [prepared]).GetAwaiter().GetResult();
            check(!result.Success && File.ReadAllBytes(fixture.InternalCatalogPath).SequenceEqual(old));
        });

        test("A yt-dlp source failure is not treated as a valid empty drop", () =>
        {
            using var temp = new PodcastTemp();
            var source = new YtDlpPodcastSource(new PortableToolPaths(temp.Root), new FakeProcessRunner(_ => new ProcessResult(1, "", "network down")));
            var result = source.EnumerateAsync(new PodcastSubscription("UC_feed", "Feed", "https://www.youtube.com/channel/UC_feed"),
                PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
            check(!result.Checked && result.Episodes.Count == 0 && result.Error!.Contains("network down"));
        });

        test("Missing precise publication timestamps are source errors, not empty episodes", () =>
        {
            using var temp = new PodcastTemp();
            var runner = new FakeProcessRunner(spec => spec.Arguments.Contains("--flat-playlist")
                ? JsonResult("{\"channel_id\":\"UC_feed\",\"channel\":\"Feed\",\"entries\":[{\"id\":\"wanted\"}]}")
                : JsonResult("{\"id\":\"wanted\",\"channel_id\":\"UC_feed\",\"title\":\"Wanted\",\"duration\":120}"));
            var source = new YtDlpPodcastSource(new PortableToolPaths(temp.Root), runner);
            var result = source.EnumerateAsync(new PodcastSubscription("UC_feed", "Feed", "https://www.youtube.com/channel/UC_feed"),
                new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8))).GetAwaiter().GetResult();
            check(!result.Checked && result.Error!.Contains("timestamp", StringComparison.OrdinalIgnoreCase));
        });

        test("Malformed channel listings and in-week metadata cannot install an empty drop", () =>
        {
            using var temp = new PodcastTemp();
            foreach (var listing in new[] { "{}", "{\"channel_id\":\"UC_feed\",\"entries\":[{\"id\":\"wanted\"}]}" })
            {
                var source = new YtDlpPodcastSource(new PortableToolPaths(temp.Root), new FakeProcessRunner(spec =>
                    spec.Arguments.Contains("--flat-playlist") ? JsonResult(listing) :
                    JsonResult("{\"id\":\"wanted\",\"channel_id\":\"UC_feed\",\"timestamp\":1772928000}")));
                var result = source.EnumerateAsync(new("UC_feed", "Feed", "https://youtube.com/@feed"),
                    new(new(2026, 3, 2), new(2026, 3, 8))).GetAwaiter().GetResult();
                check(!result.Checked && result.Error is not null);
            }
        });

        test("Shorts with watch URLs and missing status are not fetched for full metadata", () =>
        {
            using var temp = new PodcastTemp(); var calls = new List<ProcessSpec>();
            var runner = new FakeProcessRunner(spec =>
            {
                calls.Add(spec);
                return spec.Arguments.Contains("--flat-playlist")
                    ? JsonResult("{\"channel_id\":\"UC_feed\",\"channel\":\"Feed\",\"entries\":[{\"id\":\"short\",\"webpage_url\":\"https://www.youtube.com/watch?v=short\",\"original_url\":\"https://www.youtube.com/shorts/short\"}]}")
                    : JsonResult("{\"id\":\"short\",\"channel_id\":\"UC_feed\",\"title\":\"Short\",\"timestamp\":1772928000,\"duration\":30}");
            });
            var result = new YtDlpPodcastSource(new PortableToolPaths(temp.Root), runner)
                .EnumerateAsync(new PodcastSubscription("UC_feed", "Feed", "https://www.youtube.com/channel/UC_feed"),
                    new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8))).GetAwaiter().GetResult();
            check(result.Checked && result.Episodes.Count == 0 && calls.Count == 1);
        });

        test("Podcast transfer streams staged media, commits catalog last, and retries a failed fixture", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var week = new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8));
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Episode", DateTimeOffset.Parse("2026-03-08T14:00:00Z"), TimeSpan.FromSeconds(10));
            var catalog = new PodcastCatalog(week, [new("UC_feed", "Feed")], [episode]);
            var cache = fixture.WriteCache(episode, Encoding.UTF8.GetBytes("new audio"));
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), new FileInfo(cache).Length);
            var failing = new PodcastTransferService(fixture.DataRoot, new ThrowOnPodcastReplace("video_1"));
            var first = failing.PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult();
             check(!first.Success && !File.Exists(fixture.InternalCatalogPath) && !File.Exists(fixture.AudioPath));
            check(File.Exists(fixture.JournalPath));
            var retry = new PodcastTransferService(fixture.DataRoot);
            var second = retry.PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult();
             if (!(second.Success && File.ReadAllText(fixture.AudioPath) == "new audio" && File.Exists(fixture.InternalCatalogPath)))
                throw new Exception(second.Message);
            check(File.Exists(Path.Combine(fixture.Device.Sd!.Root, "Podcasts/RockboxManager/database.ignore")) &&
                File.Exists(fixture.InternalCatalogPath) && !File.Exists(fixture.SdCatalogPath));
            check(JsonSerializer.Deserialize<List<PodcastInstallState>>(File.ReadAllText(Path.Combine(fixture.DataRoot, "installed.json")))!.Single().Revision == second.Revision);
        });

        test("A process interruption after catalog rename is detected and finalized on retry", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var week = new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8));
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Episode", DateTimeOffset.Parse("2026-03-08T14:00:00Z"), TimeSpan.FromSeconds(10));
            var catalog = new PodcastCatalog(week, [new("UC_feed", "Feed")], [episode]);
            var cache = fixture.WriteCache(episode, [1, 2, 3]);
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
            var crashed = false;
            try { new PodcastTransferService(fixture.DataRoot, new CrashAfterPodcastReplace("catalog.tsv"))
                .PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult(); }
            catch (SimulatedPodcastCrashException) { crashed = true; }
            check(crashed && File.Exists(fixture.InternalCatalogPath) && File.Exists(fixture.JournalPath));
            var journal = JsonSerializer.Deserialize<PodcastTransferJournal>(File.ReadAllText(fixture.JournalPath))!;
            check(!journal.Published);
            check(new PodcastTransferService(fixture.DataRoot).PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult().Success);
            check(!File.Exists(fixture.JournalPath));
        });

        test("A catalog commit failure restores replaced audio and the previous catalog", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var week = new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8));
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "New title", DateTimeOffset.Parse("2026-03-08T14:00:00Z"), TimeSpan.FromSeconds(10));
            var old = new PodcastCatalog(week, [new("UC_feed", "Feed")], [episode with { Title = "Old title" }]);
              Directory.CreateDirectory(Path.GetDirectoryName(fixture.InternalCatalogPath)!); File.WriteAllBytes(fixture.InternalCatalogPath, PodcastCatalogCodec.Write(old));
            File.WriteAllText(fixture.AudioPath, "old audio");
            episode = episode with { Id = "video_2" };
            var catalog = new PodcastCatalog(week, [new("UC_feed", "Feed")], [episode]);
            var cache = fixture.WriteCache(episode, Encoding.UTF8.GetBytes("new audio"));
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), new FileInfo(cache).Length);
            var first = new PodcastTransferService(fixture.DataRoot, new ThrowOnPodcastReplace("catalog.tsv"))
                .PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult();
            check(!first.Success && File.ReadAllText(fixture.AudioPath) == "old audio");
             check(PodcastCatalogCodec.TryParse(File.ReadAllBytes(fixture.InternalCatalogPath), out var retained, out _) && retained!.Episodes[0].Title == "Old title");
        });

        test("Obsolete podcast audio and resume records are removed from their managed volumes", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var week = new PodcastWeek(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8));
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Old episode", DateTimeOffset.Parse("2026-03-08T14:00:00Z"), TimeSpan.FromSeconds(10));
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.InternalCatalogPath)!);
            File.WriteAllBytes(fixture.InternalCatalogPath, PodcastCatalogCodec.Write(new PodcastCatalog(week, [new("UC_feed", "Feed")], [episode])));
            File.WriteAllText(fixture.AudioPath, "old audio");
            var resume = Path.Combine(fixture.Device.Internal.Root, ".rockbox/podcasts/resume/video_1.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(resume)!); File.WriteAllText(resume, "old resume");
            var empty = new PodcastCatalog(week, [new("UC_feed", "Feed")], []);
            check(new PodcastTransferService(fixture.DataRoot).PublishAsync(fixture.Device, empty, []).GetAwaiter().GetResult().Success);
            check(!File.Exists(fixture.AudioPath) && !File.Exists(resume) && File.Exists(fixture.InternalCatalogPath));
        });

        test("Podcast transfer rejects wrong identity and missing SD without touching the card", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Episode", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            var catalog = new PodcastCatalog(PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow), [new("UC_feed", "Feed")], [episode]);
            var cache = fixture.WriteCache(episode, [1, 2, 3]);
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
            File.WriteAllText(Path.Combine(fixture.DataRoot, "installed.json"), JsonSerializer.Serialize(new List<PodcastInstallState>
            {
                new(fixture.Device.Id, fixture.Device.Internal.Id, fixture.Device.Sd!.Id, catalog.Week.Monday, catalog.Week.Sunday, "old-revision", [])
            }));
            var service = new PodcastTransferService(fixture.DataRoot);
            check(!service.PublishAsync(fixture.Device with { Id = "other" }, catalog, [prepared]).GetAwaiter().GetResult().Success);
            check(!service.PublishAsync(fixture.Device with { Sd = null }, catalog, [prepared]).GetAwaiter().GetResult().Success);
            check(!File.Exists(fixture.InternalCatalogPath));
        });

        test("Mounted identity and free space are rechecked before transfer", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Episode", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            var catalog = new PodcastCatalog(PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow), [new("UC_feed", "Feed")], [episode]);
            var cache = fixture.WriteCache(episode, [1, 2, 3]);
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
            var wrong = new PodcastTransferService(fixture.DataRoot, null, new FakePodcastMountProvider(d => d with { Sd = d.Sd! with { Id = "different" } }));
            check(!wrong.PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult().Success);
            var full = new PodcastTransferService(fixture.DataRoot, null, new FakePodcastMountProvider(d => d with { Sd = d.Sd! with { FreeBytes = 0 } }));
            check(!full.PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult().Success);
            check(!File.Exists(fixture.InternalCatalogPath));
        });

        test("Installed state permits a second player identity without losing the first", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Episode", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            var catalog = new PodcastCatalog(PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow), [new("UC_feed", "Feed")], [episode]);
            var cache = fixture.WriteCache(episode, [1, 2, 3]);
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
            check(new PodcastTransferService(fixture.DataRoot).PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult().Success);
            var second = fixture.Device with { Id = "second-player", Internal = fixture.Device.Internal with { Id = "second-internal" }, Sd = fixture.Device.Sd! with { Id = "second-sd" } };
            check(new PodcastTransferService(fixture.DataRoot).PublishAsync(second, catalog, [prepared]).GetAwaiter().GetResult().Success);
            check(JsonSerializer.Deserialize<List<PodcastInstallState>>(File.ReadAllText(Path.Combine(fixture.DataRoot, "installed.json")))!.Count == 2);
        });

        test("Same-week reconnect makes no writes and keeps independent resume records", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Episode", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            var catalog = new PodcastCatalog(PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow), [new("UC_feed", "Feed")], [episode]);
            var cache = fixture.WriteCache(episode, [1, 2, 3]);
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
            check(new PodcastTransferService(fixture.DataRoot).PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult().Success);
            var resume = Path.Combine(fixture.Device.Internal.Root, ".rockbox/podcasts/resume/video_1.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(resume)!); File.WriteAllText(resume, "saved position");
            File.Delete(cache);
            var again = new PodcastTransferService(fixture.DataRoot, new ThrowOnPodcastReplace(""))
                .PublishAsync(fixture.Device, catalog, []).GetAwaiter().GetResult();
            check(again.Success && !File.Exists(fixture.JournalPath) && File.ReadAllText(resume) == "saved position");
        });

        test("A checked prepared week survives restart, subscription changes refresh it, and missed weeks use latest", () =>
        {
            using var temp = new PodcastTemp();
            var now = DateTimeOffset.Parse("2026-11-09T16:00:00Z");
            var source = new CountingPodcastSource();
            PodcastManager Manager() => new(temp.Root, source, new NoPodcastMedia(), now: () => now);
            var manager = Manager();
            manager.Subscriptions.Add(new("UC_feed", "Feed", "https://youtube.com/@feed"));
            check(manager.PrepareAsync().GetAwaiter().GetResult().Success && source.Calls == 1);
            manager = Manager();
            check(manager.PrepareAsync().GetAwaiter().GetResult().Success && source.Calls == 1);
            manager.Subscriptions.Add(new("UC_second", "Second", "https://youtube.com/@second"));
            check(manager.PrepareAsync().GetAwaiter().GetResult().Success && source.Calls == 3);
            now = now.AddDays(21);
            var latest = manager.PrepareAsync().GetAwaiter().GetResult();
            check(latest.Success && source.Calls == 5 && latest.Catalog!.Week == PodcastWeek.PreviousCompleted(now));
            manager.Remove("UC_second");
            check(manager.PrepareAsync().GetAwaiter().GetResult().Success && source.Calls == 6);
        });

        test("Podcast cancellation, low space, hash failures and forged cleanup preserve unrelated data", () =>
        {
            foreach (var mode in new[] { "cancel", "sd-space", "internal-space", "hash", "forged" })
            {
                using var fixture = new PodcastTransferFixture();
                var episode = new PodcastEpisode("video_2", "UC_feed", "Feed", "Episode", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
                var catalog = new PodcastCatalog(PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow), [new("UC_feed", "Feed")], [episode]);
                var old = PodcastCatalogCodec.Write(new(catalog.Week, [new("UC_old", "Old")], []));
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.InternalCatalogPath)!); File.WriteAllBytes(fixture.InternalCatalogPath, old);
                var protectedFiles = new[] { Path.Combine(fixture.Device.Internal.Root, "Playlists/Mix.m3u8"),
                    Path.Combine(fixture.Device.Internal.Root, ".rockbox/home-scores.tsv"),
                    Path.Combine(fixture.Device.Sd!.Root, "Music/Song.mp3"), Path.Combine(fixture.Device.Sd.Root, "Music/cover.bmp") };
                foreach (var path in protectedFiles) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "unrelated " + path); }
                var before = protectedFiles.ToDictionary(p => p, p => File.ReadAllBytes(p));
                var cache = fixture.WriteCache(episode, [1, 2, 3]);
                var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
                using var cancellation = new CancellationTokenSource();
                IPodcastTransferHooks? hook = mode == "cancel" ? new PodcastHook(path => cancellation.Cancel()) : null;
                if (mode == "hash") File.WriteAllBytes(cache, [3, 2, 1]);
                if (mode == "forged")
                {
                    new PodcastTransferService(fixture.DataRoot, new ThrowOnPodcastReplace("video_2"))
                        .PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult();
                    var journal = JsonSerializer.Deserialize<PodcastTransferJournal>(File.ReadAllText(fixture.JournalPath))!;
                    journal.CleanupPaths.Add(protectedFiles[2]);
                    File.WriteAllText(fixture.JournalPath, JsonSerializer.Serialize(journal));
                }
                var mounts = new FakePodcastMountProvider(d => mode switch {
                    "sd-space" => d with { Sd = d.Sd! with { FreeBytes = 0 } },
                    "internal-space" => d with { Internal = d.Internal with { FreeBytes = 0 } }, _ => d });
                var result = new PodcastTransferService(fixture.DataRoot, hook, mounts)
                    .PublishAsync(fixture.Device, catalog, [prepared], cancellation.Token).GetAwaiter().GetResult();
                check(!result.Success && File.ReadAllBytes(fixture.InternalCatalogPath).SequenceEqual(old));
                check(before.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            }
        });

        test("Post-publication cleanup failure is journaled and retried without reinstalling", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var week = PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow);
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Old", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.InternalCatalogPath)!);
            File.WriteAllBytes(fixture.InternalCatalogPath, PodcastCatalogCodec.Write(new(week, [new("UC_feed", "Feed")], [episode])));
            File.WriteAllText(fixture.AudioPath, "old audio");
            File.SetAttributes(fixture.AudioPath, FileAttributes.ReadOnly);
            try
            {
                var empty = new PodcastCatalog(week, [new("UC_feed", "Feed")], []);
                var sent = new PodcastTransferService(fixture.DataRoot).PublishAsync(fixture.Device, empty, []).GetAwaiter().GetResult();
                check(sent.Committed && File.Exists(fixture.JournalPath));
                File.SetAttributes(fixture.AudioPath, FileAttributes.Normal);
                check(new PodcastTransferService(fixture.DataRoot, new ThrowOnPodcastReplace(""))
                    .PublishAsync(fixture.Device, empty, []).GetAwaiter().GetResult().Success);
                check(!File.Exists(fixture.JournalPath) && !File.Exists(fixture.AudioPath));
            }
            finally { if (File.Exists(fixture.AudioPath)) File.SetAttributes(fixture.AudioPath, FileAttributes.Normal); }
        });

        test("An unowned audio filename collision is never overwritten or deleted", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var episode = new PodcastEpisode("video_1", "UC_feed", "Feed", "Episode", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            var catalog = new PodcastCatalog(PodcastWeek.PreviousCompleted(DateTimeOffset.UtcNow), [new("UC_feed", "Feed")], [episode]);
            File.WriteAllText(fixture.AudioPath, "unowned audio");
            var cache = fixture.WriteCache(episode, [1, 2, 3]);
            var prepared = new PodcastPreparedEpisode(episode, cache, FileSnapshot.Digest(File.ReadAllBytes(cache)), 3);
            var sent = new PodcastTransferService(fixture.DataRoot).PublishAsync(fixture.Device, catalog, [prepared]).GetAwaiter().GetResult();
            check(!sent.Success && !File.Exists(fixture.InternalCatalogPath) && File.ReadAllText(fixture.AudioPath) == "unowned audio");
        });

        test("Podcast cache pruning keeps only current, prepared, and pending IDs", () =>
        {
            using var temp = new PodcastTemp();
            var cache = new PodcastCache(temp.Root);
            cache.Write("UC_feed", "keep", [1]);
            cache.Write("UC_feed", "prepared", [2]);
            cache.Write("UC_feed", "remove", [3]);
            cache.Prune([new PodcastCacheKey("UC_feed", "keep")], [new PodcastCacheKey("UC_feed", "prepared")], []);
            check(File.Exists(cache.PathFor("UC_feed", "keep")) && File.Exists(cache.PathFor("UC_feed", "prepared")) &&
                !File.Exists(cache.PathFor("UC_feed", "remove")));
        });

        test("Managed podcast audio is excluded from the music scan", () =>
        {
            using var fixture = new PodcastTransferFixture();
            var visible = Path.Combine(fixture.Device.Sd!.Root, "Music/visible.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(visible)!); File.WriteAllText(visible, "visible");
            check(PlayerLibrary.Scan(fixture.Device).Select(t => t.Path.RelativePath).SequenceEqual(new[] { "Music/visible.mp3" }));
        });
    }

    private static ProcessResult JsonResult(string json) => new(0, json, "");

    private sealed class FakeProcessRunner(Func<ProcessSpec, ProcessResult> run) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellation = default) => Task.FromResult(run(spec));
    }

    private sealed class CountingPodcastSource : IPodcastVideoSource
    {
        public int Calls;
        public Task<PodcastEnumerationResult> EnumerateAsync(PodcastSubscription subscription, PodcastWeek week,
            CancellationToken cancellation = default, IProgress<PodcastProgress>? progress = null)
        {
            Calls++;
            return Task.FromResult(new PodcastEnumerationResult(true, new(subscription.ChannelId, subscription.Name), []));
        }
    }

    private sealed class NoPodcastMedia : IPodcastMediaPreparer
    {
        public Task<PodcastPreparedEpisode> PrepareAsync(PodcastEpisode episode, CancellationToken cancellation = default,
            IProgress<PodcastProgress>? progress = null) => throw new Exception("Empty weeks must not download audio.");
    }
}

sealed class PodcastTemp : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "RockboxManagerPodcastTests", Guid.NewGuid().ToString("N"));
    public PodcastTemp() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RockboxManagerPodcastTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup path");
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

sealed class PodcastTransferFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "RockboxManagerPodcastTransferTests", Guid.NewGuid().ToString("N"));
    public string DataRoot => Path.Combine(Root, "Data");
    public PlayerDevice Device { get; }
    public string InternalCatalogPath => Path.Combine(Device.Internal.Root, ".rockbox/podcasts/catalog.tsv");
    public string SdCatalogPath => Path.Combine(Device.Sd!.Root, ".rockbox/podcasts/catalog.tsv");
    public string AudioPath => Path.Combine(Device.Sd!.Root, "Podcasts/RockboxManager/video_1.mp3");
    public string JournalPath => Path.Combine(DataRoot, "transfer.json");
    public PodcastTransferFixture()
    {
        var internalRoot = Path.Combine(Root, "Internal");
        var sdRoot = Path.Combine(Root, "SD");
        Directory.CreateDirectory(internalRoot); Directory.CreateDirectory(sdRoot); Directory.CreateDirectory(DataRoot);
        Device = new("podcast-player", "Test Clip+", new(Storage.Internal, "internal", internalRoot, long.MaxValue), new(Storage.Sd, "sd", sdRoot, long.MaxValue));
        Directory.CreateDirectory(Path.GetDirectoryName(AudioPath)!);
    }
    public string WriteCache(PodcastEpisode episode, byte[] bytes)
    {
        var path = Path.Combine(DataRoot, "cache", episode.ChannelId, episode.Id + ".mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path;
    }
    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RockboxManagerPodcastTransferTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup path");
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

sealed class ThrowOnPodcastReplace(string contains) : IPodcastTransferHooks
{
    public void BeforeReplace(string path)
    {
        if (path.Contains(contains, StringComparison.OrdinalIgnoreCase)) throw new IOException("Injected podcast transfer failure");
    }
    public void AfterReplace(string path) { }
}

sealed class CrashAfterPodcastReplace(string contains) : IPodcastTransferHooks
{
    public void BeforeReplace(string path) { }
    public void AfterReplace(string path)
    {
        if (path.Contains(contains, StringComparison.OrdinalIgnoreCase)) throw new SimulatedPodcastCrashException();
    }
}

sealed class SimulatedPodcastCrashException : Exception;

sealed class FakePodcastMountProvider(Func<PlayerDevice, PlayerDevice?> recheck) : IPodcastMountProvider
{
    public PlayerDevice? Recheck(PlayerDevice expected) => recheck(expected);
}

sealed class PodcastHook(Action<string> before) : IPodcastTransferHooks
{
    public void BeforeReplace(string path) => before(path);
    public void AfterReplace(string path) { }
}
