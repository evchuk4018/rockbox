using System.Text;
using System.Text.Json;
using RockboxManager.Core;

var failures = 0;
var tests = 0;
void Test(string name, Action test)
{
    tests++;
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
SongPath Sd(string path) => new(Storage.Sd, "M/" + path);

Test("Unicode, duplicates, missing songs and mixed volumes round trip on both copies", () =>
{
    SongPath[] entries = [Sd("Gio 🦩/Taking what’s not Yours.mp3"), new(Storage.Internal, "Music/Été.flac"), Sd("missing.mp3"), Sd("missing.mp3")];
    foreach (var storage in new[] { Storage.Internal, Storage.Sd })
        Check(PlaylistCodec.Read(PlaylistCodec.Write(entries, storage, "Playlists/Mix.m3u8"), storage, "Playlists/Mix.m3u8").SequenceEqual(entries));
});
Test("Rockbox root, volume and Windows path resolution", () =>
{
    Check(PlaylistCodec.Resolve("../M/a.mp3", Storage.Sd, "Playlists/x.m3u8") == Sd("a.mp3"));
    Check(PlaylistCodec.Resolve("/M/a.mp3", Storage.Sd, "Playlists/x.m3u8") == Sd("a.mp3"));
    Check(PlaylistCodec.Resolve("/<microSD0>/Music/a.mp3", Storage.Sd, "Playlists/x.m3u8") == new SongPath(Storage.Internal, "Music/a.mp3"));
    Check(PlaylistCodec.Resolve(@"C:\M\a.mp3", Storage.Sd, "Playlists/x.m3u8") == Sd("a.mp3"));
    Check(PlaylistCodec.Resolve("/<microSD2>/a.mp3", Storage.Internal, "Playlists/x.m3u8").Storage == Storage.Unknown);
});
Test("Legacy encoding and UTF-8 BOM", () =>
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var bytes = Encoding.GetEncoding(1252).GetBytes("#EXTM3U\r\n../M/café.mp3\r\n");
    Check(PlaylistCodec.Read(bytes, Storage.Sd, "Playlists/x.m3u", "cp1252")[0] == Sd("café.mp3"));
    Check(PlaylistCodec.Read(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("../M/a.mp3\n")).ToArray(), Storage.Sd, "Playlists/x.m3u8")[0] == Sd("a.mp3"));
});
Test("Filename and UTF-8 byte limits", () =>
{
    foreach (var invalid in new[] { "CON", "A/B", "..", "tail.", " leading", "bad\nname", new string('é', 130) }) Throws(() => PlaylistCodec.ValidateName(invalid));
    PlaylistCodec.ValidateName("Hip-Hop／Rap 🦩");
    Throws(() => PlaylistCodec.ValidatePath(new string('é', 130)));
});
Test("Loading and drafts do not write files; equal mirrors collapse", () =>
{
    using var f = new Fixture(); var before = f.Hashes(); var s = EditSession.Load(f.Device);
    Check(s.Playlists.Count == 2 && s.Playlists.Single(p => !p.IsLiked).Copies.Count == 2 && !s.IsDirty);
    var mix = s.Playlists.Single(p => !p.IsLiked); s.Rename(mix, "New name"); s.Add(mix, [Sd("missing.mp3")]);
    Check(before.SequenceEqual(f.Hashes())); s.Discard(); Check(!s.IsDirty && mix.Name == "Mix");
});
Test("Conflicting mirror order requires an explicit version", () =>
{
    using var f = new Fixture(); f.Write(Storage.Sd, "Playlists/Mix.m3u8", "#EXTM3U\n../M/b.mp3\n../M/a.mp3\n");
    var s = EditSession.Load(f.Device); var p = s.Playlists.Single(p => !p.IsLiked);
    Check(p.HasConflict); Throws(() => s.Add(p, [Sd("c.mp3")])); s.ResolveConflict(p, true);
    Check(!p.HasConflict && p.Entries[0] == Sd("b.mp3") && p.IsDirty);
    Check(new SyncEngine().Execute(SyncBatch.Build(s, f.Backups), () => f.Device).Success);
    Check(!EditSession.Load(f.Device).Playlists.Any(p => p.HasConflict));
});
Test("Selected duplicate removal and multi-row reordering", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device);
    var p = s.Create("Duplicates", [Sd("a.mp3"), Sd("a.mp3"), Sd("b.mp3"), Sd("c.mp3")]);
    s.Remove(p, [1]); Check(p.Entries.SequenceEqual(new[] { Sd("a.mp3"), Sd("b.mp3"), Sd("c.mp3") }));
    s.Move(p, [1, 2], -1); Check(p.Entries.SequenceEqual(new[] { Sd("b.mp3"), Sd("c.mp3"), Sd("a.mp3") }));
    s.MoveTo(p, [0, 1], 3); Check(p.Entries.SequenceEqual(new[] { Sd("a.mp3"), Sd("b.mp3"), Sd("c.mp3") }));
});
Test("Likes are distinct, protected, and only change the internal file", () =>
{
    using var f = new Fixture(); f.Write(Storage.Sd, "Playlists/Liked Songs.m3u8", "#EXTM3U\n../M/b.mp3\n");
    var s = EditSession.Load(f.Device); Throws(() => s.Rename(s.Liked, "Other")); Throws(() => s.Delete(s.Liked));
    s.SetLiked([Sd("b.mp3")], true); var batch = SyncBatch.Build(s, f.Backups);
    Check(batch.Changes.All(c => c.Storage == Storage.Internal));
    Check(new SyncEngine().Execute(batch, () => f.Device with { Sd = null }).Success);
    Check(EditSession.Load(f.Device).Liked.Entries.Contains(Sd("b.mp3")));
});
Test("All playlists are available without pins and legacy pins remain untouched", () =>
{
    using var f = new Fixture(); var before = File.ReadAllBytes(f.PathFor(Storage.Internal, EditSession.PinsPath));
    var s = EditSession.Load(f.Device);
    for (var i = 0; i < 24; i++) s.Create("Mix " + i, [Sd("a.mp3")]);
    Check(new SyncEngine().Execute(SyncBatch.Build(s, f.Backups), () => f.Device).Success);
    Check(EditSession.Load(f.Device).VisiblePlaylists.Count() == 26);
    Check(before.SequenceEqual(File.ReadAllBytes(f.PathFor(Storage.Internal, EditSession.PinsPath))));
});
Test("Verified send mirrors playlists and leaves audio hashes unchanged", () =>
{
    using var f = new Fixture(); var audio = f.AudioHashes(); var s = EditSession.Load(f.Device);
    var p = s.Create("New 🦩", [Sd("b.mp3"), new(Storage.Internal, "Music/internal.mp3"), Sd("missing.mp3")]);
    var batch = SyncBatch.Build(s, f.Backups); var result = new SyncEngine().Execute(batch, () => f.Device);
    Check(result.Success, result.Message); var loaded = EditSession.Load(f.Device); var copy = loaded.Playlists.Single(p => p.Name == "New 🦩");
    Check(copy.Copies.Count == 2 && !copy.HasConflict && copy.Entries.SequenceEqual(p.Entries) && !loaded.IsDirty);
    Check(audio.SequenceEqual(f.AudioHashes())); Check(File.Exists(Path.Combine(batch.BackupFolder, "operation.json")));
});
Test("Rename writes both destinations before deleting originals", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device); s.Rename(s.Playlists.Single(p => !p.IsLiked), "Renamed");
    var files = new FaultFiles { OnDelete = _ => {
        Check(File.Exists(f.PathFor(Storage.Internal, "Playlists/Renamed.m3u8")));
        Check(File.Exists(f.PathFor(Storage.Sd, "Playlists/Renamed.m3u8")));
        Check(File.ReadAllText(f.PathFor(Storage.Internal, EditSession.PinsPath)).Contains("Mix")); } };
    Check(new SyncEngine(files).Execute(SyncBatch.Build(s, f.Backups), () => f.Device).Success);
    Check(!File.Exists(f.PathFor(Storage.Internal, "Playlists/Mix.m3u8")) && !File.Exists(f.PathFor(Storage.Sd, "Playlists/Mix.m3u8")));
});
Test("External changes block the entire batch before device writes", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device); s.Add(s.Playlists.Single(p => !p.IsLiked), [Sd("c.mp3")]);
    f.Write(Storage.Sd, "Playlists/Mix.m3u8", "external\n"); var before = f.Hashes();
    var result = new SyncEngine().Execute(SyncBatch.Build(s, f.Backups), () => f.Device);
    Check(!result.Success && result.Completed == 0); Check(before.SequenceEqual(f.Hashes()));
});
Test("Wrong player, changed SD card, and missing SD are rejected", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device); s.Create("New");
    foreach (var device in new[] { f.Device with { Id = "other" }, f.Device with { Sd = null }, f.Device with { Sd = f.Device.Sd! with { Id = "other-card" } } })
        Check(!new SyncEngine().Execute(SyncBatch.Build(s, f.Backups), () => device).Success);
});
Test("Insufficient space leaves every file intact", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device); s.Create("New"); var before = f.Hashes();
    var result = new SyncEngine(new FaultFiles { Space = 0 }).Execute(SyncBatch.Build(s, f.Backups), () => f.Device);
    Check(!result.Success && result.Completed == 0 && before.SequenceEqual(f.Hashes()));
});
Test("Interrupted send persists progress and retries only unfinished files", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device); s.Create("New", [Sd("a.mp3")]);
    var batch = SyncBatch.Build(s, f.Backups); var fault = new FaultFiles { FailAtWrite = 2 };
    var first = new SyncEngine(fault).Execute(batch, () => f.Device); Check(!first.Success && first.Completed == 1);
    var recovered = JsonSerializer.Deserialize<SyncBatch>(File.ReadAllText(Path.Combine(batch.BackupFolder, "operation.json")))!;
    var retryFiles = new FaultFiles(); Check(new SyncEngine(retryFiles).Execute(recovered, () => f.Device).Success);
    Check(retryFiles.Writes == 1 && recovered.Complete);
});
Test("Failure after replacement but before progress save can be retried", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device); s.Create("New");
    var batch = SyncBatch.Build(s, f.Backups); Check(!new SyncEngine(new FaultFiles { FailAfterWrite = true }).Execute(batch, () => f.Device).Success);
    Check(new SyncEngine().Execute(batch, () => f.Device).Success);
});
Test("Read-only target preserves its original and can be retried", () =>
{
    using var f = new Fixture(); var s = EditSession.Load(f.Device); s.Add(s.Playlists.Single(p => !p.IsLiked), [Sd("c.mp3")]);
    var path = f.PathFor(Storage.Sd, "Playlists/Mix.m3u8"); var original = File.ReadAllBytes(path); File.SetAttributes(path, FileAttributes.ReadOnly);
    var batch = SyncBatch.Build(s, f.Backups);
    try { Check(!new SyncEngine().Execute(batch, () => f.Device).Success); Check(File.ReadAllBytes(path).SequenceEqual(original)); }
    finally { File.SetAttributes(path, FileAttributes.Normal); }
    Check(new SyncEngine().Execute(batch, () => f.Device).Success);
});
Test("Write boundary rejects audio, firmware, traversal and pin file on SD", () =>
{
    using var f = new Fixture();
    foreach (var path in new[] { "M/a.mp3", ".rockbox/config.cfg", "Playlists/../audio.m3u8", "Playlists/../../outside.m3u8" })
        Throws(() => SyncEngine.SafePath(f.Device, new() { Storage = Storage.Internal, RelativePath = path }));
    Throws(() => SyncEngine.SafePath(f.Device, new() { Storage = Storage.Sd, RelativePath = EditSession.PinsPath }));
});
Test("Legacy conversion preserves retired pins and removes legacy copies", () =>
{
    using var f = new Fixture();
    File.Move(f.PathFor(Storage.Internal, "Playlists/Mix.m3u8"), f.PathFor(Storage.Internal, "Playlists/Mix.m3u"));
    File.Move(f.PathFor(Storage.Sd, "Playlists/Mix.m3u8"), f.PathFor(Storage.Sd, "Playlists/Mix.m3u"));
    f.Write(Storage.Internal, EditSession.PinsPath, "/Playlists/Mix.m3u\n");
    var s = EditSession.Load(f.Device); s.Add(s.Playlists.Single(p => !p.IsLiked), [Sd("c.mp3")]);
    Check(new SyncEngine().Execute(SyncBatch.Build(s, f.Backups), () => f.Device).Success);
    Check(File.ReadAllText(f.PathFor(Storage.Internal, EditSession.PinsPath)) == "/Playlists/Mix.m3u\n");
    Check(!File.Exists(f.PathFor(Storage.Internal, "Playlists/Mix.m3u")));
});
Test("Unreadable metadata falls back to filename without changing audio", () =>
{
    using var f = new Fixture(); var before = f.AudioHashes(); var tracks = PlayerLibrary.Scan(f.Device);
    Check(tracks.Count == 3 && tracks.All(t => t.Title.Length > 0)); Check(before.SequenceEqual(f.AudioHashes()));
});
ArtworkTests.Run(Test, condition => Check(condition));
ThumbnailCropTests.Run(Test, condition => Check(condition));
ArtworkLookupTests.Run(Test, condition => Check(condition));
DiscoveryTests.Run(Test, condition => Check(condition));
PodcastsTests.Run(Test, condition => Check(condition));
Console.WriteLine($"{tests - failures}/{tests} tests passed");
return failures == 0 ? 0 : 1;

