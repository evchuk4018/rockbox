using System.Text.Json;
using System.Windows;
using RockboxManager.Core;

namespace RockboxManager;

public partial class App : Application
{
    private Mutex? instance;
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RockboxManager");
    public static string BackupRoot => Path.Combine(DataRoot, "Backups");
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var fixtureIndex = Array.IndexOf(e.Args, "--fixture");
        var fixture = fixtureIndex >= 0 && fixtureIndex + 1 < e.Args.Length ? e.Args[fixtureIndex + 1] : null;
        var discovery = new DeviceDiscovery(fixture);
        var suggestIndex = Array.IndexOf(e.Args, "--suggest");
        if (suggestIndex >= 0 && suggestIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[suggestIndex + 1]);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var devices = await Task.Run(discovery.Find);
                if (devices.Count != 1) throw new InvalidOperationException("Suggestions require exactly one identified Clip+.");
                var tracks = await Task.Run(() => PlayerLibrary.Scan(devices[0]));
                var profile = ListeningProfile.Load(devices[0], tracks);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var result = await new DiscoveryService(http).Find(profile.Genres, tracks);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Created = DateTimeOffset.Now,
                    Tracks = tracks.Count, profile.Status, Genres = profile.Genres, result.Suggestions,
                    CatalogStatus = result.Status }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Error = ex.Message })); Shutdown(1); }
            return;
        }
        var prepareIndex = Array.IndexOf(e.Args, "--prepare-artwork");
        if (prepareIndex >= 0 && prepareIndex + 1 < e.Args.Length)
        {
            instance = new Mutex(true, "Local\\RockboxManager", out var available);
            var reportPath = Path.GetFullPath(e.Args[prepareIndex + 1]);
            try
            {
                if (!available) throw new InvalidOperationException("Close Rockbox Manager before preparing artwork.");
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var sourceIndex = Array.IndexOf(e.Args, "--source-library");
                if (sourceIndex < 0 || sourceIndex + 1 >= e.Args.Length)
                    throw new ArgumentException("Artwork preparation requires --source-library followed by a local music folder.");
                var sourceRoot = Path.GetFullPath(e.Args[sourceIndex + 1]);
                if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("The source music folder does not exist.");
                var sourceStorageIndex = Array.IndexOf(e.Args, "--source-storage");
                var sdSource = sourceStorageIndex >= 0 && sourceStorageIndex + 1 < e.Args.Length &&
                    e.Args[sourceStorageIndex + 1].Equals("sd", StringComparison.OrdinalIgnoreCase);
                var previewRoot = reportPath + ".previews";
                var internalRoot = sdSource ? Path.Combine(previewRoot, "internal-placeholder") : sourceRoot;
                if (sdSource) Directory.CreateDirectory(internalRoot);
                var device = new PlayerDevice("prepare-only", "Local artwork preparation",
                    new(Storage.Internal, "local-source", internalRoot, long.MaxValue),
                    sdSource ? new(Storage.Sd, "local-sd-source", sourceRoot, long.MaxValue) : null);
                var progress = new Progress<string>(text => File.WriteAllText(reportPath + ".progress.txt", text));
                var tracks = await Task.Run(() => PlayerLibrary.Scan(device, progress));
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var service = new ArtworkService(Path.Combine(DataRoot, "Artwork"), ArtworkDecoder.Decode, http);
                var reviewIndex = Array.IndexOf(e.Args, "--release-review");
                int hints = reviewIndex >= 0 && reviewIndex + 1 < e.Args.Length ? service.ImportVerifiedReview(e.Args[reviewIndex + 1], tracks) : 0;
                var prepared = await Task.Run(() => service.Prepare(device, tracks, progress,
                    retry: e.Args.Contains("--retry-artwork")));
                Directory.CreateDirectory(previewRoot);
                var previews = prepared.Where(p => p.Bitmap is not null).Select(p =>
                {
                    var path = Path.Combine(previewRoot, FileSnapshot.Digest(System.Text.Encoding.UTF8.GetBytes(p.Track.Key)) + ".bmp");
                    File.WriteAllBytes(path, p.Bitmap!);
                    return new { p.Track, Path = path };
                }).ToList();
                var report = new { Mode = "PrepareOnly", LibraryRoot = sourceRoot, SourceStorage = sdSource ? "SD" : "Internal",
                    Tracks = tracks.Count, VerifiedAlbumHints = hints, Created = DateTimeOffset.Now,
                    Counts = prepared.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
                    Artwork = prepared, Previews = previews };
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(prepared.All(p => p.Bitmap is not null) ? 0 : 1);
            }
            catch (Exception ex) { File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Error = ex.ToString() })); Shutdown(1); }
            return;
        }
        var backfillIndex = Array.IndexOf(e.Args, "--backfill-artwork");
        if (backfillIndex >= 0 && backfillIndex + 1 < e.Args.Length)
        {
            instance = new Mutex(true, "Local\\RockboxManager" + (fixture is null ? "" : "Fixture"), out var available);
            var reportPath = Path.GetFullPath(e.Args[backfillIndex + 1]);
            try
            {
                if (!available) throw new InvalidOperationException("Close Rockbox Manager before running artwork backfill.");
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var devices = await Task.Run(discovery.Find);
                if (devices.Count != 1) throw new InvalidOperationException("Artwork backfill requires exactly one identified Clip+.");
                var device = devices[0];
                if (Directory.Exists(BackupRoot) && Directory.EnumerateFiles(BackupRoot, "operation.json", SearchOption.AllDirectories)
                    .Select(p => JsonSerializer.Deserialize<SyncBatch>(File.ReadAllText(p)))
                    .Any(b => b is { Started: true, Complete: false } && b.DeviceId == device.Id))
                    throw new InvalidOperationException("Finish or reload the interrupted send in Manager before backfilling artwork.");
                var progress = new Progress<string>(text => File.WriteAllText(reportPath + ".progress.txt", text));
                var tracks = await Task.Run(() => PlayerLibrary.Scan(device, progress));
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var service = new ArtworkService(Path.Combine(DataRoot, "Artwork"), ArtworkDecoder.Decode, http);
                var reviewIndex = Array.IndexOf(e.Args, "--release-review");
                int hints = reviewIndex >= 0 && reviewIndex + 1 < e.Args.Length ? service.ImportVerifiedReview(e.Args[reviewIndex + 1], tracks) : 0;
                var prepared = await Task.Run(() => service.Prepare(device, tracks, progress,
                    retry: e.Args.Contains("--retry-artwork")));
                var session = EditSession.Load(device);
                session.ArtworkChanges.AddRange(prepared.Where(p => p.Change is not null).Select(p => p.Change!)
                    .DistinctBy(c => (c.Storage, c.RelativePath)));
                var batch = SyncBatch.Build(session, BackupRoot);
                void Save(SyncResult? sent)
                {
                    var recorded = prepared.Select(p => p.Status == "Ready" && batch.Changes.Any(c => c.Done &&
                        c.Storage == p.Storage && c.RelativePath == p.RelativePath && c.DesiredHash == p.Hash)
                        ? p with { Status = "Installed" } : p).ToList();
                    var report = new { device.Id, Internal = device.Internal.Root, SD = device.Sd?.Root,
                        Tracks = tracks.Count, VerifiedAlbumHints = hints, Created = DateTimeOffset.Now,
                        Counts = recorded.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
                        Send = sent, BackupFolder = batch.BackupFolder,
                        Artwork = recorded };
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                Save(null);
                var result = batch.Changes.Count == 0 ? new SyncResult(true, "No artwork changes needed.", 0, 0) :
                    await Task.Run(() => new SyncEngine().Execute(batch, () => discovery.Verify(device), progress));
                Save(result); Shutdown(result.Success ? 0 : 1);
            }
            catch (Exception ex) { File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Error = ex.ToString() })); Shutdown(1); }
            return;
        }
        var inspectIndex = Array.IndexOf(e.Args, "--inspect");
        if (inspectIndex >= 0 && inspectIndex + 1 < e.Args.Length)
        {
            try
            {
                var report = await Task.Run(() => discovery.Find().Select(device =>
                {
                    var session = EditSession.Load(device);
                    var tracks = PlayerLibrary.Scan(device);
                    var known = tracks.Select(t => t.Path).ToHashSet();
                    return new { device.Id, device.Name, Internal = device.Internal.Root, SD = device.Sd?.Root,
                        Tracks = tracks.Count, Playlists = session.VisiblePlaylists.Count(),
                        Mirrored = session.Playlists.Count(p => p.Copies.Count == 2), Conflicts = session.Playlists.Count(p => p.HasConflict),
                        MissingEntries = session.Playlists.Sum(p => p.Entries.Count(t => !known.Contains(t))),
                        DraftChanges = session.ChangedCount };
                }).ToArray());
                File.WriteAllText(e.Args[inspectIndex + 1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[inspectIndex + 1], ex.ToString()); Shutdown(1); }
            return;
        }
        instance = new Mutex(true, "Local\\RockboxManager" + (fixture is null ? "" : "Fixture"), out var created);
        if (!created) { MessageBox.Show("Rockbox Manager is already open.", "Rockbox Manager"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            Directory.CreateDirectory(DataRoot);
            File.AppendAllText(Path.Combine(DataRoot, "errors.log"), $"{DateTimeOffset.Now}: {args.Exception}\n");
            MessageBox.Show(args.Exception.Message, "Rockbox Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        new MainWindow(discovery).Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
