namespace EveryDownload.Services;

/// <summary>What a launch (from the browser, a magnet link, the command line) asks the app to do.</summary>
public sealed class LaunchRequest
{
    public List<AddRequest> Adds { get; } = new();
    /// <summary>Open the site grabber on this page.</summary>
    public string? GrabUrl { get; set; }
    public bool Background { get; set; }
    public bool Show { get; set; }

    /// <summary>
    /// Understands <c>--add URL</c> (followed by any of <c>--referer --cookie --ua --name</c>), <c>--grab URL</c>,
    /// <c>--background</c>, <c>--show</c>, and bare magnet links, web addresses and .torrent files.
    /// </summary>
    public static LaunchRequest Parse(IEnumerable<string> args)
    {
        var request = new LaunchRequest();
        AddRequest? current = null;
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            string? Next() => i + 1 < list.Count ? list[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--background": request.Background = true; break;
                case "--show": request.Show = true; break;
                case "--grab": request.GrabUrl = Next(); break;
                case "--add":
                    if (Next() is { } url) { current = new AddRequest { Urls = { url }, FromBrowser = true }; request.Adds.Add(current); }
                    break;
                case "--referer": if (Next() is { } r && current != null) current.Referer = r; break;
                case "--cookie": if (Next() is { } c && current != null) current.Cookie = c; break;
                case "--ua": if (Next() is { } u && current != null) current.UserAgent = u; break;
                case "--name": if (Next() is { } n && current != null) current.FileName = n; break;
                case "--silent": if (current != null) current.Silent = true; break;
                default:
                    if (PathUtil.LooksDownloadable(a))
                    {
                        current = new AddRequest { Urls = { a } };
                        request.Adds.Add(current);
                    }
                    break;
            }
        }
        return request;
    }
}
