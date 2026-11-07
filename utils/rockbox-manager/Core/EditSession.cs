namespace RockboxManager.Core;

public sealed class EditSession
{
    // Kept as a permitted legacy recovery target; new batches never write it.
    public const string PinsPath = ".rockbox/home-pins.txt";
    public PlayerDevice Device { get; }
    public List<PlaylistDraft> Playlists { get; } = [];
    public SyncBatch? PendingBatch { get; set; }
    public IEnumerable<PlaylistDraft> VisiblePlaylists => Playlists.Where(p => !p.Deleted);
    public PlaylistDraft Liked => Playlists.Single(p => p.IsLiked);
    public int ChangedCount => Playlists.Count(p => p.IsDirty);
    public bool IsDirty => ChangedCount > 0;

    private EditSession(PlayerDevice device)
    {
        Device = device;
    }

    public static EditSession Load(PlayerDevice device)
    {
        var session = new EditSession(device);
        var pairs = new Dictionary<string, PlaylistDraft>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in device.Volumes)
        {
            var catalog = Path.Combine(volume.Root, "Playlists");
            if (!Directory.Exists(catalog)) continue;
            foreach (var file in Directory.EnumerateFiles(catalog, "*", new EnumerationOptions {
                RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(volume.Root, file).Replace('\\', '/');
                var stem = Path.GetRelativePath(catalog, file)[..^extension.Length].Replace('\\', '/');
                // The firmware's special likes file lives on internal storage only.
                if (volume.Storage == Storage.Sd && stem.Equals("Liked Songs", StringComparison.OrdinalIgnoreCase)) continue;
                var bytes = File.ReadAllBytes(file);
                var entries = PlaylistCodec.Read(bytes, volume.Storage, relative, device.Codepage);
                var liked = volume.Storage == Storage.Internal && stem.Equals("Liked Songs", StringComparison.OrdinalIgnoreCase);
                // Only the required internal file is the special likes playlist.
                var pairKey = stem.Equals("Liked Songs", StringComparison.OrdinalIgnoreCase) ? $"{volume.Storage}:{stem}" : stem;
                if (!pairs.TryGetValue(pairKey, out var playlist))
                {
                    playlist = new(stem, entries, liked);
                    session.Playlists.Add(playlist);
                    pairs.Add(pairKey, playlist);
                }
                else if (playlist.Copies.ContainsKey(volume.Storage))
                    throw new IOException($"Both M3U and M3U8 versions of '{stem}' exist on {volume.Storage}. Rename one in Explorer before editing.");
                else if (!playlist.Entries.SequenceEqual(entries)) playlist.SetAlternative(entries);
                playlist.Copies.Add(volume.Storage, new(volume.Storage, relative, FileSnapshot.Digest(bytes)));
            }
        }
        if (!session.Playlists.Any(p => p.IsLiked)) session.Playlists.Insert(0, new("Liked Songs", [], liked: true));
        return session;
    }

    private void EnsureEditable(PlaylistDraft? playlist = null)
    {
        if (PendingBatch is not null) throw new InvalidOperationException("Retry the pending send or reload the player before making further edits.");
        if (playlist?.HasConflict == true) throw new InvalidOperationException("Choose which playlist copy to keep before editing it.");
    }

    public PlaylistDraft Create(string name, IEnumerable<SongPath>? entries = null)
    {
        EnsureEditable(); ValidateUniqueName(name, null);
        var playlist = new PlaylistDraft(name, entries ?? [], isNew: true);
        Playlists.Add(playlist);
        return playlist;
    }
    public void Rename(PlaylistDraft playlist, string name)
    {
        EnsureEditable(playlist);
        if (playlist.IsLiked) throw new InvalidOperationException("Liked Songs has a fixed name on your player.");
        var directory = Path.GetDirectoryName(playlist.Stem)?.Replace('\\', '/');
        var stem = string.IsNullOrEmpty(directory) ? name : directory + "/" + name;
        ValidateUniqueName(name, playlist, stem);
        playlist.Stem = stem;
    }
    private void ValidateUniqueName(string name, PlaylistDraft? except, string? stem = null)
    {
        PlaylistCodec.ValidateName(name);
        stem ??= name;
        if (stem.Equals("Liked Songs", StringComparison.OrdinalIgnoreCase) ||
            VisiblePlaylists.Any(p => p != except && p.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That playlist name is already in use. Choose another name.");
        PlaylistCodec.ValidatePath("/<microSD1>/Playlists/" + stem + ".m3u8");
    }
    public void Delete(PlaylistDraft playlist)
    {
        EnsureEditable(playlist);
        if (playlist.IsLiked) throw new InvalidOperationException("Liked Songs cannot be deleted. You can remove its entries.");
        playlist.Deleted = true;
    }
    public void ResolveConflict(PlaylistDraft playlist, bool useSd)
    {
        EnsureEditable();
        if (!playlist.HasConflict) return;
        if (useSd) playlist.Entries = [.. playlist.SdAlternative!];
        playlist.SdAlternative = null;
        playlist.ForceWrite = true;
    }
    public void Add(PlaylistDraft playlist, IEnumerable<SongPath> songs)
    {
        EnsureEditable(playlist);
        var existing = playlist.Entries.ToHashSet();
        playlist.Entries.AddRange(songs.Where(existing.Add));
    }
    public void Remove(PlaylistDraft playlist, IEnumerable<int> indexes)
    {
        EnsureEditable(playlist);
        foreach (var index in indexes.Distinct().OrderDescending()) playlist.Entries.RemoveAt(index);
    }
    public int[] Move(PlaylistDraft playlist, IEnumerable<int> indexes, int direction)
    {
        EnsureEditable(playlist);
        var selected = indexes.ToHashSet();
        foreach (var index in direction < 0 ? selected.Order().ToArray() : selected.OrderDescending().ToArray())
        {
            var target = index + direction;
            if (target < 0 || target >= playlist.Entries.Count || selected.Contains(target)) continue;
            (playlist.Entries[index], playlist.Entries[target]) = (playlist.Entries[target], playlist.Entries[index]);
            selected.Remove(index); selected.Add(target);
        }
        return selected.Order().ToArray();
    }
    public int[] MoveTo(PlaylistDraft playlist, IEnumerable<int> indexes, int target)
    {
        EnsureEditable(playlist);
        var selected = indexes.Distinct().Order().ToArray();
        var block = selected.Select(i => playlist.Entries[i]).ToList();
        target = Math.Clamp(target - selected.Count(i => i < target), 0, playlist.Entries.Count - selected.Length);
        foreach (var i in selected.Reverse()) playlist.Entries.RemoveAt(i);
        playlist.Entries.InsertRange(target, block);
        return Enumerable.Range(target, block.Count).ToArray();
    }
    public void SetLiked(IEnumerable<SongPath> songs, bool liked)
    {
        EnsureEditable(Liked);
        if (liked) Add(Liked, songs);
        else { var remove = songs.ToHashSet(); Liked.Entries.RemoveAll(remove.Contains); }
    }
    public void Discard()
    {
        EnsureEditable();
        Playlists.RemoveAll(p => p.IsNew);
        foreach (var playlist in Playlists) playlist.Reset();
    }
}
