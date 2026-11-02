using System.Net;
using System.Text.RegularExpressions;
using EveryDownload.Models;

namespace EveryDownload.Services;

/// <summary>One file found on a page.</summary>
public sealed record FoundFile(string Url, string Name, string Category, string FoundOn);

/// <summary>Reads a web page (and optionally the pages it links to on the same site) and lists the downloadable files in it.</summary>
public static class SiteGrabber
{
    /// <summary>Named filters shown in the grabber; the value is the list of extensions, or null for "any file".</summary>
    public static readonly (string Name, string[]? Extensions)[] Filters =
    {
        ("All files", null),
        ("Video", new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "m3u8", "mpg", "mpeg", "ts" }),
        ("Music", new[] { "mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus" }),
        ("Images", new[] { "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "tif", "tiff", "heic" }),
        ("Documents", new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "epub", "csv" }),
        ("Archives", new[] { "zip", "rar", "7z", "tar", "gz", "iso", "bz2", "xz" }),
        ("Programs", new[] { "exe", "msi", "msix", "apk", "dmg", "deb", "rpm" }),
    };

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".php", ".asp", ".aspx", ".jsp", ".css", ".js", ".json", ".xml", ".map", ".woff", ".woff2", ".ttf", ".cfm", ".cgi", ".shtml" };

    private static readonly Regex Attribute = new("""(?:href|src|data-src|data-url|data-href|content|poster)\s*=\s*["']([^"'<>]+)["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bare = new("""https?://[^\s"'<>\\)]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="depth">1 reads just <paramref name="pageUrl"/>; 2 also reads the pages it links to on the same site.</param>
    /// <param name="extensions">Extensions to keep (no dots), or null for every file that is not a web page.</param>
    public static async Task<List<FoundFile>> GrabAsync(HttpClient http, string pageUrl, DownloadItem template, string userAgent,
        string[]? extensions, int depth, CancellationToken ct, int maxPages = 25)
    {
        var start = new Uri(pageUrl);
        var found = new Dictionary<string, FoundFile>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.AbsoluteUri };
        var queue = new Queue<(Uri Page, int Depth)>();
        queue.Enqueue((start, 1));
        var wanted = extensions == null ? null : new HashSet<string>(extensions.Select(e => "." + e.Trim().TrimStart('.')), StringComparer.OrdinalIgnoreCase);

        int pages = 0;
        while (queue.Count > 0 && pages < maxPages)
        {
            var (page, level) = queue.Dequeue();
            pages++;
            string html;
            try
            {
                using var request = HttpFactory.Request(page.AbsoluteUri, template, userAgent);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                string type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length > 0 && !type.Contains("html") && !type.Contains("xml") && !type.Contains("text") && !type.Contains("json") && !type.Contains("mpegurl")) continue;
                html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested) { continue; }

            foreach (var raw in Candidates(html))
            {
                if (!Uri.TryCreate(page, WebUtility.HtmlDecode(raw.Replace("\\/", "/")), out var uri)) continue;
                if (uri.Scheme is not ("http" or "https" or "ftp")) continue;
                string ext = Path.GetExtension(uri.AbsolutePath);

                bool isPage = ext.Length == 0 || PageExtensions.Contains(ext);
                if (isPage)
                {
                    // Pages of the same site are followed on the next level; nothing else is worth fetching as a page.
                    if (level < depth && uri.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase) && seen.Add(uri.AbsoluteUri))
                        queue.Enqueue((uri, level + 1));
                    continue;
                }
                if (wanted != null && !wanted.Contains(ext)) continue;
                string name = PathUtil.SafeName(uri.Segments.Length > 0 ? uri.Segments[^1] : "", "file");
                found.TryAdd(uri.AbsoluteUri, new FoundFile(uri.AbsoluteUri, name, Categories.For(name), page.Host));
            }
        }
        return found.Values.OrderBy(f => f.Category).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Candidates(string html)
    {
        foreach (Match m in Attribute.Matches(html)) yield return m.Groups[1].Value;
        foreach (Match m in Bare.Matches(html)) yield return m.Value.TrimEnd('.', ',', ';');
    }
}
