using System.Text.Json;

namespace RockboxManager.Core;

public sealed class PodcastManager
{
    private readonly PodcastSubscriptionStore subscriptions;
    private readonly IPodcastVideoSource source;
    private readonly IPodcastMediaPreparer media;
    private readonly PodcastCache cache;
    private readonly string root;
    private readonly IPodcastMountProvider mounts;
    private readonly Func<DateTimeOffset> now;
    private PodcastPreparationResult? prepared;
    private string preparedSubscriptions = "";

    public PodcastSubscriptionStore Subscriptions => subscriptions;
    public PodcastPreparationResult? Prepared => prepared;
    public PodcastWeek CurrentWeek => PodcastWeek.PreviousCompleted(now());

    public PodcastManager(string root, IPodcastVideoSource source, IPodcastMediaPreparer media, IPodcastMountProvider? mounts = null,
        Func<DateTimeOffset>? now = null)
    {
        this.root = Path.GetFullPath(root); Directory.CreateDirectory(this.root);
        subscriptions = new PodcastSubscriptionStore(this.root);
        this.source = source; this.media = media;
        cache = new PodcastCache(Path.Combine(this.root, "cache"));
        this.mounts = mounts ?? new FileSystemPodcastMountProvider();
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<PodcastSubscription> AddAsync(string url, CancellationToken cancellation = default,
        IProgress<PodcastProgress>? progress = null)
    {
        var normalizedUrl = NormalizeUrl(url);
        var provisional = new PodcastSubscription("UC_pending", "", normalizedUrl);
        var result = await source.EnumerateAsync(provisional, CurrentWeek, cancellation, progress);
        if (!result.Checked || result.Channel is null || result.Channel.Id == provisional.ChannelId || !string.IsNullOrEmpty(result.Error))
            throw new PodcastSourceException(result.Error ?? "The public YouTube channel could not be checked.");
        var subscription = new PodcastSubscription(result.Channel.Id, result.Channel.Name, normalizedUrl).Normalize();
        subscriptions.Add(subscription);
        return subscription;
    }

    public void Remove(string channelId) => subscriptions.Remove(channelId);

    public async Task<PodcastPreparationResult> PrepareAsync(CancellationToken cancellation = default,
        IProgress<PodcastProgress>? progress = null, bool retry = false)
    {
        var week = CurrentWeek;
        var subscriptionRevision = SubscriptionRevision();
        var cached = prepared ?? LoadPrepared();
        if (!retry && cached is { Success: true, Catalog: not null } && cached.Catalog.Week == week &&
            preparedSubscriptions == subscriptionRevision) return cached;
        var all = new List<PodcastEpisode>(); var channels = new List<PodcastChannel>();
        var currentSubscriptions = subscriptions.Subscriptions.ToList();
        if (currentSubscriptions.Count == 0)
        {
            var empty = new PodcastPreparationResult(true, true, new PodcastCatalog(week, [], []), [], "Checked: no podcast channels are subscribed.");
            prepared = empty; SavePrepared(empty); return empty;
        }
        try
        {
            for (var i = 0; i < currentSubscriptions.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var subscription = currentSubscriptions[i];
                progress?.Report(new($"Checking {subscription.Name}…", i, currentSubscriptions.Count, true));
                var result = await source.EnumerateAsync(subscription, week, cancellation, progress);
                if (!result.Checked || !string.IsNullOrEmpty(result.Error))
                {
                    var failed = new PodcastPreparationResult(false, false, null, [],
                        $"Source error for {subscription.Name}: {result.Error ?? "the channel was not checked"}.", result.Error);
                    prepared = failed; return failed;
                }
                if (result.Channel is not null)
                {
                    channels.Add(result.Channel);
                    if (!subscription.ChannelId.Equals(result.Channel.Id, StringComparison.Ordinal) || subscription.Name != result.Channel.Name)
                        subscriptions.Add(subscription with { ChannelId = result.Channel.Id, Name = result.Channel.Name });
                }
                all.AddRange(result.Episodes);
            }
            var distinct = all.GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var catalogChannels = channels.Concat(distinct.Select(e => new PodcastChannel(e.ChannelId, e.ChannelName)))
                .GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var catalog = new PodcastCatalog(week, catalogChannels, distinct);
            var ready = new List<PodcastPreparedEpisode>();
            for (var i = 0; i < distinct.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(new($"Preparing {distinct[i].Title}…", i, distinct.Count));
                    ready.Add(await media.PrepareAsync(distinct[i], cancellation, progress));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or PodcastSourceException or UnauthorizedAccessException)
                {
                    var failed = new PodcastPreparationResult(false, true, catalog, ready,
                        $"Could not prepare all podcast audio: {ex.Message}", ex.Message);
                    prepared = failed; SavePrepared(failed); return failed;
                }
            }
            var resultReady = new PodcastPreparationResult(true, true, catalog, ready,
                distinct.Count == 0 ? $"Checked {catalog.Week.Label}: empty drop is valid." : $"Prepared {ready.Count:N0} podcast episode{(ready.Count == 1 ? "" : "s")} for {catalog.Week.Label}.");
            prepared = resultReady; SavePrepared(resultReady);
            var pending = PodcastTransferService.TryLoadPending(root, out var pendingCatalog) && pendingCatalog is not null
                ? pendingCatalog.Episodes.Select(e => new PodcastCacheKey(e.ChannelId, e.Id)) : [];
            cache.Prune(distinct.Select(e => new PodcastCacheKey(e.ChannelId, e.Id)), ready.Select(e => new PodcastCacheKey(e.Episode.ChannelId, e.Episode.Id)), pending);
            return resultReady;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            var failed = new PodcastPreparationResult(false, false, null, [], "Podcast preparation failed: " + ex.Message, ex.Message);
            prepared = failed; return failed;
        }
    }

