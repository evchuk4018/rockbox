using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using RockboxManager.Core;

namespace RockboxManager;

public partial class MainWindow : Window
{
    private readonly DeviceDiscovery discovery;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource cancellation = new();
    private EditSession? session;
    private PlayerDevice? connected;
    private List<Track> library = [];
    private ListeningProfile listening = ListeningProfile.Empty("Connect your player to read listening scores.");
    private List<MusicSuggestion> suggestions = [];
    private string discoveryStatus = "Select Find suggestions to explore new music.";
    private CancellationTokenSource? discoveryCancellation;
    private Dictionary<SongPath, ArtworkResult> artwork = [];
    private CancellationTokenSource? artworkCancellation;
    private readonly PodcastManager podcastManager;
    private CancellationTokenSource? podcastCancellation;
    private bool podcastBusy, showingPodcasts;
    private PodcastWeek? requestedPodcastWeek;
    private bool preparingArtwork;
    private Dictionary<SongPath, Track> trackIndex = [];
    private ICollectionView? rows;
    private string currentKey = "library";
    private bool busy, polling, navigating, sending, closed;
    private Point dragStart;
    private int[] dragIndexes = [];
    private PlaylistDraft? CurrentPlaylist => currentKey.StartsWith("playlist:") && Guid.TryParse(currentKey[9..], out var id)
        ? session?.VisiblePlaylists.FirstOrDefault(p => p.Id == id) : currentKey == "liked" ? session?.Liked : null;
    private bool CanEdit => session is not null && connected is not null && !busy && session.PendingBatch is null;
    private bool NaturalOrder => rows is not null && rows.SortDescriptions.Count == 0 && SearchBox.Text.Length == 0 && StorageFilter.SelectedIndex == 0 && GenreFilter.SelectedIndex <= 0;

    public MainWindow(DeviceDiscovery discovery)
    {
        this.discovery = discovery;
        var podcastRoot = Path.Combine(App.DataRoot, "Podcasts");
        var podcastTools = PortableToolPaths.BesideExecutable();
        podcastManager = new PodcastManager(podcastRoot, new YtDlpPodcastSource(podcastTools),
            new PodcastMediaPreparer(podcastTools, new PodcastCache(Path.Combine(podcastRoot, "cache"))),
            new DeviceDiscoveryPodcastMountProvider(discovery));
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += async (_, _) => { timer.Start(); await Poll(); await PreparePodcastsAsync(transferIfConnected: true); };
        timer.Tick += async (_, _) => await Poll();
        RefreshPodcastUi();
    }

