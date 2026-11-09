using System.Text;
using System.Text.Json;

namespace RockboxManager.Core;

public static class PodcastId
{
    public static bool IsValid(string value) => !string.IsNullOrEmpty(value) &&
        value.Length < PodcastLimits.IdMax && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static string Validate(string value, string label = "Podcast ID")
    {
        if (!IsValid(value)) throw new InvalidOperationException($"{label} contains invalid characters.");
        return value;
    }
}

public static class PodcastLimits
{
    // These buffers include their terminating byte in the firmware parser.
    public const int IdMax = 64;
    public const int NameMax = 512;
    public const int TitleMax = 512;
    public const int MaxChannels = 32;
    public const int MaxEpisodes = 96;
    public const int MaxCatalogLine = 2048;

    public static string ValidateText(string value, string label, int bufferSize)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{label} must not be empty.");
        if (value.Contains('\0')) throw new InvalidOperationException($"{label} contains an unsupported character.");
        if (Encoding.UTF8.GetByteCount(value) >= bufferSize)
            throw new InvalidOperationException($"{label} exceeds the firmware limit.");
        return value;
    }

    public static void ValidateCatalogSize(PodcastCatalog catalog)
    {
        if (catalog.Channels.Count > MaxChannels) throw new InvalidOperationException("The podcast catalog has too many channels.");
        if (catalog.Episodes.Count > MaxEpisodes) throw new InvalidOperationException("The podcast catalog has too many episodes.");
    }
}

public sealed record PodcastWeek
{
    public DateOnly Monday { get; }
    public DateOnly Sunday { get; }

    public PodcastWeek(DateOnly monday, DateOnly sunday)
    {
        if (sunday.DayNumber != monday.DayNumber + 6 || monday.DayOfWeek != DayOfWeek.Monday)
            throw new ArgumentException("A podcast week must contain one Monday through Sunday week.");
        Monday = monday; Sunday = sunday;
    }

    public static PodcastWeek PreviousCompleted(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, NewYork).Date;
        var monday = local.AddDays(-(int)local.DayOfWeek + (int)DayOfWeek.Monday);
        if (local.DayOfWeek == DayOfWeek.Sunday) monday = local.AddDays(-6);
        monday = monday.AddDays(-7);
        return new PodcastWeek(DateOnly.FromDateTime(monday), DateOnly.FromDateTime(monday.AddDays(6)));
    }

    public bool Contains(DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, NewYork).Date;
        return local >= Monday.ToDateTime(TimeOnly.MinValue) && local <= Sunday.ToDateTime(TimeOnly.MaxValue);
    }

    public string Label => $"{Monday:yyyy-MM-dd} to {Sunday:yyyy-MM-dd}";

    private static TimeZoneInfo NewYork
    {
        get
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        }
    }
}

public sealed record PodcastSubscription(string ChannelId, string Name, string Url)
{
    public PodcastSubscription Normalize()
    {
        var id = PodcastId.Validate(ChannelId.Trim(), "Channel ID");
        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Podcast subscriptions must use a public HTTPS YouTube channel URL.");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Podcast channel URLs must not contain credentials, query strings, or fragments.");
        var name = string.IsNullOrWhiteSpace(Name) ? id : Name.Trim();
        return this with { ChannelId = id, Name = PodcastLimits.ValidateText(name, "Channel name", PodcastLimits.NameMax), Url = uri.ToString() };
    }
}

public sealed record PodcastChannel(string Id, string Name)
{
    public PodcastChannel Normalize()
    {
        var id = PodcastId.Validate(Id.Trim(), "Channel ID");
        var name = string.IsNullOrWhiteSpace(Name) ? id : Name.Trim();
        return this with { Id = id, Name = PodcastLimits.ValidateText(name, "Channel name", PodcastLimits.NameMax) };
    }
}

public sealed record PodcastEpisode(string Id, string ChannelId, string ChannelName, string Title,
    DateTimeOffset PublishedAt, TimeSpan Duration)
{
    public string AudioPath
    {
        get
        {
            var path = $"/<microSD1>/Podcasts/RockboxManager/{PodcastId.Validate(Id, "Video ID")}.mp3";
            PlaylistCodec.ValidatePath(path); return path;
        }
    }
    public long PublicationUnixSeconds => PublishedAt.ToUnixTimeSeconds();
    public long DurationMilliseconds => Math.Max(0, (long)Math.Round(Duration.TotalMilliseconds));
}

public sealed record PodcastCatalog(PodcastWeek Week, IReadOnlyList<PodcastChannel> Channels, IReadOnlyList<PodcastEpisode> Episodes)
{
    public string Revision => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PodcastCatalogCodec.Write(this)));
}

public sealed record PodcastEnumerationResult(bool Checked, PodcastChannel? Channel,
    IReadOnlyList<PodcastEpisode> Episodes, string? Error = null);

public sealed record PodcastPreparedEpisode(PodcastEpisode Episode, string CachePath, string Hash, long Length);