    public async Task<PodcastTransferResult> PublishIfDueAsync(PlayerDevice device, CancellationToken cancellation = default,
        IProgress<PodcastProgress>? progress = null)
    {
        if (PodcastTransferService.TryLoadPending(root, out var pendingCatalog) && pendingCatalog is not null)
            return await new PodcastTransferService(root, mounts: mounts).PublishAsync(device, pendingCatalog, [], cancellation, progress);
        var result = prepared ?? LoadPrepared();
        if (result is null || !result.Success || result.Catalog is null) return new(false, false, "Prepare podcasts before connecting a player.", 0, 0);
        if (result.Catalog.Week != CurrentWeek || preparedSubscriptions != SubscriptionRevision())
            return new(false, false, "The prepared drop is outdated. Prepare the latest completed week before sending.", 0, 0);
        return await new PodcastTransferService(root, mounts: mounts).PublishAsync(device, result.Catalog, result.Prepared, cancellation, progress);
    }

    private void SavePrepared(PodcastPreparationResult result)
    {
        if (result.Catalog is null) return;
        preparedSubscriptions = SubscriptionRevision();
        var manifest = new PodcastPreparedManifest(result.Catalog, result.Prepared, preparedSubscriptions, result.Success);
        var path = Path.Combine(root, "prepared.json"); var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(temp, path, true);
    }

    private PodcastPreparationResult? LoadPrepared()
    {
        try
        {
            var path = Path.Combine(root, "prepared.json"); if (!File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<PodcastPreparedManifest>(File.ReadAllText(path));
            if (manifest is null) return null;
            var valid = manifest.Success && manifest.Prepared.Count == manifest.Catalog.Episodes.Count &&
                manifest.Prepared.All(p => File.Exists(p.CachePath));
            preparedSubscriptions = manifest.Subscriptions;
            return prepared = valid ? new(true, true, manifest.Catalog, manifest.Prepared, "Prepared podcast drop loaded.") : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a public HTTPS YouTube channel URL.");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Channel URLs must not contain credentials, query strings, or fragments.");
        return uri.ToString();
    }

    private string SubscriptionRevision() => string.Join("\n", subscriptions.Subscriptions
        .OrderBy(s => s.ChannelId, StringComparer.Ordinal).Select(s => s.ChannelId));

    private sealed record PodcastPreparedManifest(PodcastCatalog Catalog, IReadOnlyList<PodcastPreparedEpisode> Prepared,
        string Subscriptions = "", bool Success = false);

}
