using System.Text;
using System.Text.RegularExpressions;

namespace RockboxManager.Core;

public static partial class PlaylistCodec
{
    public const int MaxPathBytes = 260;
    [GeneratedRegex(@"^<[^/<>]*?(\d+)>/")]
    private static partial Regex VolumePrefix();

    public static SongPath Resolve(string entry, Storage source, string playlistRelativePath)
    {
        var path = entry.TrimEnd(' ', '\t', '\r', '\n').Replace('\\', '/');
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') path = path[2..];
        var storage = source;
        string relative;
        if (path.StartsWith('/'))
        {
            relative = path.TrimStart('/');
            var match = VolumePrefix().Match(relative);
            if (match.Success)
            {
                storage = match.Groups[1].Value switch { "0" => Storage.Internal, "1" => Storage.Sd, _ => Storage.Unknown };
                relative = relative[match.Length..];
                if (storage == Storage.Unknown) return new(storage, path);
            }
        }
        else relative = (System.IO.Path.GetDirectoryName(playlistRelativePath)?.Replace('\\', '/') ?? "") + "/" + path;
        var parts = new List<string>();
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return new(Storage.Unknown, entry);
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return new(storage, string.Join('/', parts));
    }

    public static List<SongPath> Read(byte[] bytes, Storage source, string relativePath, string codepage = "utf-8")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = relativePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding(MapEncoding(codepage), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var text = encoding.GetString(bytes).TrimStart('\ufeff');
        return text.Split('\n').Select(s => s.TrimEnd('\r', ' ', '\t'))
            .Where(s => s.Length > 0 && !s.StartsWith('#'))
            .Select(s => Resolve(s, source, relativePath)).ToList();
    }

    private static string MapEncoding(string codepage) => codepage.ToLowerInvariant() switch
    {
        "sjis" => "shift_jis", "ksx1001" => "ks_c_5601-1987", "iso8859-11" => "windows-874",
        "iso8859-1" => "iso-8859-1", "iso8859-2" => "iso-8859-2", "iso8859-7" => "iso-8859-7",
        "iso8859-8" => "iso-8859-8", "iso8859-9" => "iso-8859-9", "cp1250" => "windows-1250",
        "cp1251" => "windows-1251", "cp1252" => "windows-1252", "cp1256" => "windows-1256", _ => codepage
    };

    public static byte[] Write(IEnumerable<SongPath> entries, Storage destination, string relativePath)
    {
        var lines = entries.Select(entry =>
        {
            string path;
            if (entry.Storage == Storage.Unknown) path = entry.RelativePath;
            else if (destination == Storage.Internal) path = entry.RockboxPath;
            else if (entry.Storage == Storage.Internal) path = "/<microSD0>/" + entry.RelativePath;
            else
            {
                var directory = System.IO.Path.GetDirectoryName(relativePath)!.Replace('\\', '/');
                path = System.IO.Path.GetRelativePath("/" + directory, "/" + entry.RelativePath).Replace('\\', '/');
            }
            ValidatePath(path);
            if (entry.Storage != Storage.Unknown) ValidatePath(entry.RockboxPath);
            return path;
        });
        return Encoding.UTF8.GetBytes("#EXTM3U\n" + string.Join('\n', lines) + "\n");
    }

    public static void ValidatePath(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) >= MaxPathBytes || path.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("A playlist path exceeds Rockbox's 259-byte limit or contains an invalid character.");
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') ||
            name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || name.Any(char.IsControl) ||
            Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Choose a valid Windows filename without slashes, reserved names, or trailing spaces/dots.");
        ValidatePath("/<microSD1>/Playlists/" + name + ".m3u8");
    }
}