sealed class FaultFiles : IPlayerFiles
{
    private readonly PlayerFiles real = new();
    public int FailAtWrite { get; init; }
    public bool FailAfterWrite { get; init; }
    public int Writes { get; private set; }
    public long Space { get; init; } = long.MaxValue;
    public Action<string>? OnDelete { get; init; }
    public byte[]? Read(string path) => real.Read(path);
    public void Replace(string path, byte[] bytes)
    {
        Writes++;
        if (Writes == FailAtWrite) throw new IOException("Injected unplug/write failure");
        real.Replace(path, bytes);
        if (FailAfterWrite) throw new IOException("Injected failure after replacement");
    }
    public void Delete(string path) { OnDelete?.Invoke(path); real.Delete(path); }
    public long FreeBytes(string root) => Space;
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "RockboxManagerTests", Guid.NewGuid().ToString("N"));
    public string Backups => Path.Combine(Root, "Backups");
    public PlayerDevice Device { get; }
    public Fixture()
    {
        var internalRoot = Path.Combine(Root, "Internal"); var sdRoot = Path.Combine(Root, "SD");
        Directory.CreateDirectory(internalRoot); Directory.CreateDirectory(sdRoot);
        Device = new("test-player", "Test Clip+", new(Storage.Internal, "internal-id", internalRoot, long.MaxValue), new(Storage.Sd, "sd-id", sdRoot, long.MaxValue));
        Write(Storage.Internal, "Playlists/Mix.m3u8", "#EXTM3U\n/<microSD1>/M/a.mp3\n/<microSD1>/M/b.mp3\n");
        Write(Storage.Sd, "Playlists/Mix.m3u8", "#EXTM3U\n../M/a.mp3\n../M/b.mp3\n");
        Write(Storage.Internal, "Playlists/Liked Songs.m3u8", "#EXTM3U\n/<microSD1>/M/a.mp3\n");
        Write(Storage.Internal, EditSession.PinsPath, "/Playlists/Mix.m3u8\n");
        Write(Storage.Sd, "M/a.mp3", "corrupt-audio-for-tag-fallback-test"); Write(Storage.Sd, "M/b.mp3", "audio-b");
        Write(Storage.Internal, "Music/internal.mp3", "audio-internal");
    }
    public string PathFor(Storage storage, string relative) => Device.FilePath(storage, relative);
    public void Write(Storage storage, string relative, string content)
    { var path = PathFor(storage, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content, new UTF8Encoding(false)); }
    public string[] Hashes() => Device.Volumes.SelectMany(v => Directory.EnumerateFiles(v.Root, "*", SearchOption.AllDirectories))
        .Order().Select(p => Path.GetRelativePath(Root, p) + ":" + FileSnapshot.Digest(File.ReadAllBytes(p))).ToArray();
    public string[] AudioHashes() => Device.Volumes.SelectMany(v => Directory.EnumerateFiles(v.Root, "*.mp3", SearchOption.AllDirectories))
        .Order().Select(p => FileSnapshot.Digest(File.ReadAllBytes(p))).ToArray();
    public void Dispose()
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RockboxManagerTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup path");
        Directory.Delete(Root, true);
    }
}
