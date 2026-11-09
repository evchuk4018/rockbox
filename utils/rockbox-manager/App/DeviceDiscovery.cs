using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using RockboxManager.Core;

namespace RockboxManager;

public sealed class DeviceDiscovery(string? fixtureRoot = null)
{
    public bool IsFixture => fixtureRoot is not null;
    public List<PlayerDevice> Find()
    {
        if (fixtureRoot is not null)
        {
            var internalRoot = Path.Combine(Path.GetFullPath(fixtureRoot), "Internal");
            var sdRoot = Path.Combine(Path.GetFullPath(fixtureRoot), "SD");
            if (!Directory.Exists(internalRoot)) return [];
            return [new("fixture-player", "Test Clip+ · local folders", new(Storage.Internal, "fixture-internal", internalRoot, long.MaxValue),
                Directory.Exists(sdRoot) ? new(Storage.Sd, "fixture-sd", sdRoot, long.MaxValue) : null)];
        }
        var volumes = new List<UsbVolume>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, VolumeSerialNumber, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 2");
        using var disks = searcher.Get();
        foreach (ManagementObject disk in disks)
        using (disk)
        {
            var root = disk["DeviceID"] + "\\";
            using var partitions = disk.GetRelated("Win32_DiskPartition");
            foreach (ManagementObject partition in partitions)
            using (partition)
            using (var physicalDisks = partition.GetRelated("Win32_DiskDrive"))
            foreach (ManagementObject physical in physicalDisks)
            using (physical)
            {
                var pnp = physical["PNPDeviceID"]?.ToString() ?? "";
                if (!pnp.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)) continue;
                var serial = pnp.Split('\\').Last();
                var lun = serial.LastIndexOf('&');
                if (lun > 0) serial = serial[..lun];
                volumes.Add(new(root, disk["VolumeSerialNumber"]?.ToString() ?? "", serial,
                    physical["Model"]?.ToString() ?? "", Convert.ToInt64(disk["FreeSpace"] ?? 0)));
            }
        }
        var devices = new List<PlayerDevice>();
        foreach (var volume in volumes)
        {
            var info = Path.Combine(volume.Root, ".rockbox", "rockbox-info.txt");
            if (!File.Exists(info) || ReadSetting(info, "Target") != "sansaclipplus") continue;
            var product = ReadSetting(Path.Combine(volume.Root, "version.sdk"), "Product");
            if (product.Length > 0 && product != "Clip+") continue;
            var sdCandidates = volumes.Where(v => v.Pair == volume.Pair && v.Root != volume.Root &&
                v.Model.Contains("SD Card", StringComparison.OrdinalIgnoreCase)).ToList();
            var sd = sdCandidates.Count == 1 ? sdCandidates[0] : null;
            devices.Add(new(volume.Pair + "/" + volume.Id, "SanDisk Sansa Clip+",
                new(Storage.Internal, volume.Id, volume.Root, volume.Free),
                sd is null ? null : new(Storage.Sd, sd.Id, sd.Root, sd.Free),
                ReadSetting(Path.Combine(volume.Root, ".rockbox", "config.cfg"), "default codepage") is { Length: > 0 } cp ? cp : "utf-8"));
        }
        return devices;
    }
    public PlayerDevice? Verify(PlayerDevice expected)
    {
        foreach (var volume in expected.Volumes)
        {
            if (IsFixture)
            {
                if (!Directory.Exists(volume.Root)) return null;
            }
            else if (!GetVolumeInformation(volume.Root, null, 0, out var serial, out _, out _, null, 0) ||
                serial.ToString("X8") != volume.Id) return null;
        }
        return expected;
    }
    private static string ReadSetting(string path, string key)
    {
        if (!File.Exists(path)) return "";
        return File.ReadLines(path).Select(line => line.Split(':', 2)).FirstOrDefault(parts =>
            parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))?[1].Trim() ?? "";
    }
    private sealed record UsbVolume(string Root, string Id, string Pair, string Model, long Free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameSize,
        out uint serial, out uint maxComponent, out uint flags, StringBuilder? fileSystem, int fileSystemSize);
}

public sealed class DeviceDiscoveryPodcastMountProvider(DeviceDiscovery discovery) : IPodcastMountProvider
{
    public PlayerDevice? Recheck(PlayerDevice expected)
    {
        var mounted = discovery.Find().FirstOrDefault(device => device.Id == expected.Id);
        if (mounted is null || mounted.Internal.Id != expected.Internal.Id ||
            !Path.GetFullPath(mounted.Internal.Root).Equals(Path.GetFullPath(expected.Internal.Root), StringComparison.OrdinalIgnoreCase)) return null;
        var expectedSd = expected.Sd;
        var mountedSd = mounted.Sd;
        if (expectedSd is null || mountedSd is null || mountedSd.Id != expectedSd.Id ||
            !Path.GetFullPath(mountedSd.Root).Equals(Path.GetFullPath(expectedSd.Root), StringComparison.OrdinalIgnoreCase)) return null;
        return mounted;
    }
}