public sealed record PodcastCacheKey(string ChannelId, string VideoId);

public sealed record PodcastPreparationResult(bool Success, bool Checked, PodcastCatalog? Catalog,
    IReadOnlyList<PodcastPreparedEpisode> Prepared, string Status, string? Error = null)
{
    public string Revision => Catalog?.Revision ?? "";
}

public sealed record PodcastInstallState(string DeviceId, string InternalId, string SdId,
    DateOnly Monday, DateOnly Sunday, string Revision, IReadOnlyList<string> InstalledEpisodeIds);

public sealed record PodcastTransferResult(bool Success, bool Committed, string Message,
    int Completed, int Total, string Revision = "");

public sealed record PodcastProgress(string Message, int Completed, int Total, bool Indeterminate = false);

public interface IPodcastVideoSource
{
    Task<PodcastEnumerationResult> EnumerateAsync(PodcastSubscription subscription, PodcastWeek week,
        CancellationToken cancellation = default, IProgress<PodcastProgress>? progress = null);
}

public interface IPodcastMediaPreparer
{
    Task<PodcastPreparedEpisode> PrepareAsync(PodcastEpisode episode, CancellationToken cancellation = default,
        IProgress<PodcastProgress>? progress = null);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellation = default);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null, bool UseShellExecute = false);

public interface IPodcastTransferHooks
{
    void BeforeReplace(string path);
    void AfterReplace(string path);
}

public interface IPodcastMountProvider
{
    PlayerDevice? Recheck(PlayerDevice expected);
}

public sealed class FileSystemPodcastMountProvider : IPodcastMountProvider
{
    public PlayerDevice? Recheck(PlayerDevice expected)
    {
        if (!Directory.Exists(expected.Internal.Root) || expected.Internal.Id.Length == 0) return null;
        var sd = expected.Sd;
        if (sd is not null && (!Directory.Exists(sd.Root) || sd.Id.Length == 0)) return null;
        return expected;
    }
}

public sealed class NoopPodcastTransferHooks : IPodcastTransferHooks
{
    public void BeforeReplace(string path) { }
    public void AfterReplace(string path) { }
}

public sealed class PodcastSourceException(string message) : IOException(message);

public sealed class PodcastSubscriptionStore
{
    private readonly string path;
    private readonly List<PodcastSubscription> subscriptions;
    public IReadOnlyList<PodcastSubscription> Subscriptions => subscriptions;

    public PodcastSubscriptionStore(string root)
    {
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "subscriptions.json");
        subscriptions = Load(path);
    }

    public void Add(PodcastSubscription subscription)
    {
        var normalized = subscription.Normalize();
        subscriptions.RemoveAll(s => s.ChannelId.Equals(normalized.ChannelId, StringComparison.Ordinal));
        subscriptions.Add(normalized);
        Save();
    }

    public void Remove(string channelId)
    {
        PodcastId.Validate(channelId.Trim(), "Channel ID");
        subscriptions.RemoveAll(s => s.ChannelId.Equals(channelId.Trim(), StringComparison.Ordinal));
        Save();
    }

    public void Replace(IEnumerable<PodcastSubscription> values)
    {
        subscriptions.Clear();
        foreach (var value in values) AddWithoutSave(value.Normalize());
        Save();
    }

    private void AddWithoutSave(PodcastSubscription value)
    {
        subscriptions.RemoveAll(s => s.ChannelId.Equals(value.ChannelId, StringComparison.Ordinal));
        subscriptions.Add(value);
    }

    private void Save()
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, subscriptions, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }

    private static List<PodcastSubscription> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var values = JsonSerializer.Deserialize<List<PodcastSubscription>>(File.ReadAllText(path)) ?? [];
            var result = new List<PodcastSubscription>();
            foreach (var value in values)
            {
                try
                {
                    var normalized = value.Normalize();
                    if (!result.Any(s => s.ChannelId.Equals(normalized.ChannelId, StringComparison.Ordinal))) result.Add(normalized);
                }
                catch (InvalidOperationException) { }
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return []; }
    }
}

public sealed class PodcastCache
{
    public string Root { get; }
    public PodcastCache(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }

    public string PathFor(string channelId, string videoId)
    {
        PodcastId.Validate(channelId, "Channel ID"); PodcastId.Validate(videoId, "Video ID");
        return Path.Combine(Root, channelId, videoId + ".mp3");
    }

    public string Write(string channelId, string videoId, byte[] content)
    {
        var path = PathFor(channelId, videoId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content); return path;
    }

    public void Prune(IEnumerable<PodcastCacheKey> current, IEnumerable<PodcastCacheKey> prepared, IEnumerable<PodcastCacheKey> pending)
    {
        var keep = current.Concat(prepared).Concat(pending)
            .Select(k => Path.GetFullPath(PathFor(k.ChannelId, k.VideoId)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(Root)) return;
        foreach (var file in Directory.EnumerateFiles(Root, "*.mp3", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !keep.Contains(full)) File.Delete(full);
        }
    }
}
