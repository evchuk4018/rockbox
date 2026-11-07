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