    private async Task Poll()
    {
        if (polling || busy || closed) return;
        polling = true;
        try
        {
            var devices = await Task.Run(discovery.Find);
            if (closed) return;
            if (session is null)
            {
                if (devices.Count == 1) await LoadDevice(devices[0]);
                else
                {
                    DevicePicker.Visibility = devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                    DevicePicker.ItemsSource = devices;
                    ConnectionTitle.Text = devices.Count > 1 ? "Choose your player" : "Connect your Clip+";
                    ConnectionText.Text = "Connect your Rockbox Clip+ and its SD card through USB. The app will detect them automatically.";
                    ScanProgress.Visibility = Visibility.Collapsed;
                    DeviceTitle.Text = "No player selected";
                }
            }
            else
            {
                var found = devices.FirstOrDefault(d => d.Id == session.Device.Id);
                var changed = ConnectionKey(connected) != ConnectionKey(found);
                connected = found;
                if (changed)
                {
                    discoveryCancellation?.Cancel();
                    if (found is not null && !session.IsDirty && session.PendingBatch is null) await LoadDevice(found);
                    else
                    {
                        StatusText.Text = found is null ? "Player disconnected. Your draft is retained; reconnect the same player to send it." :
                            found.Sd is null ? "Internal storage connected. Reconnect the original SD card to send mirrored playlist edits." :
                            "Player reconnected. Your pending changes are ready to send.";
                        UpdateChrome();
                        if (found is not null) _ = PreparePodcastsAsync(transferIfConnected: true);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Management.ManagementException)
        {
            StatusText.Text = "Could not read the player: " + ex.Message;
            connected = null;
            UpdateChrome();
        }
        finally { polling = false; }
        if (requestedPodcastWeek != podcastManager.CurrentWeek)
            _ = PreparePodcastsAsync(transferIfConnected: connected is not null);
    }

    private static string ConnectionKey(PlayerDevice? device) => device is null ? "" :
        device.Id + "|" + device.Internal.Root + "|" + device.Sd?.Id + "|" + device.Sd?.Root;

    private async Task LoadDevice(PlayerDevice device, bool findRecovery = true)
    {
        artworkCancellation?.Cancel();
        discoveryCancellation?.Cancel();
        busy = true; UpdateChrome();
        ConnectionPanel.Visibility = Visibility.Visible;
        ConnectionTitle.Text = "Reading your player";
        ConnectionText.Text = "Loading playlists and music tags…";
        ScanProgress.Visibility = Visibility.Visible;
        DevicePicker.Visibility = Visibility.Collapsed;
        var progress = new Progress<string>(text => { ConnectionText.Text = text; StatusText.Text = text; });
        try
        {
            var loaded = await Task.Run(() => (Session: EditSession.Load(device),
                Tracks: PlayerLibrary.Scan(device, progress, cancellation.Token)), cancellation.Token);
            if (closed) return;
            session = loaded.Session; library = loaded.Tracks;
            listening = await Task.Run(() => ListeningProfile.Load(device, library), cancellation.Token);
            suggestions = []; discoveryStatus = "Select Find suggestions to explore new music.";
            UpdateDiscovery();
            artwork = [];
            GenreFilter.ItemsSource = new[] { "All genres" }.Concat(library.Select(t => t.Genre).Where(g => g.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)).ToList();
            GenreFilter.SelectedIndex = 0;
            trackIndex = library.ToDictionary(t => t.Path);
            connected = device;
            if (findRecovery && Directory.Exists(App.BackupRoot))
            {
                foreach (var manifest in Directory.EnumerateFiles(App.BackupRoot, "operation.json", SearchOption.AllDirectories).OrderDescending())
                {
                    try
                    {
                        var batch = JsonSerializer.Deserialize<SyncBatch>(File.ReadAllText(manifest));
                        if (batch is { Started: true, Complete: false } && batch.DeviceId == device.Id)
                        { batch.BackupFolder = Path.GetDirectoryName(manifest)!; session.PendingBatch = batch; break; }
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
                }
            }
            ConnectionPanel.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            RefreshNavigation();
            StatusText.Text = session.PendingBatch is null
                ? "Changes stay on this PC until you send them. Removing a playlist entry keeps the audio file."
                : "An interrupted send was found. Retry Send to finish it, or Reload player to inspect the current files. Backups are retained.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ConnectionTitle.Text = "Could not read the library";
            ConnectionText.Text = ex.Message + " Reconnect the player and click Refresh.";
            ScanProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = ex.Message;
        }
        finally { busy = false; if (!closed) UpdateChrome(); }
        if (!closed && session?.PendingBatch is null && connected is not null) _ = PrepareArtwork();
        if (!closed && connected is not null) _ = PreparePodcastsAsync(transferIfConnected: true);
    }

    private void RefreshNavigation(string? selectKey = null)
    {
        if (session is null) return;
        var target = AddTarget.SelectedItem as PlaylistDraft;
        navigating = true;
        var nav = new List<NavItem> { new("library", "♫  All music", $"{library.Count:N0} songs · combined library"),
            new("artists", "Artists", "Browse by album artist"), new("albums", "Albums", "Browse in disc / track order"),
            new("discover", "Discover", "New music from your listening scores"),
            new("liked", "♥  " + session.Liked.Label, $"{session.Liked.Entries.Count:N0} liked songs") };
        nav.AddRange(session.VisiblePlaylists.Where(p => !p.IsLiked).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new NavItem("playlist:" + p.Id, p.Label, $"{p.Entries.Count:N0} songs · {p.CopyLabel}")));
        currentKey = selectKey ?? currentKey;
        Navigation.ItemsSource = nav;
        Navigation.SelectedItem = nav.FirstOrDefault(n => n.Key == currentKey) ?? nav[0];
        currentKey = ((NavItem)Navigation.SelectedItem).Key;
        AddTarget.ItemsSource = session.VisiblePlaylists.Where(p => !p.HasConflict).OrderBy(p => p.Name).ToList();
        AddTarget.SelectedItem = target is not null && !target.Deleted ? target : session.VisiblePlaylists.FirstOrDefault(p => !p.IsLiked && !p.HasConflict);
        navigating = false;
        SetCatalog();
        ShowView();
    }

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (navigating || Navigation.SelectedItem is not NavItem nav) return;
        showingPodcasts = false; PodcastPanel.Visibility = Visibility.Collapsed;
        currentKey = nav.Key;
        SearchBox.Text = ""; StorageFilter.SelectedIndex = 0;
        GenreFilter.SelectedIndex = 0;
        SetCatalog();
        ShowView();
    }
    private void SetCatalog()
    {
        var selected = (CatalogPicker.SelectedItem as CatalogItem)?.Key;
        navigating = true;
        CatalogPanel.Visibility = currentKey is "artists" or "albums" ? Visibility.Visible : Visibility.Collapsed;
        if (currentKey is not ("artists" or "albums"))
        { CatalogPicker.ItemsSource = null; navigating = false; return; }
        CatalogLabel.Text = currentKey == "artists" ? "ARTIST" : "ALBUM";
        CatalogPicker.ItemsSource = currentKey == "artists"
            ? library.Select(t => t.GroupArtist).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase).Select(a => new CatalogItem(a, a.Length == 0 ? "Unknown artist" : a)).ToList()
            : LibraryViews.Albums(library).Select(a => new CatalogItem(a.Key, $"{a.Name} — {a.Artist}")).ToList();
        CatalogPicker.SelectedItem = CatalogPicker.Items.Cast<CatalogItem>().FirstOrDefault(c => c.Key == selected) ??
            (CatalogPicker.Items.Count > 0 ? CatalogPicker.Items[0] : null);
        navigating = false;
    }
    private void CatalogChanged(object sender, SelectionChangedEventArgs e) { if (!navigating && session is not null) ShowView(); }
    private void ShowView()
    {
        if (session is null) return;
        if (showingPodcasts) return;
        var discover = currentKey == "discover";
        DiscoveryPanel.Visibility = discover ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = discover ? Visibility.Collapsed : Visibility.Visible;
        if (discover) { UpdateDiscovery(); UpdateChrome(); return; }
        var playlist = CurrentPlaylist;
        ViewTitle.Text = playlist?.Name ?? (currentKey == "artists" ? "Artists" : currentKey == "albums" ? "Albums" : "All music");
        ViewCaption.Text = playlist is null ? "Search every song on your player. Select songs to build a playlist or update your likes." :
            playlist.IsLiked ? "The hearts on your player use this playlist. Removing an entry unlikes the song." :
            $"{playlist.Entries.Count:N0} songs · {playlist.CopyLabel} · Changes will be sent to both storage locations.";
        PlaylistActions.Visibility = playlist is { IsLiked: false } ? Visibility.Visible : Visibility.Collapsed;
        ConflictBanner.Visibility = playlist?.HasConflict == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = SongActions.Visibility = Songs.Visibility = Visibility.Visible;
        AddTarget.Visibility = AddButton.Visibility = playlist is null ? Visibility.Visible : Visibility.Collapsed;
        BrowseButton.Visibility = RemoveButton.Visibility = UpButton.Visibility = DownButton.Visibility = playlist is null ? Visibility.Collapsed : Visibility.Visible;
        var liked = session.Liked.Entries.ToHashSet();
        var shown = playlist is null ? library : playlist.Entries.Select(path => trackIndex.GetValueOrDefault(path) ??
            new Track(path, Path.GetFileNameWithoutExtension(path.RelativePath), "", "", "", TimeSpan.Zero, false)).ToList();
        if (playlist is null && CatalogPicker.SelectedItem is CatalogItem catalog)
        {
            if (currentKey == "artists") shown = shown.Where(t => t.GroupArtist.Equals(catalog.Key, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title).ToList();
            if (currentKey == "albums") shown = LibraryViews.Albums(shown).FirstOrDefault(a => a.Key == catalog.Key)?.Tracks ?? [];
        }
        rows = new ListCollectionView(shown.Select((track, i) => new TrackRow(i, track, liked.Contains(track.Path), listening.Scores.GetValueOrDefault(track.Path))).ToList());
            rows.Filter = RowMatches;
            Songs.ItemsSource = rows;
        UpdateRowCount();
        UpdateChrome();
    }

    private void PodcastsClick(object sender, RoutedEventArgs e)
    {
        showingPodcasts = !showingPodcasts;
        if (showingPodcasts)
        {
            Editor.Visibility = DiscoveryPanel.Visibility = ConnectionPanel.Visibility = Visibility.Collapsed;
            PodcastPanel.Visibility = Visibility.Visible;
            RefreshPodcastUi();
        }
        else if (session is not null)
        {
            PodcastPanel.Visibility = Visibility.Collapsed;
            ShowView();
        }
        else
        {
            PodcastPanel.Visibility = Visibility.Collapsed;
            ConnectionPanel.Visibility = Visibility.Visible;
        }
    }
    private bool RowMatches(object obj)
    {
        var track = ((TrackRow)obj).Track;
        if (StorageFilter.SelectedIndex == 1 && track.Path.Storage != Storage.Internal ||
            StorageFilter.SelectedIndex == 2 && track.Path.Storage != Storage.Sd) return false;
        return LibraryViews.Matches(track, SearchBox.Text, GenreFilter.SelectedIndex > 0 ? GenreFilter.SelectedItem as string : null);
    }
    private void UpdateDiscovery()
    {
        ScoreSummary.Text = listening.Status;
        GenreScores.ItemsSource = listening.Genres.Take(5).ToList();
        Suggestions.ItemsSource = suggestions;
        DiscoveryStatus.Text = listening.Genres.Count == 0
            ? "Suggestions need scored songs with genre tags. Play music through Home genre autoplay, then reload scores."
            : discoveryStatus;
    }
    private async void ReloadScoresClick(object sender, RoutedEventArgs e)
    {
        if (busy || connected is null || discoveryCancellation is not null) return;
        busy = true; UpdateChrome();
        try
        {
            listening = await Task.Run(() => ListeningProfile.Load(connected, library), cancellation.Token);
            suggestions = []; discoveryStatus = "Scores reloaded. Select Find suggestions for fresh picks.";
            if (closed) return;
            ShowView();
        }
        catch (OperationCanceledException) { }
        finally { busy = false; if (!closed) UpdateChrome(); }
    }
    private async void FindSuggestionsClick(object sender, RoutedEventArgs e)
    {
        if (discoveryCancellation is not null) { discoveryCancellation.Cancel(); return; }
        if (busy || listening.Genres.Count == 0 || session is null) return;
        var editing = session;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        discoveryCancellation = operation; UpdateChrome();
        var progress = new Progress<string>(text =>
        { if (session == editing && !operation.IsCancellationRequested) { discoveryStatus = text; DiscoveryStatus.Text = text; } });
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var result = await new DiscoveryService(http).Find(listening.Genres, library, progress, operation.Token);
            if (session != editing || operation.IsCancellationRequested) return;
            var retained = result.Suggestions.Count == 0 && suggestions.Count > 0;
            if (!retained) suggestions = result.Suggestions;
            discoveryStatus = (retained ? "Previous suggestions kept. " : "") + result.Status;
        }
        catch (OperationCanceledException) { if (session == editing) discoveryStatus = "Search canceled. Select Find suggestions to try again."; }
        finally
        {
            if (discoveryCancellation == operation)
            { discoveryCancellation = null; if (!closed) { UpdateDiscovery(); UpdateChrome(); } }
        }
    }
    private void SuggestionNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == "https" && e.Uri.Host == "musicbrainz.org")
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception ex) { ShowError("Could not open your browser: " + ex.Message); }
        e.Handled = true;
    }
    private void CopySuggestionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MusicSuggestion suggestion) return;
        try { Clipboard.SetText($"{suggestion.Artist} — {suggestion.Title} ({suggestion.Kind})\n{suggestion.Url}"); }
        catch (ExternalException) { ShowError("The clipboard is busy. Try copying again."); }
    }
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (SearchBox is null || StorageFilter is null || rows is null) return;
        rows.Refresh(); UpdateRowCount(); UpdateChrome();
    }
    private void ClearFiltersClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = ""; StorageFilter.SelectedIndex = 0; GenreFilter.SelectedIndex = 0;
        rows?.SortDescriptions.Clear();
        foreach (var column in Songs.Columns) column.SortDirection = null;
        UpdateChrome();
    }
    private void SongsSorting(object sender, DataGridSortingEventArgs e) => Dispatcher.BeginInvoke(UpdateChrome, DispatcherPriority.Background);
    private void SongsSelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateRowCount(); UpdateChrome(); UpdateCoverPreview(); }
    private void UpdateCoverPreview()
    {
        CoverPreview.Source = null;
        if (Songs.SelectedItem is not TrackRow row) { CoverStatus.Text = "Select a song to preview its cover."; return; }
        CoverPreview.Source = ArtworkDecoder.Preview(CoverConverter.Placeholder());
        if (!artwork.TryGetValue(row.Track.Path, out var cover)) { CoverStatus.Text = preparingArtwork ? "Preparing this song's artwork…" : "No prepared cover."; return; }
        if (cover.Bitmap is not null) CoverPreview.Source = ArtworkDecoder.Preview(cover.Bitmap);
        CoverStatus.Text = $"{cover.Status} · {cover.SourceKind} · {row.Track.Album}" + (cover.Message.Length > 0 ? " · " + cover.Message : "");
    }
    private async Task PrepareArtwork(bool retry = false)
    {
        if (session is null || connected is null || session.PendingBatch is not null) return;
        var editing = session;
        var sourceTracks = library;
        artworkCancellation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        artworkCancellation = operation; preparingArtwork = true; ArtworkRetry.IsEnabled = false;
        var progress = new Progress<string>(text => { if (session == editing && !operation.IsCancellationRequested) ArtworkSummary.Text = text; });
        try
        {
            var prepared = await Task.Run(async () =>
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var service = new ArtworkService(Path.Combine(App.DataRoot, "Artwork"), ArtworkDecoder.Decode, http);
                return await service.Prepare(editing.Device, sourceTracks, progress, operation.Token, retry);
            }, operation.Token);
            if (session != editing || operation.IsCancellationRequested || editing.PendingBatch is not null) return;
            artwork = prepared.ToDictionary(a => a.Track);
            editing.ArtworkChanges.Clear();
            editing.ArtworkChanges.AddRange(prepared.Where(a => a.Change is not null).Select(a => a.Change!)
                .DistinctBy(c => (c.Storage, c.RelativePath)));
            ArtworkSummary.Text = $"Artwork: {prepared.Count(a => a.Bitmap is not null):N0} ready / installed · " +
                $"{prepared.Count(a => a.Status == "Missing"):N0} unavailable · {prepared.Count(a => a.Status is not ("Ready" or "Installed" or "Missing")):N0} need retry / attention.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        { if (session == editing) ArtworkSummary.Text = "Artwork preparation failed: " + ex.Message; }
        finally
        {
            if (artworkCancellation == operation)
            { artworkCancellation = null; preparingArtwork = false; ArtworkRetry.IsEnabled = !busy; UpdateCoverPreview(); UpdateChrome(); }
        }
    }
    private async void ArtworkRetryClick(object sender, RoutedEventArgs e) { if (!busy && !preparingArtwork) await PrepareArtwork(retry: true); }

    private void RefreshPodcastUi()
    {
        if (PodcastSubscriptions is null) return;
        PodcastSubscriptions.ItemsSource = podcastManager.Subscriptions.Subscriptions.ToList();
        var prepared = podcastManager.Prepared;
        PodcastWeekLabel.Text = $"Latest completed America/New_York week: {podcastManager.CurrentWeek.Label}";
        PodcastCount.Text = prepared?.Catalog is { } catalog
            ? $"{catalog.Episodes.Count:N0} episode{(catalog.Episodes.Count == 1 ? "" : "s")}"
            : "No prepared drop";
        PodcastEpisodes.ItemsSource = prepared?.Catalog?.Episodes.Select(e => new PodcastRow(e)).ToList() ?? [];
        if (prepared is not null && !podcastBusy) PodcastStatus.Text = prepared.Status;
        PodcastCancel.IsEnabled = podcastBusy;
        PodcastRetry.IsEnabled = !podcastBusy;
    }

    private async Task PreparePodcastsAsync(bool transferIfConnected)
    {
        if (podcastBusy || closed) return;
        podcastBusy = true;
        requestedPodcastWeek = podcastManager.CurrentWeek;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        podcastCancellation = operation;
        PodcastRetry.IsEnabled = false; PodcastCancel.IsEnabled = true;
        var progress = new Progress<PodcastProgress>(value =>
        {
            PodcastStatus.Text = value.Message;
            PodcastProgress.IsIndeterminate = value.Indeterminate || value.Total == 0;
            PodcastProgress.Maximum = Math.Max(1, value.Total);
            PodcastProgress.Value = Math.Min(value.Completed, PodcastProgress.Maximum);
        });
        try
        {
            var result = await Task.Run(() => podcastManager.PrepareAsync(operation.Token, progress), operation.Token);
            if (result.Success && transferIfConnected && connected is not null)
                await PublishPodcastsAsync(connected, operation.Token, progress);
            else if (!result.Success)
            {
                var podcastRoot = Path.Combine(App.DataRoot, "Podcasts");
                if (transferIfConnected && connected is not null && PodcastTransferService.HasPendingTransfer(podcastRoot))
                    await PublishPodcastsAsync(connected, operation.Token, progress);
                else PodcastStatus.Text = result.Status;
            }
        }
        catch (OperationCanceledException) { PodcastStatus.Text = "Podcast preparation canceled. Retry when ready."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PodcastSourceException)
        { PodcastStatus.Text = "Podcast preparation failed: " + ex.Message; }
        finally
        {
            if (podcastCancellation == operation) podcastCancellation = null;
            var status = PodcastStatus.Text;
            podcastBusy = false; RefreshPodcastUi(); PodcastStatus.Text = status; UpdateChrome();
        }
    }

    private async Task PublishPodcastsAsync(PlayerDevice device, CancellationToken cancellation, IProgress<PodcastProgress> progress)
    {
        PodcastStatus.Text = "Sending prepared podcasts…";
        var result = await Task.Run(() => podcastManager.PublishIfDueAsync(device, cancellation, progress), cancellation);
        PodcastStatus.Text = result.Message;
        if (!result.Success) ShowError(result.Message);
    }

    private async void PodcastPrepareClick(object sender, RoutedEventArgs e) => await PreparePodcastsAsync(transferIfConnected: true);

    private async void PodcastAddClick(object sender, RoutedEventArgs e)
    {
        if (podcastBusy) return;
        podcastBusy = true; PodcastCancel.IsEnabled = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        podcastCancellation = operation;
        var added = false;
        try
        {
            PodcastStatus.Text = "Checking public channel…";
            await podcastManager.AddAsync(PodcastUrl.Text, operation.Token, new Progress<PodcastProgress>(p => PodcastStatus.Text = p.Message));
            added = true; PodcastUrl.Text = ""; RefreshPodcastUi();
        }
        catch (OperationCanceledException) { PodcastStatus.Text = "Adding channel canceled."; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PodcastSourceException)
        { PodcastStatus.Text = "Could not add channel: " + ex.Message; }
        finally
        {
            podcastCancellation = null; podcastBusy = false; RefreshPodcastUi();
        }
        if (added) await PreparePodcastsAsync(transferIfConnected: true);
    }

    private async void PodcastRemoveClick(object sender, RoutedEventArgs e)
    {
        if (podcastBusy || (sender as FrameworkElement)?.DataContext is not PodcastSubscription subscription) return;
        podcastManager.Remove(subscription.ChannelId); RefreshPodcastUi();
        await PreparePodcastsAsync(transferIfConnected: true);
    }

    private void PodcastCancelClick(object sender, RoutedEventArgs e) => podcastCancellation?.Cancel();
    private async void PodcastRetryClick(object sender, RoutedEventArgs e) => await PreparePodcastsAsync(transferIfConnected: true);

    private void UpdateRowCount()
    {
        if (rows is null) return;
        RowCount.Text = $"{rows.Cast<object>().Count():N0} songs shown · {Songs.SelectedItems.Count:N0} selected" +
            (CurrentPlaylist is not null && !NaturalOrder ? " · Clear filters / sort to rearrange" : "");
    }
    private void UpdateChrome()
    {
        if (SendButton is null) return;
        if (connected is not null)
        {
            DeviceTitle.Text = connected.Name;
            static string Space(PlayerVolume v) => v.FreeBytes == long.MaxValue ? "test folders" : $"{v.FreeBytes / 1073741824d:0.0} GB free";
            DeviceDetails.Text = $"Internal {Space(connected.Internal)}  ·  " + (connected.Sd is null ? "SD disconnected" : $"SD {Space(connected.Sd)}");
        }
        else if (session is not null) { DeviceTitle.Text = "Player disconnected"; DeviceDetails.Text = "Reconnect the same Clip+ to send changes"; }
        NewButton.IsEnabled = CanEdit;
        FindSuggestionsButton.Content = discoveryCancellation is null ? "Find suggestions" : "Cancel search";
        FindSuggestionsButton.IsEnabled = discoveryCancellation is not null || !busy && session is not null && listening.Genres.Count > 0;
        ReloadScoresButton.IsEnabled = !busy && connected is not null && discoveryCancellation is null;
        PlaylistActions.IsEnabled = SongActions.IsEnabled = ConflictBanner.IsEnabled = CanEdit;
        if (CurrentPlaylist is { } playlist)
        {
            RemoveButton.IsEnabled = !playlist.HasConflict && Songs.SelectedItems.Count > 0;
            UpButton.IsEnabled = DownButton.IsEnabled = !playlist.HasConflict && NaturalOrder && Songs.SelectedItems.Count > 0;
        }
        var batch = session?.PendingBatch;
        var needsSd = batch?.RequiresSd ?? (session?.Playlists.Any(p => p.IsDirty && !p.IsLiked) == true ||
            session?.ArtworkChanges.Any(c => c.Storage == Storage.Sd || c.ArtworkTrack?.Storage == Storage.Sd) == true);
        ArtworkRetry.IsEnabled = CanEdit && !preparingArtwork;
        var sdId = batch?.SdId ?? session?.Device.Sd?.Id;
        var storageReady = connected is not null && (!needsSd || connected.Sd is not null && connected.Sd.Id == sdId);
        SendButton.IsEnabled = !busy && storageReady && (session?.IsDirty == true || batch is not null);
        SendButton.Content = batch is null ? "Send to Player →" : "Retry Send →";
        DiscardButton.Content = batch is null ? "Discard changes" : "Reload player";
        DiscardButton.IsEnabled = !busy && (session?.IsDirty == true || batch is not null && connected is not null);
        PendingTitle.Text = busy ? sending ? "Sending changes…" : "Reading library…" : batch is not null
            ? $"Send pending · {batch.CompletedCount}/{batch.Changes.Count} files verified" : session?.IsDirty == true
            ? $"{session.ChangedCount} pending change{(session.ChangedCount == 1 ? "" : "s")}" : "All changes sent";
        if (!busy && session?.IsDirty == true && needsSd && !storageReady)
            StatusText.Text = "Your draft is retained. Connect the original player and SD card to send both playlist copies.";
    }

    private int[] SelectedIndexes() => Songs.SelectedItems.Cast<TrackRow>().Select(r => r.Index).Order().ToArray();
    private SongPath[] SelectedSongs() => Songs.SelectedItems.Cast<TrackRow>().Select(r => r.Track.Path).ToArray();
    private void Edit(Action action, string? key = null)
    {
        if (!CanEdit) return;
        try { action(); RefreshNavigation(key); StatusText.Text = "Draft updated. Click Send to Player when you're ready."; }
        catch (InvalidOperationException ex) { ShowError(ex.Message); }
    }
    private void NewClick(object sender, RoutedEventArgs e)
    {
        var name = NameDialog.Ask(this, "New playlist");
        if (name is null) return;
        Edit(() => { var created = session!.Create(name); currentKey = "playlist:" + created.Id; });
    }
    private void RenameClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPlaylist is not { } p) return;
        var name = NameDialog.Ask(this, "Rename playlist", p.Name);
        if (name is not null) Edit(() => session!.Rename(p, name));
    }
    private void DuplicateClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPlaylist is not { } p) return;
        var name = NameDialog.Ask(this, "Duplicate playlist", p.Name + " copy");
        if (name is not null) Edit(() => { var copy = session!.Create(name, p.Entries); currentKey = "playlist:" + copy.Id; });
    }
    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPlaylist is { } p && MessageBox.Show(this, $"Delete '{p.Name}' from both playlist catalogs when changes are sent? Its music files will remain.", "Delete playlist", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Edit(() => session!.Delete(p), "library");
    }
    private void AddClick(object sender, RoutedEventArgs e)
    {
        if (AddTarget.SelectedItem is PlaylistDraft p) { var songs = SelectedSongs(); Edit(() => session!.Add(p, songs)); }
    }
    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var target = CurrentPlaylist;
        RefreshNavigation("library"); AddTarget.SelectedItem = target;
        SearchBox.Focus();
    }
    private void RemoveClick(object sender, RoutedEventArgs e)
    { if (CurrentPlaylist is { } p) { var indexes = SelectedIndexes(); Edit(() => session!.Remove(p, indexes)); } }
    private void LikeClick(object sender, RoutedEventArgs e) { var songs = SelectedSongs(); Edit(() => session!.SetLiked(songs, true)); }
    private void UnlikeClick(object sender, RoutedEventArgs e) { var songs = SelectedSongs(); Edit(() => session!.SetLiked(songs, false)); }
    private void UseInternalClick(object sender, RoutedEventArgs e) { if (CurrentPlaylist is { } p) Edit(() => session!.ResolveConflict(p, false)); }
    private void UseSdClick(object sender, RoutedEventArgs e) { if (CurrentPlaylist is { } p) Edit(() => session!.ResolveConflict(p, true)); }
    private void UpClick(object sender, RoutedEventArgs e) => MoveSongs(-1);
    private void DownClick(object sender, RoutedEventArgs e) => MoveSongs(1);
    private void MoveSongs(int direction)
    {
        if (!NaturalOrder || CurrentPlaylist is not { } p) return;
        int[] selected = [];
        Edit(() => selected = session!.Move(p, SelectedIndexes(), direction));
        Reselect(selected);
    }
    private void Reselect(int[] indexes)
    {
        var wanted = indexes.ToHashSet();
        foreach (TrackRow row in Songs.Items) if (wanted.Contains(row.Index)) Songs.SelectedItems.Add(row);
        if (Songs.SelectedItem is not null) Songs.ScrollIntoView(Songs.SelectedItem);
    }
    private void SongsMouseDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(Songs); dragIndexes = []; }
    private void SongsMouseMove(object sender, MouseEventArgs e)
    {
        if (!CanEdit || CurrentPlaylist is null || !NaturalOrder || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(Songs);
        if (Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragIndexes = SelectedIndexes();
        if (dragIndexes.Length > 0) DragDrop.DoDragDrop(Songs, new DataObject("RockboxRows", true), DragDropEffects.Move);
    }
    private void SongsDrop(object sender, DragEventArgs e)
    {
        if (!CanEdit || !NaturalOrder || CurrentPlaylist is not { } p || !e.Data.GetDataPresent("RockboxRows") || dragIndexes.Length == 0) return;
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit is not DataGridRow) hit = VisualTreeHelper.GetParent(hit);
        var target = (hit as DataGridRow)?.Item is TrackRow row ? row.Index : p.Entries.Count;
        int[] selected = [];
        Edit(() => selected = session!.MoveTo(p, dragIndexes, target));
        Reselect(selected); e.Handled = true;
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (session?.IsDirty == true || session?.PendingBatch is not null) { await DiscardOrReload(); return; }
        if (connected is not null) await LoadDevice(connected); else await Poll();
    }
    private async void DiscardClick(object sender, RoutedEventArgs e) => await DiscardOrReload();
    private async Task DiscardOrReload()
    {
        if (session is null || busy) return;
        if (session.PendingBatch is not null)
        {
            if (connected is null) return;
            if (MessageBox.Show(this, "Reload current player files and stop retrying this send? Files already sent stay applied. The operation and original files remain in Backups for recovery.", "Reload player", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            // Mark an explicitly abandoned operation so it is not offered again at startup.
            var manifest = Path.Combine(session.PendingBatch.BackupFolder, "operation.json");
            if (File.Exists(manifest)) File.Move(manifest, Path.Combine(session.PendingBatch.BackupFolder, "abandoned-operation.json"), true);
            await LoadDevice(connected, findRecovery: false);
        }
        else if (MessageBox.Show(this, "Discard all unsent playlist, like and artwork changes?", "Discard changes", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        { artworkCancellation?.Cancel(); session.Discard(); RefreshNavigation(); StatusText.Text = "Unsent changes discarded."; }
    }
    private async void DevicePicked(object sender, SelectionChangedEventArgs e)
    { if (!busy && session is null && DevicePicker.SelectedItem is PlayerDevice device) await LoadDevice(device); }

    private async void SendClick(object sender, RoutedEventArgs e)
    {
        if (session is null || !SendButton.IsEnabled) return;
        artworkCancellation?.Cancel();
        busy = sending = true; UpdateChrome();
        try
        {
            var detected = await Task.Run(() => discovery.Find().FirstOrDefault(d => d.Id == session.Device.Id));
            if (detected is null) throw new IOException("Reconnect the original player before sending.");
            connected = detected;
            session.PendingBatch ??= SyncBatch.Build(session, App.BackupRoot);
            var progress = new Progress<string>(text => StatusText.Text = text);
            var result = await Task.Run(() => new SyncEngine().Execute(session.PendingBatch, () => discovery.Verify(detected), progress));
            if (result.Success)
            {
                await LoadDevice(detected);
                StatusText.Text = result.Message;
            }
            else { StatusText.Text = result.Message; ShowError(result.Message); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Management.ManagementException)
        { StatusText.Text = ex.Message; ShowError(ex.Message); }
        finally { busy = sending = false; UpdateChrome(); }
    }
    private void BackupsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.BackupRoot);
        Process.Start(new ProcessStartInfo(App.BackupRoot) { UseShellExecute = true });
    }
    private void ShowError(string text) => MessageBox.Show(this, text, "Rockbox Manager", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (sending) { e.Cancel = true; ShowError("Wait for the current send to finish before closing."); return; }
        if (session?.IsDirty == true || session?.PendingBatch is not null)
        {
            var text = session.PendingBatch is { Started: true }
                ? "Close now? The interrupted send is saved and can be resumed next time."
                : "Close and discard your unsent changes?";
            if (MessageBox.Show(this, text, "Unsent changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
        }
        closed = true; timer.Stop(); cancellation.Cancel(); artworkCancellation?.Cancel(); discoveryCancellation?.Cancel(); podcastCancellation?.Cancel();
    }
    private sealed record CatalogItem(string Key, string Label);
    private sealed record NavItem(string Key, string Label, string Caption);
    private sealed record PodcastRow(PodcastEpisode Episode)
    {
        public string Title => Episode.Title;
        public string ChannelName => Episode.ChannelName;
        public string PublishedLabel => Episode.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz");
        public string DurationLabel => Episode.Duration == TimeSpan.Zero ? "—" : Episode.Duration.ToString(Episode.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    }
    private sealed record TrackRow(int Index, Track Track, bool Liked, double Points)
    {
        public int Number => Index + 1;
        public string Heart => Liked ? "♥" : "";
        public string Location => Track.Available ? Track.StorageLabel : "Unavailable";
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
