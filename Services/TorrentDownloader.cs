using EveryDownload.Models;
using MonoTorrent;
using MonoTorrent.Client;

namespace EveryDownload.Services;

/// <summary>Downloads a magnet link or a .torrent file with MonoTorrent (DHT, trackers, peer exchange).</summary>
public sealed class TorrentDownloader
{
    private static ClientEngine? engine;
    private static readonly object EngineGate = new();

    private static ClientEngine Engine()
    {
        lock (EngineGate)
        {
            if (engine != null) return engine;
            string cache = Path.Combine(AppSettings.Folder, "torrents");
            Directory.CreateDirectory(cache);
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = cache,
                AllowPortForwarding = true,
                AutoSaveLoadFastResume = true,
                AutoSaveLoadMagnetLinkMetadata = true,
                DhtEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0),
                ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint> { ["ipv4"] = new(System.Net.IPAddress.Any, 0) },
            };
            return engine = new ClientEngine(settings.ToSettings());
        }
    }

    private readonly DownloadItem item;
    private readonly HttpClient http;
    private readonly EngineOptions options;
    private readonly bool seed;

    public TorrentDownloader(DownloadItem item, HttpClient http, EngineOptions options, bool seed)
    {
        this.item = item;
        this.http = http;
        this.options = options;
        this.seed = seed;
    }

    /// <summary>Stops every torrent and the engine; call when the app exits.</summary>
    public static async Task ShutdownAsync()
    {
        ClientEngine? e;
        lock (EngineGate) { e = engine; engine = null; }
        if (e == null) return;
        try { await e.StopAllAsync().ConfigureAwait(false); e.Dispose(); } catch (Exception ex) { Log.Write("Torrent engine did not stop cleanly", ex); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(item.Directory);
        item.Error = "";
        item.Resumable = true;
        var client = Engine();

        TorrentManager manager;
        item.Note = "Looking for metadata…";
        try
        {
            if (item.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                manager = await client.AddAsync(MagnetLink.Parse(item.Url), item.Directory).ConfigureAwait(false);
            else
            {
                string file = await LocalTorrentAsync(ct).ConfigureAwait(false);
                manager = await client.AddAsync(file, item.Directory).ConfigureAwait(false);
            }
        }
        catch (TorrentException ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            manager = client.Torrents.First(t => t.SavePath == Path.GetFullPath(item.Directory) || t.Torrent?.Name == item.FileName);
        }
        catch (FormatException ex) { throw new FatalDownloadException("That is not a valid magnet link: " + ex.Message); }

        try
        {
            await manager.StartAsync().ConfigureAwait(false);
            var history = new List<double>();
            long lastDone = 0;
            double ema = 0;
            while (!ct.IsCancellationRequested)
            {
                if (manager.HasMetadata && manager.Torrent is { } torrent)
                {
                    item.TotalBytes = torrent.Size;
                    if (!item.NameLocked && item.FileName != torrent.Name)
                    {
                        item.FileName = PathUtil.SafeName(torrent.Name);
                        item.Category = Categories.Torrents;
                    }
                    item.Note = manager.State == TorrentState.Hashing ? "Checking existing files…" : "";
                }
                long done = (long)(manager.Progress / 100.0 * Math.Max(0, item.TotalBytes));
                item.DoneBytes = done;
                double instant = manager.Monitor.DownloadRate;
                ema = ema == 0 ? instant : ema * 0.6 + instant * 0.4;
                item.Speed = ema;
                item.ActiveConnections = manager.OpenConnections;
                history.Add(ema);
                if (history.Count > 120) history.RemoveAt(0);
                item.SpeedHistory = history.ToArray();
                lastDone = done;

                if (manager.Complete || manager.State == TorrentState.Seeding)
                {
                    item.DoneBytes = item.TotalBytes;
                    break;
                }
                if (manager.State == TorrentState.Error)
                    throw new IOException(manager.Error?.Exception?.Message ?? "The torrent stopped with an error.");
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            _ = lastDone;
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            item.Speed = 0;
            item.ActiveConnections = 0;
            if (!seed || ct.IsCancellationRequested)
            {
                try
                {
                    await manager.StopAsync().ConfigureAwait(false);
                    await client.RemoveAsync(manager).ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Write("Could not stop the torrent", ex); }
            }
        }
    }

    /// <summary>The path of the .torrent file: the one on disk, or the one downloaded from a web address.</summary>
    private async Task<string> LocalTorrentAsync(CancellationToken ct)
    {
        if (File.Exists(item.Url)) return item.Url;
        using var request = HttpFactory.Request(item.Url, item, options.UserAgent);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        Probe.ThrowIfBad(response);
        string folder = Path.Combine(AppSettings.Folder, "torrent-files");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, item.Id.ToString("N") + ".torrent");
        await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return path;
    }
}
