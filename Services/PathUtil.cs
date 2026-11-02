using System.Text.RegularExpressions;
using EveryDownload.Models;

namespace EveryDownload.Services;

/// <summary>File-name and URL helpers shared by the engine and the window.</summary>
public static class PathUtil
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();
    private static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };

    /// <summary>Makes <paramref name="name"/> safe to use as a single file name (no folders, no illegal characters).</summary>
    public static string SafeName(string? name, string fallback = "download")
    {
        name = Uri.UnescapeDataString(name ?? "");
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..]; // a name from a server must never choose a folder
        var chars = name.Select(c => Array.IndexOf(Invalid, c) >= 0 || c < ' ' ? '_' : c).ToArray();
        name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return fallback;
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        if (name.Length > 180)
        {
            string ext = Path.GetExtension(name);
            name = name[..Math.Max(1, 180 - ext.Length)] + ext;
        }
        return name;
    }

    /// <summary>"name (2).ext" style name that is free in <paramref name="directory"/> and not in <paramref name="reserved"/>.</summary>
    public static string UniqueName(string directory, string name, Func<string, bool>? reserved = null)
    {
        bool Taken(string n) => File.Exists(Path.Combine(directory, n)) || File.Exists(Path.Combine(directory, n + ".edpart"))
            || System.IO.Directory.Exists(Path.Combine(directory, n)) || (reserved?.Invoke(n) ?? false);
        if (!Taken(name)) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; i < 10_000; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (!Taken(candidate)) return candidate;
        }
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    /// <summary>The file name a URL points at, or "download" when it ends in a folder.</summary>
    public static string NameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            {
                var dn = Regex.Match(uri.Query, @"[?&]dn=([^&]+)");
                return dn.Success ? SafeName(dn.Groups[1].Value.Replace('+', ' '), "torrent") : "torrent";
            }
            string last = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
            if (last.Length > 0) return SafeName(last);
            return SafeName(uri.Host);
        }
        return "download";
    }

    private static readonly Dictionary<string, string> TypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/x-matroska"] = ".mkv", ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a",
        ["audio/ogg"] = ".ogg", ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["application/x-7z-compressed"] = ".7z",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp",
        ["application/x-bittorrent"] = ".torrent", ["application/vnd.apple.mpegurl"] = ".m3u8",
    };

    /// <summary>Adds an extension taken from the content type when the name has none.</summary>
    public static string WithExtension(string name, string? contentType)
    {
        if (Path.HasExtension(name) || contentType == null) return name;
        string type = contentType.Split(';')[0].Trim();
        return TypeExtensions.TryGetValue(type, out var ext) ? name + ext : name;
    }

    public static DownloadKind KindOf(string url)
    {
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return DownloadKind.Torrent;
        if (url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase)) return DownloadKind.Ftp;
        string path = url.Split('?', '#')[0];
        if (path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return DownloadKind.Torrent;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)) return DownloadKind.Hls;
        return DownloadKind.Http;
    }

    /// <summary>True for something the app can download: http(s), ftp, a magnet link or a .torrent path.</summary>
    public static bool LooksDownloadable(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Contains('\n')) return false;
        if (text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return true;
        if (Uri.TryCreate(text, UriKind.Absolute, out var u)) return u.Scheme is "http" or "https" or "ftp" or "ftps";
        return text.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(text);
    }
}
