using System.Text.Json;

namespace RockboxManager.Core;

public sealed class FileChange
{
    public Storage Storage { get; set; }
    public string RelativePath { get; set; } = "";
    public string? BeforeHash { get; set; }
    public byte[]? Content { get; set; }
    public int Phase { get; set; }
    public bool Done { get; set; }
    public SongPath? ArtworkTrack { get; set; }
    public string? DesiredHash => Content is null ? null : FileSnapshot.Digest(Content);
}

public sealed class SyncBatch
{
    public string DeviceId { get; set; } = "";
    public string InternalId { get; set; } = "";
    public string? SdId { get; set; }
    public bool RequiresSd { get; set; }
    public bool Started { get; set; }
    public bool Complete { get; set; }
    public string BackupFolder { get; set; } = "";
    public List<FileChange> Changes { get; set; } = [];
    public int CompletedCount => Changes.Count(c => c.Done);

    public static SyncBatch Build(EditSession session, string backupRoot)
    {
        var batch = new SyncBatch { DeviceId = session.Device.Id, InternalId = session.Device.Internal.Id,
            SdId = session.Device.Sd?.Id,
            BackupFolder = Path.Combine(Path.GetFullPath(backupRoot), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]) };
        var snapshots = session.Playlists.SelectMany(p => p.Copies.Values).ToList();
        var removals = new List<FileSnapshot>();
        foreach (var playlist in session.Playlists.Where(p => p.IsDirty))
        {
            if (playlist.HasConflict) throw new InvalidOperationException($"Resolve the copies of '{playlist.Name}' before sending.");
            if (!playlist.IsLiked) batch.RequiresSd = true;
            if (!playlist.Deleted)
            {
                foreach (var storage in playlist.IsLiked ? new[] { Storage.Internal } : [Storage.Internal, Storage.Sd])
                {
                    var before = snapshots.FirstOrDefault(s => s.Storage == storage &&
                        s.RelativePath.Equals(playlist.TargetPath, StringComparison.OrdinalIgnoreCase));
                    batch.Changes.Add(new() { Storage = storage, RelativePath = playlist.TargetPath,
                        BeforeHash = before?.Hash, Content = PlaylistCodec.Write(playlist.Entries, storage, playlist.TargetPath) });
                }
            }
            removals.AddRange(playlist.Copies.Values.Where(copy => playlist.Deleted ||
                !copy.RelativePath.Equals(playlist.TargetPath, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var old in removals)
        {
            if (batch.Changes.Any(c => c.Storage == old.Storage && c.RelativePath.Equals(old.RelativePath, StringComparison.OrdinalIgnoreCase))) continue;
            batch.Changes.Add(new() { Storage = old.Storage, RelativePath = old.RelativePath, BeforeHash = old.Hash, Phase = 2 });
        }
        batch.Changes = batch.Changes.OrderBy(c => c.Phase).ToList();
        batch.Changes.AddRange(session.ArtworkChanges);
        if (batch.Changes.Any(c => c.Storage == Storage.Sd || c.ArtworkTrack?.Storage == Storage.Sd)) batch.RequiresSd = true;
        return batch;
    }
}

public interface IPlayerFiles
{
    byte[]? Read(string path);
    void Replace(string path, byte[] content);
    void Delete(string path);
    long FreeBytes(string root);
}

public sealed class PlayerFiles : IPlayerFiles
{
    public byte[]? Read(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public void Replace(string path, byte[] content)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"The player file is read-only: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".rbmanager-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Delete(string path) => File.Delete(path);
    public long FreeBytes(string root) => new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
}

public sealed record SyncResult(bool Success, string Message, int Completed, int Total);

public sealed class SyncEngine(IPlayerFiles? files = null)
{
    private readonly IPlayerFiles files = files ?? new PlayerFiles();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SyncResult Execute(SyncBatch batch, Func<PlayerDevice?> discover, IProgress<string>? progress = null)
    {
        try
        {
            var device = VerifyDevice(batch, discover());
            // Validate the complete batch before changing a single device file.
            foreach (var change in batch.Changes)
            {
                var path = SafePath(device, change);
                var actual = Hash(files.Read(path));
                if (change.Done ? actual != change.DesiredHash : actual != change.BeforeHash &&
                    !(batch.Started && actual == change.DesiredHash))
                    throw new IOException($"'{change.RelativePath}' changed since it was loaded. Reload the player and resolve its current versions.");
            }
            foreach (var group in batch.Changes.Where(c => !c.Done && c.Content is not null).GroupBy(c => c.Storage))
            {
                // Conservative, small playlist-only requirement includes temporary replacement space.
                var needed = group.Sum(c => (long)c.Content!.Length);
                if (files.FreeBytes(device.Volume(group.Key)!.Root) < needed)
                    throw new IOException($"There is not enough free space on {group.Key} storage to send these files.");
            }
            Directory.CreateDirectory(batch.BackupFolder);
            if (!batch.Started)
            {
                for (var i = 0; i < batch.Changes.Count; i++)
                {
                    var change = batch.Changes[i];
                    var original = files.Read(SafePath(device, change));
                    if (Hash(original) != change.BeforeHash) throw new IOException("A player file changed while preparing its backup. Reload the player.");
                    if (original is not null) File.WriteAllBytes(Path.Combine(batch.BackupFolder, $"{i:D3}.original"), original);
                }
                batch.Started = true;
                Save(batch);
            }
            for (var i = 0; i < batch.Changes.Count; i++)
            {
                var change = batch.Changes[i];
                device = VerifyDevice(batch, discover());
                var path = SafePath(device, change);
                var actual = Hash(files.Read(path));
                if (change.Done)
                {
                    if (actual != change.DesiredHash) throw new IOException($"An already sent file changed: {change.RelativePath}. Reload the player.");
                    continue;
                }
                if (actual != change.DesiredHash)
                {
                    if (actual != change.BeforeHash) throw new IOException($"'{change.RelativePath}' changed while sending. Reload the player.");
                    progress?.Report($"Sending {i + 1}/{batch.Changes.Count} · {change.RelativePath}");
                    if (change.Content is null) files.Delete(path);
                    else files.Replace(path, change.Content);
                }
                if (Hash(files.Read(path)) != change.DesiredHash) throw new IOException($"Read-back verification failed for {change.RelativePath}.");
                change.Done = true;
                Save(batch);
            }
            batch.Complete = true;
            Save(batch);
            PruneCompletedBackups(batch);
            return new(true, "All changes verified on the player. Safely eject it through Windows before disconnecting.", batch.CompletedCount, batch.Changes.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return new(false, $"{batch.CompletedCount}/{batch.Changes.Count} files sent. {ex.Message} Your remaining changes are retained; reconnect and retry, or reload the player.", batch.CompletedCount, batch.Changes.Count);
        }
    }

    private static PlayerDevice VerifyDevice(SyncBatch batch, PlayerDevice? device)
    {
        if (device is null || device.Id != batch.DeviceId || device.Internal.Id != batch.InternalId)
            throw new IOException("The original player is disconnected or a different device is connected.");
        if (batch.RequiresSd && (device.Sd is null || device.Sd.Id != batch.SdId))
            throw new IOException("Reconnect the original SD card to keep both playlist copies synchronized.");
        return device;
    }

    public static string SafePath(PlayerDevice device, FileChange change)
    {
        var relative = change.RelativePath.Replace('\\', '/');
        var isPins = change.Storage == Storage.Internal && relative == EditSession.PinsPath;
        var isPlaylist = relative.StartsWith("Playlists/", StringComparison.OrdinalIgnoreCase) &&
            (relative.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));
        var suffix = relative.EndsWith(CoverConverter.LegacySuffix, StringComparison.OrdinalIgnoreCase)
            ? CoverConverter.LegacySuffix : CoverConverter.Suffix;
        var isArtwork = change.ArtworkTrack is { } source && change.Content is { } bitmap &&
            CoverConverter.IsCover(bitmap, suffix == CoverConverter.LegacySuffix ? 44 : CoverConverter.Size) &&
            relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
            (change.Storage == source.Storage &&
                (relative.Equals(Path.ChangeExtension(source.RelativePath, null) + suffix, StringComparison.OrdinalIgnoreCase) ||
                 suffix == CoverConverter.Suffix && relative.Equals(
                    (source.RelativePath.Contains('/') ? source.RelativePath[..(source.RelativePath.LastIndexOf('/') + 1)] : "") + "cover" + CoverConverter.Suffix,
                    StringComparison.OrdinalIgnoreCase) && IsSingleSongFolder(device, source)) &&
                !relative.StartsWith(".rockbox/", StringComparison.OrdinalIgnoreCase) ||
             change.Storage == Storage.Internal && relative.StartsWith(".rockbox/albumart/", StringComparison.Ordinal) && relative.Split('/').Length == 3);
        if ((!isPins && !isPlaylist && !isArtwork) || relative.Split('/').Any(p => p is ".." or "." or "" || p.Contains(':')))
            throw new InvalidOperationException("Refusing to write outside playlists, generated covers and the legacy Home pin file.");
        if (isArtwork)
        {
            var audio = change.ArtworkTrack!;
            PlaylistCodec.ValidatePath(audio.RockboxPath);
            if (!PlayerLibrary.IsAudio(audio.RelativePath) || audio.RelativePath.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or "" || p.Contains(':')) ||
                !File.Exists(device.FilePath(audio.Storage, audio.RelativePath))) throw new IOException("The source song is unavailable.");
        }
        PlaylistCodec.ValidatePath("/" + relative);
        var volume = device.Volume(change.Storage) ?? throw new IOException("Required storage is disconnected.");
        var root = Path.GetFullPath(volume.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid player path.");
        for (var current = Path.GetDirectoryName(full); current is not null && current.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Playlist folders cannot be junctions or symbolic links.");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Playlist files cannot be symbolic links.");
        return full;
    }

    public static bool IsSingleSongFolder(PlayerDevice device, SongPath source)
    {
        try
        {
            if (!PlayerLibrary.IsAudio(source.RelativePath) || source.RelativePath.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or "" || p.Contains(':')))
                return false;
            var path = device.FilePath(source.Storage, source.RelativePath);
            if (!File.Exists(path)) return false;
            var songs = Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                .Where(PlayerLibrary.IsAudio).Take(2).ToArray();
            return songs.Length == 1 && songs[0].Equals(path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { return false; }
    }

    private static string? Hash(byte[]? bytes) => bytes is null ? null : FileSnapshot.Digest(bytes);
    private static void Save(SyncBatch batch)
    {
        var manifest = Path.Combine(batch.BackupFolder, "operation.json");
        var temp = manifest + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, batch, JsonOptions);
            stream.Flush(true);
        }
        File.Move(temp, manifest, true);
    }
    private static void PruneCompletedBackups(SyncBatch batch)
    {
        // Only folders with our completed manifest are eligible; incomplete operations are retained.
        var root = Path.GetDirectoryName(batch.BackupFolder)!;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (folder == batch.BackupFolder) continue;
            try
            {
                var manifest = Path.Combine(folder, "operation.json");
                if (!File.Exists(manifest)) continue;
                var previous = JsonSerializer.Deserialize<SyncBatch>(File.ReadAllText(manifest));
                if (previous is { Complete: true } && previous.DeviceId == batch.DeviceId &&
                    Path.GetFullPath(previous.BackupFolder) == Path.GetFullPath(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
}
