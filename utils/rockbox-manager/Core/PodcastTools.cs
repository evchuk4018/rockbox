using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace RockboxManager.Core;

public sealed record PortableToolPaths(string BaseDirectory)
{
    public string YtDlp => Path.Combine(BaseDirectory, "yt-dlp.exe");
    public string Deno => Path.Combine(BaseDirectory, "deno.exe");
    public string Ffmpeg => Path.Combine(BaseDirectory, "ffmpeg.exe");
    public string Ffprobe => Path.Combine(BaseDirectory, "ffprobe.exe");
    public string FfmpegDirectory => Path.GetDirectoryName(Ffmpeg)!;

    public static PortableToolPaths BesideExecutable() => new(Path.Combine(AppContext.BaseDirectory, "tools"));

    public void Require()
    {
        var missing = new[] { (YtDlp, "yt-dlp"), (Deno, "Deno"), (Ffmpeg, "FFmpeg"), (Ffprobe, "ffprobe") }
            .Where(pair => !File.Exists(pair.Item1)).Select(pair => pair.Item2).ToArray();
        if (missing.Length > 0) throw new FileNotFoundException("Podcast tools are missing: " + string.Join(", ", missing) + ".");
        var manifest = Path.Combine(BaseDirectory, "manifest.json");
        if (!File.Exists(manifest)) throw new IOException("Podcast tool checksum manifest is missing. Reinstall the portable Manager package.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            if (document.RootElement.GetProperty("schema").GetInt32() != 1) throw new IOException("Unsupported podcast tool manifest.");
            var files = document.RootElement.GetProperty("files");
            foreach (var path in new[] { YtDlp, Deno, Ffmpeg, Ffprobe })
            {
                var expected = files.GetProperty(Path.GetFileName(path)).GetProperty("sha256").GetString();
                using var input = File.OpenRead(path);
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Podcast tool checksum failed: " + Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new IOException("Podcast tool checksum manifest is invalid. Reinstall the portable Manager package.", ex); }
    }
}

public static class PodcastToolArguments
{
    private static IReadOnlyList<string> Common(PortableToolPaths tools) =>
        ["--ignore-config", "--no-cookies", "--no-cache-dir", "--no-warnings", "--quiet",
         "--no-call-home", "--js-runtimes", "deno:" + tools.Deno, "--ffmpeg-location", tools.FfmpegDirectory];

    public static ProcessSpec Metadata(PortableToolPaths tools, string url)
    {
        var args = Common(tools).ToList();
        args.AddRange(["--skip-download", "--flat-playlist", "--extractor-args", "youtubetab:approximate_date", "--dump-single-json", url]);
        return new ProcessSpec(tools.YtDlp, args);
    }

    public static ProcessSpec VideoMetadata(PortableToolPaths tools, string url)
    {
        var args = Common(tools).ToList();
        args.AddRange(["--skip-download", "--no-playlist", "--dump-single-json", url]);
        return new ProcessSpec(tools.YtDlp, args);
    }

    public static ProcessSpec Audio(PortableToolPaths tools, string url, string output)
    {
        var args = Common(tools).ToList();
        args.AddRange(["--no-playlist", "--format", "bestaudio/best", "--output", output, url]);
        return new ProcessSpec(tools.YtDlp, args);
    }

    public static ProcessSpec FfmpegTranscode(PortableToolPaths tools, string source, string destination, PodcastEpisode episode)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-i", source, "-map_metadata", "-1",
            "-vn", "-ac", "2", "-codec:a", "libmp3lame", "-b:a", "128k", "-metadata", "title=" + episode.Title,
            "-metadata", "artist=" + episode.ChannelName, "-metadata", "album=" + episode.ChannelName,
            "-metadata", "comment=RockboxManager-ChannelId:" + episode.ChannelId, "-y", destination };
        return new ProcessSpec(tools.Ffmpeg, args);
    }

    public static ProcessSpec Probe(PortableToolPaths tools, string source) => new(tools.Ffprobe,
        ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", source]);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo { FileName = spec.FileName, WorkingDirectory = spec.WorkingDirectory ?? "",
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new IOException($"Could not start {Path.GetFileName(spec.FileName)}.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
    }
}

public sealed class YtDlpPodcastSource(PortableToolPaths tools, IProcessRunner? runner = null) : IPodcastVideoSource
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    private readonly bool portable = runner is null;

