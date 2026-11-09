using System.Security.Cryptography;
using System.Text.Json;

namespace RockboxManager.Core;

public sealed class PodcastTransferService
{
    private readonly string root;
    private readonly string journalPath;
    private readonly IPodcastTransferHooks hooks;
    private readonly IPodcastMountProvider mounts;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PodcastTransferService(string root, IPodcastTransferHooks? hooks = null, IPodcastMountProvider? mounts = null)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        journalPath = Path.Combine(this.root, "transfer.json");
        this.hooks = hooks ?? new NoopPodcastTransferHooks();
        this.mounts = mounts ?? new FileSystemPodcastMountProvider();
    }

    public async Task<PodcastTransferResult> PublishAsync(PlayerDevice device, PodcastCatalog catalog,
        IReadOnlyList<PodcastPreparedEpisode> prepared, CancellationToken cancellation = default,
        IProgress<PodcastProgress>? progress = null)
    {
        using var transferLease = await TransferCoordinator.EnterAsync(cancellation);
        var total = prepared.Count;
        var revision = "";
        PodcastTransferJournal? activeJournal = null;
        try
        {
            revision = catalog.Revision;
            var expected = device;
            var mounted = mounts.Recheck(expected) ?? throw new IOException("The original player is not connected.");
            ValidateMounted(expected, mounted);
            device = mounted;
            var sd = device.Sd ?? throw new IOException("Connect the original SD card before sending podcasts.");
            ValidateVolumeRoot(device.Internal, "internal player storage");
            ValidateVolumeRoot(sd, "SD card");

            var states = LoadStates();
            var state = states.FirstOrDefault(s => s.DeviceId == device.Id);
            if (state is not null && (state.InternalId != device.Internal.Id || state.SdId != sd.Id))
                throw new IOException("The original player or SD card is not connected.");
            if (states.Any(s => s.DeviceId != device.Id &&
                (s.InternalId == device.Internal.Id || s.SdId == sd.Id)))
                throw new IOException("The original player identity does not match the installed podcast state.");

            var journal = LoadJournal();
            if (journal is not null)
            {
                if (journal.DeviceId != device.Id || journal.InternalId != device.Internal.Id || journal.SdId != sd.Id)
                    throw new IOException("A podcast transfer is pending for a different player or SD card.");
                if (journal.Revision != revision) throw new IOException("A previous podcast transfer is pending. Retry it before preparing a new drop.");
                ValidateJournalPaths(journal, device, catalog);
                activeJournal = journal;

                var catalogPath = CatalogPath(device.Internal);
                if (!journal.Published && CatalogMatches(catalogPath, journal.CatalogBase64))
                {
                    journal.Published = true;
                    SaveJournal(journal);
                }
                if (journal.Published) return await FinishPublishedAsync(device, catalog, journal, cancellation, progress);
            }

            var catalogBytes = PodcastCatalogCodec.Write(catalog);
            if (journal is null && state is not null && state.Monday == catalog.Week.Monday && state.Sunday == catalog.Week.Sunday &&
                state.Revision == revision && CatalogMatches(CatalogPath(device.Internal), Convert.ToBase64String(catalogBytes)))
                return new(true, true, "This podcast drop is already installed; saved positions are unchanged.", total, total, revision);
            var oldCatalog = ReadOptional(CatalogPath(device.Internal));
            var oldCatalogModel = oldCatalog is not null && PodcastCatalogCodec.TryParse(oldCatalog, out var parsed, out _) ? parsed : null;
            var oldIds = state?.InstalledEpisodeIds ?? oldCatalogModel?.Episodes.Select(e => e.Id).Distinct(StringComparer.Ordinal).ToArray() ?? [];
            var desiredIds = catalog.Episodes.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var cleanup = oldIds.Where(id => !desiredIds.Contains(id)).Distinct(StringComparer.Ordinal)
                .SelectMany(id => new[] { AudioPath(sd, id), ResumePath(device.Internal, id) }).ToList();
            var audioBytes = checked(prepared.Sum(p => p.Length));
            RequireSpace(device.Internal, checked(catalogBytes.LongLength + 1), "internal player storage");
            RequireSpace(sd, checked(audioBytes + 1), "the SD card");

            var createdJournal = journal is null;
            if (createdJournal) ValidatePrepared(catalog, prepared);
            journal ??= CreateJournal(device, catalog, prepared, catalogBytes, cleanup);
            activeJournal = journal;
            if (!createdJournal && journal.CatalogBase64 != Convert.ToBase64String(catalogBytes))
                throw new InvalidOperationException("The pending podcast journal catalog does not match the retry.");
            if (journal.Audio.Count == 0 && catalog.Episodes.Count > 0)
                throw new InvalidOperationException("Every podcast catalog episode must be prepared before catalog publication.");
            var journalIds = journal.Audio.Select(a => a.VideoId).ToHashSet(StringComparer.Ordinal);
            var catalogIds = catalog.Episodes.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            if (journal.Audio.Count != catalogIds.Count || !journalIds.SetEquals(catalogIds))
                throw new InvalidOperationException("The pending podcast journal does not match the requested catalog.");
            if (!createdJournal && journal.Audio.Any(a => !a.Staged)) ValidatePendingPrepared(catalog, prepared, journal);
            if (!journal.Published)
            {
                foreach (var audio in journal.Audio.Where(a => a.Staged))
                    VerifyFile(audio.StagePath, audio.Hash, audio.Length, "A staged podcast audio file changed: ");
            }

            var beforeStaging = mounts.Recheck(expected) ?? throw new IOException("The original player was disconnected before podcast staging.");
            ValidateMounted(expected, beforeStaging);
            device = beforeStaging;
            sd = device.Sd ?? throw new IOException("Connect the original SD card before podcast staging.");
            ValidateVolumeRoot(device.Internal, "internal player storage");
            ValidateVolumeRoot(sd, "SD card");
            RequireSpace(device.Internal, checked(catalogBytes.LongLength + 1), "internal player storage");
            RequireSpace(sd, checked(audioBytes + 1), "the SD card");

            SaveJournal(journal);
            for (var i = 0; i < journal.Audio.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var audio = journal.Audio[i];
                if (!audio.Staged)
                {
                    PodcastMediaPreparer.CopyFlushHash(audio.CachePath, audio.StagePath, out var hash, out var length, cancellation);
                    if (!hash.Equals(audio.Hash, StringComparison.OrdinalIgnoreCase) || length != audio.Length)
                        throw new IOException($"Prepared podcast audio changed: {audio.VideoId}.");
                    audio.Staged = true;
                    SaveJournal(journal);
                }
                progress?.Report(new($"Staged podcast audio… {i + 1}/{journal.Audio.Count}", i, journal.Audio.Count));
            }

            var beforePublication = mounts.Recheck(expected) ?? throw new IOException("The original player was disconnected before podcast publication.");
            ValidateMounted(expected, beforePublication);
            device = beforePublication;
            sd = device.Sd ?? throw new IOException("Connect the original SD card before podcast publication.");
            ValidateVolumeRoot(device.Internal, "internal player storage");
            ValidateVolumeRoot(sd, "SD card");
            RequireSpace(device.Internal, checked(catalogBytes.LongLength + 1), "internal player storage");
            RequireSpace(sd, checked(audioBytes + 1), "the SD card");

            var catalogPathForDevice = CatalogPath(device.Internal);
            var ignorePath = IgnorePath(sd);
            BackupIfNeeded(journal, catalogPathForDevice, journal.CatalogBackupPath);
            BackupIfNeeded(journal, ignorePath, journal.IgnoreBackupPath);
            foreach (var audio in journal.Audio)
            {
                ValidateMounted(expected, mounts.Recheck(expected) ?? throw new IOException("The player was disconnected during transfer."));
                if (audio.Replaced)
                {
                    if (File.Exists(audio.DestinationPath) && HashFile(audio.DestinationPath).Equals(audio.Hash, StringComparison.OrdinalIgnoreCase))
                        continue;
                    audio.Replaced = false;
                    SaveJournal(journal);
                }
                if (File.Exists(audio.DestinationPath) && HashFile(audio.DestinationPath).Equals(audio.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(audio.DestinationPath))
                    throw new IOException("A podcast filename already contains different audio. The existing file was retained: " + audio.VideoId);
                BackupIfNeeded(journal, audio.DestinationPath, audio.BackupPath);
                audio.Replaced = true;
                SaveJournal(journal);
                ReplaceFromFile(audio.StagePath, audio.DestinationPath, audio.Hash, cancellation);
            }
            EnsureEmptyFile(ignorePath);
            cancellation.ThrowIfCancellationRequested();
            ValidateMounted(expected, mounts.Recheck(expected) ?? throw new IOException("The player was disconnected before catalog publication."));
            journal.CatalogWriteStarted = true;
            SaveJournal(journal);
            ReplaceBytes(catalogPathForDevice, catalogBytes, "catalog");
            journal.Published = true;
            SaveJournal(journal);
            return await FinishPublishedAsync(device, catalog, journal, cancellation, progress);
        }
        catch (OperationCanceledException)
        {
            if (activeJournal is { Published: false } && CatalogMatches(CatalogPathFromJournal(activeJournal), activeJournal.CatalogBase64))
                activeJournal.Published = true;
            if (activeJournal is { Published: false }) Restore(activeJournal);
            return new(false, false, "Podcast transfer canceled. The previous catalog remains installed; retry to resume.", 0, total, revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or OverflowException)
        {
            if (activeJournal is { Published: false } && CatalogMatches(CatalogPathFromJournal(activeJournal), activeJournal.CatalogBase64))
                activeJournal.Published = true;
            if (activeJournal is { Published: false }) Restore(activeJournal);
            return new(false, false, $"Podcast transfer failed: {ex.Message} The previous catalog remains installed; retry to resume.", 0, total, revision);
        }
    }

    private Task<PodcastTransferResult> FinishPublishedAsync(PlayerDevice device, PodcastCatalog catalog,
        PodcastTransferJournal journal, CancellationToken cancellation, IProgress<PodcastProgress>? progress)
    {
        var completed = 0;
        var sd = device.Sd ?? throw new IOException("Connect the original SD card before cleaning up podcasts.");
        try
        {
            SaveState(new PodcastInstallState(device.Id, device.Internal.Id, sd.Id, catalog.Week.Monday, catalog.Week.Sunday,
                journal.Revision, catalog.Episodes.Select(e => e.Id).Distinct(StringComparer.Ordinal).ToArray()));
            foreach (var path in journal.CleanupPaths)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IsOwnedCleanupPath(device.Internal.Root, sd.Root, path)) throw new InvalidOperationException("Refusing to delete an unmanaged podcast path.");
                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Refusing to delete a linked podcast path.");
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("An old podcast file is read-only: " + Path.GetFileName(path));
                    File.Delete(path);
                }
                completed++;
                progress?.Report(new($"Cleaning old podcast files… {completed}/{journal.CleanupPaths.Count}", completed, journal.CleanupPaths.Count));
            }
            foreach (var audio in journal.Audio)
            {
                if (File.Exists(audio.StagePath)) File.Delete(audio.StagePath);
                if (File.Exists(audio.BackupPath)) File.Delete(audio.BackupPath);
            }
            if (File.Exists(journal.CatalogBackupPath)) File.Delete(journal.CatalogBackupPath);
            if (File.Exists(journal.IgnoreBackupPath)) File.Delete(journal.IgnoreBackupPath);
            DeleteOwnedDirectory(journal.StageRoot, root);
            DeleteOwnedDirectory(journal.BackupRoot, root);
            if (File.Exists(journalPath)) File.Delete(journalPath);
            return Task.FromResult(new PodcastTransferResult(true, true, "Podcast catalog installed. Safely eject the player through Windows before disconnecting.", journal.Audio.Count, journal.Audio.Count, journal.Revision));
        }
        catch (OperationCanceledException)
        {
            SaveJournal(journal);
            return Task.FromResult(new PodcastTransferResult(true, true, "Podcast catalog installed; cleanup is pending and will retry next time.", journal.Audio.Count, journal.Audio.Count, journal.Revision));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveJournal(journal);
            return Task.FromResult(new PodcastTransferResult(true, true, "Podcast catalog installed; cleanup is pending: " + ex.Message, journal.Audio.Count, journal.Audio.Count, journal.Revision));
        }
    }

    private PodcastTransferJournal CreateJournal(PlayerDevice device, PodcastCatalog catalog,
        IReadOnlyList<PodcastPreparedEpisode> prepared, byte[] catalogBytes, IReadOnlyList<string> cleanup)
    {
        var sd = device.Sd ?? throw new IOException("Connect the original SD card before creating a podcast transfer.");
        var stageRoot = Path.Combine(root, "pending", catalog.Revision);
        var backupRoot = Path.Combine(root, "backups", catalog.Revision);
        var audio = prepared.Select(p => new PodcastJournalAudio
        {
            ChannelId = p.Episode.ChannelId,
            VideoId = p.Episode.Id,
            CachePath = Path.GetFullPath(p.CachePath),
            Hash = p.Hash,
            Length = p.Length,
            StagePath = Path.Combine(stageRoot, p.Episode.ChannelId, p.Episode.Id + ".mp3"),
            DestinationPath = AudioPath(sd, p.Episode.Id),
            BackupPath = Path.Combine(backupRoot, p.Episode.Id + ".mp3")
        }).ToList();
        return new PodcastTransferJournal
        {
            DeviceId = device.Id,
            InternalId = device.Internal.Id,
            SdId = sd.Id,
            Revision = catalog.Revision,
            CatalogBase64 = Convert.ToBase64String(catalogBytes),
            StageRoot = stageRoot,
            BackupRoot = backupRoot,
            CatalogBackupPath = Path.Combine(backupRoot, "catalog.tsv"),
            IgnoreBackupPath = Path.Combine(backupRoot, "database.ignore"),
            InternalRoot = device.Internal.Root,
            SdRoot = sd.Root,
            CleanupPaths = cleanup.ToList(),
            Audio = audio
        };
    }

    private void ValidatePrepared(PodcastCatalog catalog, IReadOnlyList<PodcastPreparedEpisode> prepared)
    {
        var wanted = catalog.Episodes.Select(e => (e.ChannelId, e.Id)).ToHashSet();
        if (prepared.Count != wanted.Count || prepared.Any(p => !wanted.Contains((p.Episode.ChannelId, p.Episode.Id))))
            throw new InvalidOperationException("Every podcast catalog episode must be prepared before catalog publication.");
        foreach (var item in prepared)
        {
            PodcastId.Validate(item.Episode.ChannelId, "Channel ID");
            PodcastId.Validate(item.Episode.Id, "Video ID");
            var expectedCache = Path.Combine(root, "cache", item.Episode.ChannelId, item.Episode.Id + ".mp3");
            if (!File.Exists(item.CachePath) || !SamePath(item.CachePath, expectedCache))
                throw new IOException("A prepared podcast file is missing or outside the managed cache: " + item.Episode.Id);
            VerifyFile(item.CachePath, item.Hash, item.Length, "A prepared podcast file changed: ");
        }
    }

    private void ValidatePendingPrepared(PodcastCatalog catalog, IReadOnlyList<PodcastPreparedEpisode> prepared, PodcastTransferJournal journal)
    {
        var episodes = catalog.Episodes.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var supplied = prepared.ToDictionary(p => p.Episode.Id, StringComparer.Ordinal);
        foreach (var audio in journal.Audio.Where(a => !a.Staged))
        {
            if (!episodes.TryGetValue(audio.VideoId, out var episode) || episode.ChannelId != audio.ChannelId)
                throw new InvalidOperationException("The pending podcast journal has an unknown episode.");
            var expectedCache = Path.Combine(root, "cache", audio.ChannelId, audio.VideoId + ".mp3");
            if (!SamePath(audio.CachePath, expectedCache)) throw new InvalidOperationException("Pending podcast audio is outside the managed cache.");
            if (supplied.TryGetValue(audio.VideoId, out var item) && !SamePath(item.CachePath, audio.CachePath))
                throw new IOException("Prepared podcast audio changed while a transfer was pending.");
            VerifyFile(audio.CachePath, audio.Hash, audio.Length, "A pending podcast audio file changed: ");
        }
    }

    private void ValidateJournalPaths(PodcastTransferJournal journal, PlayerDevice device, PodcastCatalog catalog)
    {
        var sd = device.Sd ?? throw new IOException("Connect the original SD card before validating the podcast journal.");
        if (journal.Revision.Length != 64 || journal.Revision.Any(c => !Uri.IsHexDigit(c)) ||
            !SamePath(journal.InternalRoot, device.Internal.Root) || !SamePath(journal.SdRoot, sd.Root))
            throw new InvalidOperationException("The pending podcast journal has an invalid storage identity.");
        var stageRoot = Path.GetFullPath(Path.Combine(root, "pending", journal.Revision));
        var backupRoot = Path.GetFullPath(Path.Combine(root, "backups", journal.Revision));
        if (!SamePath(journal.StageRoot, stageRoot) || !SamePath(journal.BackupRoot, backupRoot) ||
            !SamePath(journal.CatalogBackupPath, Path.Combine(backupRoot, "catalog.tsv")) ||
            !SamePath(journal.IgnoreBackupPath, Path.Combine(backupRoot, "database.ignore")))
            throw new InvalidOperationException("The pending podcast journal has an invalid managed path.");

        var episodes = catalog.Episodes.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var audio in journal.Audio)
        {
            if (!episodes.TryGetValue(audio.VideoId, out var episode) || episode.ChannelId != audio.ChannelId || !seen.Add(audio.VideoId))
                throw new InvalidOperationException("The pending podcast journal has an invalid episode identity.");
            PodcastId.Validate(audio.ChannelId, "Channel ID");
            PodcastId.Validate(audio.VideoId, "Video ID");
            if (audio.Length < 0 || audio.Hash.Length != 64 || audio.Hash.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidOperationException("The pending podcast journal has invalid audio metadata.");
            var expectedCache = Path.Combine(root, "cache", audio.ChannelId, audio.VideoId + ".mp3");
            var expectedStage = Path.Combine(stageRoot, audio.ChannelId, audio.VideoId + ".mp3");
            var expectedBackup = Path.Combine(backupRoot, audio.VideoId + ".mp3");
            var expectedDestination = AudioPath(sd, audio.VideoId);
            if (!SamePath(audio.CachePath, expectedCache) || !SamePath(audio.StagePath, expectedStage) ||
                !SamePath(audio.BackupPath, expectedBackup) || !SamePath(audio.DestinationPath, expectedDestination))
                throw new InvalidOperationException("The pending podcast journal has an invalid episode path.");
        }
        if (journal.CleanupPaths.Any(path => !IsOwnedCleanupPath(device.Internal.Root, sd.Root, path)))
            throw new InvalidOperationException("The pending podcast journal has an invalid cleanup path.");
    }

    private void BackupIfNeeded(PodcastTransferJournal journal, string source, string backup)
    {
        if (File.Exists(backup) || !File.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The podcast destination is a symbolic link.");
        PodcastMediaPreparer.CopyFlushHash(source, backup, out _, out _);
        SaveJournal(journal);
    }

    private void ReplaceFromFile(string source, string destination, string expectedHash, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".rbmanager-podcast.tmp";
        try
        {
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The podcast destination is a symbolic link.");
            if (File.Exists(temp) && (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The podcast temporary path is a symbolic link.");
            PodcastMediaPreparer.CopyFlushHash(source, temp, out var hash, out _, cancellation);
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Podcast staging hash verification failed.");
            hooks.BeforeReplace(destination);
            File.Move(temp, destination, true);
            if (HashFile(destination) != expectedHash) throw new IOException("Podcast destination hash verification failed.");
            hooks.AfterReplace(destination);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void ReplaceBytes(string path, byte[] bytes, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".rbmanager-podcast.tmp";
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The podcast {name} path is a symbolic link.");
            if (File.Exists(temp) && (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The podcast temporary catalog path is a symbolic link.");
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            hooks.BeforeReplace(path);
            File.Move(temp, path, true);
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException($"Podcast {name} verification failed.");
            hooks.AfterReplace(path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void EnsureEmptyFile(string path)
    {
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The database.ignore path is a symbolic link.");
            if (new FileInfo(path).Length == 0) return;
        }
        ReplaceBytes(path, [], "database.ignore");
    }

    private void Restore(PodcastTransferJournal journal)
    {
        try
        {
            foreach (var audio in journal.Audio.Where(a => a.Replaced))
            {
                if (File.Exists(audio.BackupPath)) RestoreFromFile(audio.BackupPath, audio.DestinationPath, HashFile(audio.BackupPath));
                else if (File.Exists(audio.DestinationPath)) File.Delete(audio.DestinationPath);
                audio.Replaced = false;
            }
            if (journal.CatalogWriteStarted)
            {
                var catalogPath = CatalogPathFromJournal(journal);
                if (File.Exists(journal.CatalogBackupPath)) RestoreFromFile(journal.CatalogBackupPath, catalogPath, HashFile(journal.CatalogBackupPath));
                else if (File.Exists(catalogPath)) File.Delete(catalogPath);
            }
            SaveJournal(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveJournal(journal);
        }
    }

    private static void RestoreFromFile(string source, string destination, string expectedHash)
    {
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The podcast rollback destination is a symbolic link.");
        var temp = destination + ".restore.tmp";
        try
        {
            PodcastMediaPreparer.CopyFlushHash(source, temp, out var hash, out _);
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Podcast rollback hash verification failed.");
            File.Move(temp, destination, true);
            if (HashFile(destination) != expectedHash) throw new IOException("Podcast rollback destination hash verification failed.");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private string CatalogPathFromJournal(PodcastTransferJournal journal) => Path.Combine(journal.InternalRoot, ".rockbox/podcasts/catalog.tsv");
    private string IgnorePathFromJournal(PodcastTransferJournal journal) => Path.Combine(journal.SdRoot, "Podcasts/RockboxManager/database.ignore");
    private static string CatalogPath(PlayerVolume internalVolume) => Path.Combine(internalVolume.Root, ".rockbox/podcasts/catalog.tsv");
    private static string IgnorePath(PlayerVolume sd) => Path.Combine(sd.Root, "Podcasts/RockboxManager/database.ignore");
    private static string AudioPath(PlayerVolume sd, string id) => Path.Combine(sd.Root, "Podcasts/RockboxManager", PodcastId.Validate(id, "Video ID") + ".mp3");
    private static string ResumePath(PlayerVolume internalVolume, string id) => Path.Combine(internalVolume.Root, ".rockbox/podcasts/resume", PodcastId.Validate(id, "Video ID") + ".tsv");

    private static void ValidateMounted(PlayerDevice expected, PlayerDevice mounted)
    {
        var expectedSd = expected.Sd;
        var mountedSd = mounted.Sd;
        if (expectedSd is null || mountedSd is null || mounted.Id != expected.Id ||
            mounted.Internal.Id != expected.Internal.Id || !SamePath(mounted.Internal.Root, expected.Internal.Root) ||
            mountedSd.Id != expectedSd.Id || !SamePath(mountedSd.Root, expectedSd.Root))
            throw new IOException("The mounted player identity changed while connecting.");
    }

    private static void ValidateVolumeRoot(PlayerVolume volume, string label)
    {
        var fullRoot = Path.GetFullPath(volume.Root);
        if (!Directory.Exists(fullRoot)) throw new IOException($"The {label} is unavailable.");
        for (var current = fullRoot;;)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The {label} path contains a symbolic link or junction.");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || SamePath(parent, current)) break;
            current = parent;
        }
        foreach (var relative in volume.Storage == Storage.Sd
            ? new[] { "Podcasts", "Podcasts/RockboxManager", ".rockbox", ".rockbox/podcasts", ".rockbox/podcasts/resume" }
            : new[] { ".rockbox", ".rockbox/podcasts", ".rockbox/podcasts/resume" })
        {
            var path = Path.Combine(fullRoot, relative);
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The {label} podcast path contains a symbolic link or junction.");
        }
    }

    private static void RequireSpace(PlayerVolume volume, long needed, string label)
    {
        if (volume.FreeBytes != long.MaxValue && volume.FreeBytes < needed)
            throw new IOException($"There is not enough free space on {label} for the podcast drop.");
    }

    private static bool IsOwnedCleanupPath(string internalRoot, string sdRoot, string path) =>
        IsDirectManagedPath(sdRoot, path, "Podcasts/RockboxManager", ".mp3") ||
        IsDirectManagedPath(internalRoot, path, ".rockbox/podcasts/resume", ".tsv");

    private static bool IsDirectManagedPath(string root, string path, string relativeDirectory, string extension)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)).Replace('\\', '/');
            var prefix = relativeDirectory + "/";
            if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || relative[prefix.Length..].Contains('/')) return false;
            var file = relative[prefix.Length..];
            if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
            return PodcastId.IsValid(file[..^extension.Length]);
        }
        catch (ArgumentException) { return false; }
    }

    private static void DeleteOwnedDirectory(string path, string managedRoot)
    {
        if (!Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        var expectedRoot = Path.GetFullPath(managedRoot);
        if (SamePath(full, expectedRoot) || !IsUnderRoot(full, expectedRoot) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to delete an unmanaged podcast directory.");
        Directory.Delete(full, true);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyFile(string path, string expectedHash, long expectedLength, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException(message + Path.GetFileName(path));
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) || stream.Length != expectedLength)
            throw new IOException(message + Path.GetFileName(path));
    }

    private static bool CatalogMatches(string path, string expectedBase64)
    {
        try { return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(Convert.FromBase64String(expectedBase64)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return false; }
    }

    private static byte[]? ReadOptional(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private PodcastTransferJournal? LoadJournal()
    {
        if (!File.Exists(journalPath)) return null;
        try { return JsonSerializer.Deserialize<PodcastTransferJournal>(File.ReadAllText(journalPath)) ?? throw new InvalidOperationException("The podcast transfer journal is empty."); }
        catch (JsonException ex) { throw new InvalidOperationException("The podcast transfer journal is corrupt.", ex); }
    }

    private void SaveJournal(PodcastTransferJournal journal)
    {
        var temp = journalPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, journal, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, journalPath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static bool HasPendingTransfer(string root) => File.Exists(Path.Combine(Path.GetFullPath(root), "transfer.json"));

    public static bool TryLoadPending(string root, out PodcastCatalog? catalog)
    {
        catalog = null;
        try
        {
            var path = Path.Combine(Path.GetFullPath(root), "transfer.json");
            if (!File.Exists(path)) return false;
            var journal = JsonSerializer.Deserialize<PodcastTransferJournal>(File.ReadAllText(path));
            if (journal is null || string.IsNullOrWhiteSpace(journal.CatalogBase64)) return false;
            return PodcastCatalogCodec.TryParse(Convert.FromBase64String(journal.CatalogBase64), out catalog, out _);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
        { return false; }
    }

    private List<PodcastInstallState> LoadStates()
    {
        try
        {
            var path = Path.Combine(root, "installed.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<List<PodcastInstallState>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void SaveState(PodcastInstallState state)
    {
        var states = LoadStates();
        states.RemoveAll(s => s.DeviceId == state.DeviceId);
        states.Add(state);
        var path = Path.Combine(root, "installed.json");
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, states, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public sealed class PodcastTransferJournal
{
    public string DeviceId { get; set; } = "";
    public string InternalId { get; set; } = "";
    public string SdId { get; set; } = "";
    public string Revision { get; set; } = "";
    public string CatalogBase64 { get; set; } = "";
    public string StageRoot { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public string CatalogBackupPath { get; set; } = "";
    public string IgnoreBackupPath { get; set; } = "";
    public string InternalRoot { get; set; } = "";
    public string SdRoot { get; set; } = "";
    public bool Published { get; set; }
    public bool CatalogWriteStarted { get; set; }
    public List<string> CleanupPaths { get; set; } = [];
    public List<PodcastJournalAudio> Audio { get; set; } = [];
}

public sealed class PodcastJournalAudio
{
    public string ChannelId { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string CachePath { get; set; } = "";
    public string StagePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Length { get; set; }
    public bool Staged { get; set; }
    public bool Replaced { get; set; }
}
