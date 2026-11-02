using System.Net;

namespace EveryDownload.Tests;

/// <summary>A tiny local web server with the behaviours a download manager has to survive: ranges, no ranges, drops, 404s.</summary>
public sealed class TestServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, byte[]> files = new();
    private readonly HashSet<string> noRange = new();
    private readonly HashSet<string> dropFirst = new();
    private readonly HashSet<string> dropped = new();
    private int concurrent;

    public string BaseUrl { get; }
    public int PeakConcurrent { get; private set; }
    public List<(string Path, long From)> Requests { get; } = new();
    /// <summary>Pause between 16 KB writes, to make downloads slow enough to interrupt.</summary>
    public int SlowMs { get; set; }

    public TestServer()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        BaseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(BaseUrl + "/");
        listener.Start();
        _ = Task.Run(Loop);
    }

    public void Add(string path, byte[] data, bool ignoreRange = false, bool dropFirstConnection = false)
    {
        files[path] = data;
        if (ignoreRange) noRange.Add(path);
        if (dropFirstConnection) dropFirst.Add(path);
    }

    public string Url(string path) => BaseUrl + path;

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private async Task Serve(HttpListenerContext ctx)
    {
        int now = Interlocked.Increment(ref concurrent);
        lock (Requests) PeakConcurrent = Math.Max(PeakConcurrent, now);
        try
        {
            string path = ctx.Request.Url!.AbsolutePath;
            if (path == "/page")
            {
                var html = "<a href=\"/files/a.zip\">a</a><a href='/files/b.pdf'>b</a><img src=\"/pic.png\"><a href=\"/other.html\">x</a>";
                var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
                return;
            }
            if (!files.TryGetValue(path, out var data)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

            long from = 0, to = data.Length - 1;
            bool ranged = false;
            string? range = ctx.Request.Headers["Range"];
            if (range != null && !noRange.Contains(path) && range.StartsWith("bytes="))
            {
                var parts = range[6..].Split('-');
                from = long.Parse(parts[0]);
                if (parts[1].Length > 0) to = Math.Min(long.Parse(parts[1]), data.Length - 1);
                ranged = true;
            }
            lock (Requests) Requests.Add((path, from));

            if (ranged && from >= data.Length) { ctx.Response.StatusCode = 416; ctx.Response.Headers["Content-Range"] = $"bytes */{data.Length}"; ctx.Response.Close(); return; }

            ctx.Response.AddHeader("ETag", "\"v1\"");
            if (!noRange.Contains(path)) ctx.Response.AddHeader("Accept-Ranges", "bytes");
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(path)}\"");
            long length = to - from + 1;
            if (ranged)
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{data.Length}");
            }
            ctx.Response.ContentLength64 = length;

            bool drop = dropFirst.Contains(path) && length > 100_000 && dropped.Add(path);
            long sent = 0;
            while (sent < length)
            {
                int chunk = (int)Math.Min(16 * 1024, length - sent);
                await ctx.Response.OutputStream.WriteAsync(data.AsMemory((int)(from + sent), chunk));
                sent += chunk;
                if (drop && sent >= length / 2) { ctx.Response.Abort(); return; }
                if (SlowMs > 0) await Task.Delay(SlowMs);
            }
            ctx.Response.Close();
        }
        catch { /* a client that hung up is not a server error */ }
        finally { Interlocked.Decrement(ref concurrent); }
    }

    public void Dispose() { try { listener.Close(); } catch { /* shutting down */ } }
}
