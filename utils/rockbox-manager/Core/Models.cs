using System.Security.Cryptography;

namespace RockboxManager.Core;

public enum Storage { Internal, Sd, Unknown }

public sealed record PlayerVolume(Storage Storage, string Id, string Root, long FreeBytes);

public sealed record PlayerDevice(string Id, string Name, PlayerVolume Internal, PlayerVolume? Sd,
    string Codepage = "utf-8")
{
    public PlayerVolume? Volume(Storage storage) => storage switch
    {
        Storage.Internal => Internal, Storage.Sd => Sd, _ => null
    };
    public IEnumerable<PlayerVolume> Volumes => Sd is null ? [Internal] : [Internal, Sd];
    public string FilePath(Storage storage, string relative) => Path.Combine(
        Volume(storage)?.Root ?? throw new IOException($"{storage} storage is disconnected."),
        relative.Replace('/', Path.DirectorySeparatorChar));
}

public sealed record SongPath(Storage Storage, string RelativePath)
{
    public string Key => $"{Storage}:{RelativePath}";
    public string RockboxPath => Storage switch
    {
        Storage.Internal => "/" + RelativePath,
        Storage.Sd => "/<microSD1>/" + RelativePath,
        _ => RelativePath
    };
}

public sealed record Track(SongPath Path, string Title, string Artist, string Album,
    string Genre, TimeSpan Duration, bool Available = true, string AlbumArtist = "",
    uint DiscNumber = 0, uint TrackNumber = 0)
{
    public string GroupArtist => string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist;
    public string AlbumKey => LibraryViews.Identity(GroupArtist, Album);
    internal string YouTubeVideoId { get; init; } = "";
    internal bool? HasEmbeddedArtwork { get; init; }
    internal string ArtworkReleaseId { get; init; } = "";
    internal string ArtworkReleaseGroupId { get; init; } = "";
    public string DurationLabel => Duration == TimeSpan.Zero ? "—" :
        Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");
    public string StorageLabel => Path.Storage == Storage.Sd ? "SD card" :
        Path.Storage == Storage.Internal ? "Internal" : "Unknown";
}

public sealed record FileSnapshot(Storage Storage, string RelativePath, string? Hash)
{
    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static FileSnapshot Read(PlayerDevice device, Storage storage, string relative)
    {
        var path = device.FilePath(storage, relative);
        return new(storage, relative, File.Exists(path) ? Digest(File.ReadAllBytes(path)) : null);
    }
}

public sealed class PlaylistDraft
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Stem { get; internal set; }
    public string Name => System.IO.Path.GetFileName(Stem);
    public bool IsLiked { get; }
    public bool IsNew { get; internal set; }
    public bool Deleted { get; internal set; }
    public bool ForceWrite { get; internal set; }
    public bool HasConflict => SdAlternative is not null;
    public List<SongPath> Entries { get; internal set; }
    public List<SongPath>? SdAlternative { get; internal set; }
    public Dictionary<Storage, FileSnapshot> Copies { get; } = [];
    public string OriginalStem { get; }
    private readonly List<SongPath> originalEntries;
    private List<SongPath>? originalAlternative;
    public bool IsDirty => IsNew ? !Deleted : Deleted || ForceWrite || Stem != OriginalStem ||
        !Entries.SequenceEqual(originalEntries);
    public string TargetPath => "Playlists/" + Stem + ".m3u8";
    public string Label => Name + (HasConflict ? "  • copies differ" : "") + (IsDirty ? "  •" : "");
    public string CopyLabel => IsLiked ? "Player likes" : Copies.Count == 2 ? "Internal + SD" :
        Copies.ContainsKey(Storage.Sd) ? "SD · mirror on send" : "Internal · mirror on send";

    public PlaylistDraft(string stem, IEnumerable<SongPath> entries, bool liked = false, bool isNew = false)
    {
        Stem = OriginalStem = stem;
        Entries = [.. entries];
        originalEntries = [.. Entries];
        IsLiked = liked;
        IsNew = isNew;
    }
    internal void SetAlternative(List<SongPath> entries)
    {
        SdAlternative = entries;
        originalAlternative = [.. entries];
    }
    internal void Reset()
    {
        Stem = OriginalStem;
        Entries = [.. originalEntries];
        SdAlternative = originalAlternative is null ? null : [.. originalAlternative];
        Deleted = ForceWrite = false;
    }
}