    public async Task<PodcastEnumerationResult> EnumerateAsync(PodcastSubscription subscription, PodcastWeek week,
        CancellationToken cancellation = default, IProgress<PodcastProgress>? progress = null)
    {
        var normalized = subscription.Normalize();
        var channelUrl = VideosUrl(normalized.Url);
        try
        {
            if (portable) tools.Require();
            progress?.Report(new("Checking all channel pages…", 0, 0, true));
            var listing = await runner.RunAsync(PodcastToolArguments.Metadata(tools, channelUrl), cancellation);
            if (listing.ExitCode != 0) return new(false, null, [], SourceError(listing));
            using var document = JsonDocument.Parse(listing.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || StringValue(root, "channel_id") is not { } resolvedId ||
                !resolvedId.StartsWith("UC", StringComparison.Ordinal) || !PodcastId.IsValid(resolvedId) ||
                !root.TryGetProperty("entries", out var values) || values.ValueKind != JsonValueKind.Array)
                return new(false, null, [], "YouTube did not return a complete public channel Videos listing.");
            var channel = new PodcastChannel(
                resolvedId,
                StringValue(root, "channel") ?? normalized.Name).Normalize();
            var entries = values.EnumerateArray().ToList();
            var candidates = new List<(string Id, string Url)>();
            foreach (var entry in entries)
            {
                var id = StringValue(entry, "id"); if (!PodcastId.IsValid(id ?? "")) continue;
                if (IsExcluded(entry)) continue;
                // Approximate dates avoid a full metadata request for every channel video.
                if (!CouldBeInWeek(entry, week)) continue;
                candidates.Add((id!, StringValue(entry, "webpage_url") ?? "https://www.youtube.com/watch?v=" + id));
            }
            var episodes = new List<PodcastEpisode>();
            var errors = new List<string>();
            for (var i = 0; i < candidates.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (id, url) = candidates[i];
                progress?.Report(new($"Reading episode metadata… {i + 1:N0} of {candidates.Count:N0}", i, candidates.Count));
                var detail = await runner.RunAsync(PodcastToolArguments.VideoMetadata(tools, url), cancellation);
                if (detail.ExitCode != 0)
                {
                    errors.Add($"{id}: {SourceError(detail)}");
                    continue;
                }
                using var fullDocument = JsonDocument.Parse(detail.StandardOutput);
                var full = fullDocument.RootElement;
                if (!string.Equals(StringValue(full, "id"), id, StringComparison.Ordinal))
                {
                    errors.Add($"{id}: full metadata returned a different video ID.");
                    continue;
                }
                if (IsExcluded(full)) continue;
                if (Timestamp(full) is not { } timestamp)
                {
                    errors.Add($"{id}: missing precise publication timestamp.");
                    continue;
                }
                if (!week.Contains(timestamp)) continue;
                if (TryEpisode(full, channel, week, out var episode)) episodes.Add(episode!);
                else errors.Add($"{id}: incomplete publication, duration, or title metadata.");
            }
            if (errors.Count > 0) return new(false, channel, [], "Could not read all public video metadata: " + string.Join("; ", errors.Take(3)));
            return new(true, channel, episodes.DistinctBy(e => e.Id).ToList());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        { return new(false, null, [], ex.Message); }
    }

    public static bool TryEpisode(JsonElement value, PodcastChannel fallback, PodcastWeek week, out PodcastEpisode? episode)
    {
        episode = null;
        var id = StringValue(value, "id");
        var channelId = StringValue(value, "channel_id") ?? fallback.Id;
        if (!PodcastId.IsValid(id ?? "") || !PodcastId.IsValid(channelId)) return false;
        if (IsExcluded(value)) return false;
        var timestamp = Timestamp(value);
        if (timestamp is null || !week.Contains(timestamp.Value)) return false;
        if (!value.TryGetProperty("duration", out var durationValue) || durationValue.ValueKind != JsonValueKind.Number ||
            !durationValue.TryGetDouble(out var seconds) || seconds < 0 || double.IsNaN(seconds)) return false;
        var title = StringValue(value, "title"); if (string.IsNullOrWhiteSpace(title)) return false;
        var channelName = StringValue(value, "channel") ?? fallback.Name;
        PodcastLimits.ValidateText(channelName, "Channel name", PodcastLimits.NameMax);
        PodcastLimits.ValidateText(title, "Episode title", PodcastLimits.TitleMax);
        episode = new PodcastEpisode(id!, channelId, channelName, title,
            timestamp.Value, TimeSpan.FromSeconds(seconds));
        return true;
    }

    private static bool CouldBeInWeek(JsonElement value, PodcastWeek week)
    {
        var approximate = Timestamp(value) ?? UploadDate(value);
        if (approximate is null) return true;
        var margin = TimeSpan.FromDays(2);
        var start = new DateTimeOffset(week.Monday.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - margin;
        var end = new DateTimeOffset(week.Sunday.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero) + margin;
        return approximate >= start && approximate <= end;
    }

    private static DateTimeOffset? UploadDate(JsonElement value)
    {
        if (!DateOnly.TryParseExact(StringValue(value, "upload_date"), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)) return null;
        return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private static DateTimeOffset? Timestamp(JsonElement value)
    {
        if (value.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        if (value.TryGetProperty("release_timestamp", out var release) && release.ValueKind == JsonValueKind.Number && release.TryGetInt64(out unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }

    private static bool IsExcluded(JsonElement value)
    {
        var status = (StringValue(value, "live_status") ?? "").ToLowerInvariant();
        var webpage = StringValue(value, "webpage_url") ?? "";
        var originalUrl = StringValue(value, "original_url") ?? "";
        return status is "is_live" or "is_upcoming" or "was_live" or "post_live" ||
            webpage.Contains("/shorts/", StringComparison.OrdinalIgnoreCase) || originalUrl.Contains("/shorts/", StringComparison.OrdinalIgnoreCase) ||
            value.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.True ||
            value.TryGetProperty("is_live", out var isLive) && isLive.ValueKind == JsonValueKind.True ||
            value.TryGetProperty("is_upcoming", out var upcoming) && upcoming.ValueKind == JsonValueKind.True;
    }

    private static string VideosUrl(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        var count = parts[0].StartsWith('@') ? 1 : 2;
        if (parts.Length < count || count == 2 && parts[0] is not ("channel" or "c" or "user") ||
            parts.Length > count + 1 || parts.Length == count + 1 && parts[^1] is not ("videos" or "featured" or "streams" or "shorts" or "about" or "playlists"))
            throw new InvalidOperationException("Enter a channel, handle, custom channel, or user URL.");
        var path = "/" + string.Join('/', parts.Take(count)) + "/videos";
        return new UriBuilder(uri) { Path = path }.Uri.ToString();
    }

    private static string? StringValue(JsonElement value, string name) => value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string SourceError(ProcessResult result) => string.IsNullOrWhiteSpace(result.StandardError) ?
        "yt-dlp could not check the public channel." : result.StandardError.Trim();
}

public sealed class PodcastMediaPreparer : IPodcastMediaPreparer
{
    private readonly PortableToolPaths tools;
    private readonly PodcastCache cache;
    private readonly IProcessRunner runner;
    public PodcastMediaPreparer(PortableToolPaths tools, PodcastCache cache, IProcessRunner? runner = null)
    { this.tools = tools; this.cache = cache; this.runner = runner ?? new ProcessRunner(); }

    public async Task<PodcastPreparedEpisode> PrepareAsync(PodcastEpisode episode, CancellationToken cancellation = default,
        IProgress<PodcastProgress>? progress = null)
    {
        var destination = cache.PathFor(episode.ChannelId, episode.Id);
        if (File.Exists(destination)) return Verify(episode, destination);
        tools.Require();
        var pending = Path.Combine(cache.Root, ".pending", episode.ChannelId, episode.Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending);
        try
        {
            var sourcePattern = Path.Combine(pending, "source.%(ext)s");
            progress?.Report(new($"Downloading {episode.Title}…", 0, 1));
            var download = await runner.RunAsync(PodcastToolArguments.Audio(tools, "https://www.youtube.com/watch?v=" + episode.Id, sourcePattern), cancellation);
            if (download.ExitCode != 0) throw new PodcastSourceException(string.IsNullOrWhiteSpace(download.StandardError) ? "Audio download failed." : download.StandardError.Trim());
            var source = Directory.EnumerateFiles(pending).FirstOrDefault(p => !p.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            if (source is null) throw new PodcastSourceException("yt-dlp did not produce an audio file.");
            var encoded = Path.Combine(pending, "encoded.mp3");
            var ffmpeg = await runner.RunAsync(PodcastToolArguments.FfmpegTranscode(tools, source, encoded, episode), cancellation);
            if (ffmpeg.ExitCode != 0 || !File.Exists(encoded)) throw new PodcastSourceException("FFmpeg could not create the podcast MP3.");
            var probe = await runner.RunAsync(PodcastToolArguments.Probe(tools, encoded), cancellation);
            if (probe.ExitCode != 0) throw new PodcastSourceException("ffprobe could not verify the podcast MP3.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyFlushHash(encoded, destination + ".tmp", out var hash, out var length);
            File.Move(destination + ".tmp", destination, true);
            return new(episode, destination, hash, length);
        }
        finally
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, true);
        }
    }

    private static PodcastPreparedEpisode Verify(PodcastEpisode episode, string path)
    {
        using var stream = File.OpenRead(path); var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        return new(episode, path, hash, stream.Length);
    }

    internal static void CopyFlushHash(string source, string destination, out string hash, out long length, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source); using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; int read; length = 0;
        while ((read = input.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); sha.AppendData(buffer, 0, read); length += read; }
        output.Flush(true); hash = Convert.ToHexString(sha.GetHashAndReset());
    }
}
