using System.Diagnostics;
using EveryDownload.Models;

namespace EveryDownload.Services;

public enum StartMode { Now, Queue, Later }

/// <summary>Everything the Add dialog (or the browser) can say about new downloads.</summary>
public sealed class AddRequest
{
    public List<string> Urls { get; } = new();
    public string? Directory { get; set; }
    /// <summary>Used only when there is a single address.</summary>
    public string? FileName { get; set; }
    public int Connections { get; set; }
    public int SpeedLimitKb { get; set; }
    public string Referer { get; set; } = "";
    public string Cookie { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string Headers { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public List<string> Mirrors { get; } = new();
    public string ExpectedHash { get; set; } = "";
    public DateTime? StartAt { get; set; }
    public StartMode Start { get; set; } = StartMode.Queue;
    public bool FromBrowser { get; set; }
    /// <summary>The browser asked for no dialog.</summary>
    public bool Silent { get; set; }
}

/// <summary>
/// Owns the download list: runs the queue (a limited number at a time), applies the schedule, the speed limit and the
/// metered-connection rule, retries failures, verifies and scans what arrives, and keeps everything saved.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly object listGate = new();
    private readonly List<DownloadItem> items;
    private readonly Dictionary<Guid, CancellationTokenSource> running = new();
    private readonly Dictionary<Guid, DownloadStatus> stopTo = new();
    private readonly HashSet<Guid> removing = new();
    private readonly string? storePath;
    private readonly Timer timer;
    private HttpClient http;
    private bool dirty;
    private DateTime lastSave = DateTime.MinValue, lastMeteredCheck = DateTime.MinValue;
    private bool scheduleWasOpen = true;
    private bool completedSinceIdle;
    private bool disposed;

    public AppSettings Settings { get; }
    public RateLimiter Limiter { get; } = new();
    /// <summary>False outside the scheduled window; queued downloads wait.</summary>
    public bool QueueOpen { get; private set; } = true;
    /// <summary>True while the connection is metered and the user asked to hold downloads then.</summary>
    public bool HeldForMeteredNetwork { get; private set; }
    /// <summary>Set by the app so the manager need not know about Windows networking.</summary>
    public Func<bool> IsMetered { get; set; } = () => false;

    public event Action<DownloadItem>? ItemCompleted;
    public event Action<DownloadItem>? ItemFailed;
    public event Action? ListChanged;
    /// <summary>The last running download finished and nothing else is waiting.</summary>
    public event Action? QueueFinished;

    public DownloadManager(AppSettings settings, string? storePath = null, bool autoLoad = true)
    {
        Settings = settings;
        this.storePath = storePath;
        items = autoLoad ? DownloadStore.Load(storePath) : new List<DownloadItem>();
        foreach (var it in items)
        {
            // Anything that was mid-flight when the app closed simply waits its turn again.
            if (it.Status is DownloadStatus.Downloading or DownloadStatus.Verifying or DownloadStatus.Scanning) it.Status = DownloadStatus.Queued;
            foreach (var s in it.Segments) s.Busy = false;
        }
        http = HttpFactory.Create(settings);
        ApplySettings();
        timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public List<DownloadItem> Snapshot()
    {
        lock (listGate) return items.ToList();
    }

    public double TotalSpeed => Snapshot().Where(i => i.Status == DownloadStatus.Downloading).Sum(i => i.Speed);
    public int ActiveCount => Snapshot().Count(i => i.Status == DownloadStatus.Downloading);
    public int WaitingCount => Snapshot().Count(i => i.Status == DownloadStatus.Queued);

    /// <summary>Applies changed settings: new proxy, the speed limit, the schedule.</summary>
    public void ApplySettings()
    {
        var old = http;
        http = HttpFactory.Create(Settings);
        if (old != null) _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => old.Dispose());
        ApplyLimiter();
    }

    /// <summary>Applies the total speed limit (on or off) to everything running.</summary>
    public void ApplyLimiter() => Limiter.BytesPerSecond = Settings.LimiterOn ? Settings.SpeedLimitKb * 1024L : 0;

    // ---- adding -----------------------------------------------------------------------------------------------

    public List<DownloadItem> Add(AddRequest request)
    {
        var added = new List<DownloadItem>();
        lock (listGate)
        {
            foreach (var raw in request.Urls.Select(u => u.Trim()).Where(u => u.Length > 0))
            {
                var item = new DownloadItem
                {
                    Url = raw,
                    Kind = PathUtil.KindOf(raw),
                    Referer = request.Referer,
                    Cookie = request.Cookie,
                    UserAgent = request.UserAgent,
                    Headers = request.Headers,
                    Username = request.Username,
                    Password = request.Password,
                    Connections = request.Connections,
                    SpeedLimitKb = request.SpeedLimitKb,
                    ExpectedHash = Integrity.Clean(request.ExpectedHash),
                    StartAt = request.StartAt,
                    Status = request.Start == StartMode.Later ? DownloadStatus.Paused : DownloadStatus.Queued,
                    Force = request.Start == StartMode.Now,
                };
                item.Mirrors.AddRange(request.Mirrors);

                string name = request.Urls.Count == 1 && !string.IsNullOrWhiteSpace(request.FileName) ? PathUtil.SafeName(request.FileName) : PathUtil.NameFromUrl(raw);
                if (item.Kind == DownloadKind.Torrent && File.Exists(raw)) name = Path.GetFileNameWithoutExtension(raw);
                item.NameLocked = request.Urls.Count == 1 && !string.IsNullOrWhiteSpace(request.FileName);
                if (item.Kind == DownloadKind.Hls) name = Path.ChangeExtension(name, null);
                item.Category = item.Kind == DownloadKind.Torrent ? Categories.Torrents : item.Kind == DownloadKind.Hls ? Categories.Video : Categories.For(name);
                if (!string.IsNullOrWhiteSpace(request.Directory)) { item.Directory = request.Directory!; item.DirectoryLocked = true; }
                else item.Directory = DirectoryFor(item.Category);
                item.FileName = UniqueNameLocked(item, name);

                items.Add(item);
                added.Add(item);
            }
            dirty = true;
        }
        if (added.Count > 0) ListChanged?.Invoke();
        Pump();
        return added;
    }

    /// <summary>The folder downloads of <paramref name="category"/> go to.</summary>
    public string DirectoryFor(string category) =>
        Settings.AutoCategorize && category != Categories.Other ? Path.Combine(Settings.BaseFolder, category) : Settings.BaseFolder;

    private string UniqueNameLocked(DownloadItem item, string name) =>
        PathUtil.UniqueName(item.Directory, name, n => items.Any(o => o != item && !o.IsFinished &&
            string.Equals(o.Directory, item.Directory, StringComparison.OrdinalIgnoreCase) && string.Equals(o.FileName, n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Asks the server about an address without adding it: name, size, whether it can resume.</summary>
    public async Task<ProbeResult> ProbeAsync(string url, AddRequest? details, CancellationToken ct)
    {
        var kind = PathUtil.KindOf(url);
        if (kind != DownloadKind.Http) return new ProbeResult(-1, kind != DownloadKind.Ftp, PathUtil.NameFromUrl(url), null, "", "");
        var template = new DownloadItem
        {
            Referer = details?.Referer ?? "", Cookie = details?.Cookie ?? "", UserAgent = details?.UserAgent ?? "", Headers = details?.Headers ?? "",
            Username = details?.Username ?? "", Password = details?.Password ?? "",
        };
        return await Probe.RunAsync(http, url, template, Settings.UserAgent, ct).ConfigureAwait(false);
    }

    public HttpClient Client => http;

    // ---- control ----------------------------------------------------------------------------------------------

    public void Start(DownloadItem item)
    {
        if (item.IsActive || item.IsFinished) return;
        item.Status = DownloadStatus.Queued;
        item.Force = true;
        item.Attempts = 0;
        item.RetryAt = null;
        item.Error = "";
        dirty = true;
        Pump();
    }

    public void StartAll()
    {
        foreach (var it in Snapshot().Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed))
        {
            it.Status = DownloadStatus.Queued;
            it.Attempts = 0;
            it.RetryAt = null;
        }
        dirty = true;
        Pump();
    }

    public void Pause(DownloadItem item)
    {
        if (running.TryGetValue(item.Id, out var cts))
        {
            lock (stopTo) stopTo[item.Id] = DownloadStatus.Paused;
            cts.Cancel();
        }
        else if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        item.Force = false;
        item.RetryAt = null;
        dirty = true;
    }

    public void PauseAll()
    {
        foreach (var it in Snapshot().Where(i => i.CanPause)) Pause(it);
    }

    public void Remove(DownloadItem item, bool deleteFile)
    {
        lock (listGate) items.Remove(item);
        if (running.TryGetValue(item.Id, out var cts))
        {
            lock (stopTo) removing.Add(item.Id);
            cts.Cancel();
        }
        DeleteLeftovers(item, deleteFile);
        dirty = true;
        ListChanged?.Invoke();
    }

    public void RemoveFinished()
    {
        List<DownloadItem> done;
        lock (listGate) { done = items.Where(i => i.IsFinished).ToList(); foreach (var i in done) items.Remove(i); }
        if (done.Count > 0) { dirty = true; ListChanged?.Invoke(); }
    }

    /// <summary>Throws the partial file away and downloads again from the start.</summary>
    public void Restart(DownloadItem item)
    {
        if (running.TryGetValue(item.Id, out var cts))
        {
            lock (stopTo) stopTo[item.Id] = DownloadStatus.Paused;
            cts.Cancel();
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 50 && running.ContainsKey(item.Id); i++) await Task.Delay(100);
                ResetAndStart(item);
            });
        }
        else ResetAndStart(item);
    }

    private void ResetAndStart(DownloadItem item)
    {
        DeleteLeftovers(item, item.IsFinished);
        lock (item.Gate) { item.Segments = new List<Segment>(); item.DoneBytes = 0; item.Probed = false; item.ETag = ""; item.LastModified = ""; }
        item.TotalBytes = -1;
        item.CompletedAt = null;
        item.ActualHash = "";
        Start(item);
    }

    /// <summary>Gives an expired download a new address. If the file is the same size, the bytes already on disk are kept.</summary>
    public async Task<bool> RefreshLinkAsync(DownloadItem item, string newUrl, CancellationToken ct)
    {
        var details = new AddRequest { Referer = item.Referer, Cookie = item.Cookie, UserAgent = item.UserAgent, Headers = item.Headers, Username = item.Username, Password = item.Password };
        bool keep = false;
        if (PathUtil.KindOf(newUrl) == DownloadKind.Http)
        {
            var probe = await ProbeAsync(newUrl, details, ct).ConfigureAwait(false);
            keep = item.Probed && probe.Size == item.TotalBytes && probe.Size > 0;
        }
        item.Url = newUrl;
        item.Kind = PathUtil.KindOf(newUrl);
        if (!keep)
        {
            DeleteLeftovers(item, false);
            lock (item.Gate) { item.Segments = new List<Segment>(); item.DoneBytes = 0; }
            item.Probed = false;
        }
        else { item.ETag = ""; item.LastModified = ""; } // the new link's validators will not match the old ones
        Start(item);
        return keep;
    }

    private void DeleteLeftovers(DownloadItem item, bool includeFinishedFile)
    {
        try
        {
            if (File.Exists(item.PartPath)) File.Delete(item.PartPath);
            if (System.IO.Directory.Exists(item.FilePath + ".edparts")) System.IO.Directory.Delete(item.FilePath + ".edparts", true);
            if (includeFinishedFile && item.Kind != DownloadKind.Torrent && File.Exists(item.FilePath)) File.Delete(item.FilePath);
        }
        catch (Exception ex) { Log.Write("Could not delete leftovers of " + item.FileName, ex); }
    }

    // ---- running ----------------------------------------------------------------------------------------------

    /// <summary>Starts as many waiting downloads as the limits allow.</summary>
    public void Pump()
    {
        if (disposed) return;
        List<DownloadItem> toStart = new();
        lock (listGate)
        {
            int slots = Settings.MaxSimultaneous - running.Count;
            var now = DateTime.Now;
            foreach (var item in items.OrderBy(i => i.AddedAt))
            {
                if (item.Status != DownloadStatus.Queued || running.ContainsKey(item.Id)) continue;
                if (item.StartAt is { } at && at > now) continue;
                if (item.RetryAt is { } retry && retry > now) continue;
                if (item.Force) { toStart.Add(item); continue; }
                if (!QueueOpen || HeldForMeteredNetwork || slots <= 0) continue;
                toStart.Add(item);
                slots--;
            }
        }
        foreach (var item in toStart) Launch(item);
    }

    private void Launch(DownloadItem item)
    {
        var cts = new CancellationTokenSource();
        lock (running) { if (running.ContainsKey(item.Id)) return; running[item.Id] = cts; }
        item.Status = DownloadStatus.Downloading;
        item.Force = false;
        item.Speed = 0;
        dirty = true;
        _ = Task.Run(() => RunAsync(item, cts.Token));
    }

    private EngineOptions Options() => new()
    {
        MaxConnections = Settings.MaxConnections,
        DefaultConnections = Settings.DefaultConnections,
        UserAgent = Settings.UserAgent,
        Global = Limiter,
        OnProbed = OnProbed,
        Save = () => dirty = true,
    };

    /// <summary>After the server answered: pick the real file name, type and folder.</summary>
    private void OnProbed(DownloadItem item, ProbeResult result)
    {
        lock (listGate)
        {
            if (!item.NameLocked) item.Category = Categories.For(item.FileName);
            if (!item.DirectoryLocked) item.Directory = DirectoryFor(item.Category);
            if (!item.NameLocked) item.FileName = UniqueNameLocked(item, item.FileName);
        }
    }

    private async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        try
        {
            var options = Options();
            switch (item.Kind)
            {
                case DownloadKind.Http: await new SegmentedDownloader(item, http, options).RunAsync(ct).ConfigureAwait(false); break;
                case DownloadKind.Hls: await new HlsDownloader(item, http, options, Settings.RemuxVideo).RunAsync(ct).ConfigureAwait(false); break;
                case DownloadKind.Ftp: await new FtpDownloader(item, options).RunAsync(ct).ConfigureAwait(false); break;
                case DownloadKind.Torrent: await new TorrentDownloader(item, http, options, Settings.SeedTorrents).RunAsync(ct).ConfigureAwait(false); break;
            }
            await FinishAsync(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (stopTo)
            {
                if (!removing.Remove(item.Id)) item.Status = stopTo.TryGetValue(item.Id, out var s) ? s : DownloadStatus.Paused;
                stopTo.Remove(item.Id);
            }
        }
        catch (FatalDownloadException ex) { Fail(item, ex.Message, retry: false); }
        catch (Exception ex)
        {
            Log.Write("Download failed: " + item.Url, ex);
            Fail(item, Describe(ex), retry: true);
        }
        finally
        {
            item.Speed = 0;
            item.ActiveConnections = 0;
            item.Note = "";
            lock (running) { running.Remove(item.Id); }
            dirty = true;
            if (!disposed) Pump();
            CheckQueueFinished();
        }
    }

    private async Task FinishAsync(DownloadItem item, CancellationToken ct)
    {
        string path = item.FilePath;
        bool isFile = File.Exists(path);

        if (item.ExpectedHash.Length > 0 && isFile)
        {
            item.Status = DownloadStatus.Verifying;
            var algorithm = Integrity.AlgorithmFor(item.ExpectedHash);
            if (algorithm == null) item.Note = "checksum format not recognised";
            else
            {
                item.ActualHash = await Integrity.HashAsync(path, algorithm.Value, ct).ConfigureAwait(false);
                if (!string.Equals(item.ActualHash, item.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Fail(item, "The checksum does not match, so the file is damaged or not the one expected.", retry: false);
                    return;
                }
                item.Note = "checksum verified";
            }
        }

        if (Settings.ScanDownloads && isFile && item.Kind != DownloadKind.Torrent)
        {
            item.Status = DownloadStatus.Scanning;
            bool? clean = await Integrity.ScanAsync(path, ct).ConfigureAwait(false);
            if (clean == false)
            {
                Fail(item, "Microsoft Defender found a threat in this file. It was left where it is; do not open it.", retry: false);
                return;
            }
            if (clean == true) item.Note = item.Note.Length > 0 ? item.Note + ", no threats found" : "no threats found";
        }

        item.Status = DownloadStatus.Completed;
        item.CompletedAt = DateTime.Now;
        item.Error = "";
        completedSinceIdle = true;
        ItemCompleted?.Invoke(item);
    }

    private void Fail(DownloadItem item, string message, bool retry)
    {
        item.Error = message;
        if (retry && item.Attempts < Settings.AutoRetries)
        {
            item.Attempts++;
            item.Status = DownloadStatus.Queued;
            item.RetryAt = DateTime.Now.AddSeconds(Settings.RetrySeconds);
            item.Error = ShortError(message);
            return;
        }
        item.Status = DownloadStatus.Failed;
        ItemFailed?.Invoke(item);
    }

    private static string ShortError(string message) => message.Length > 60 ? message[..57] + "…" : message;

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } => "Could not connect to the server.",
        HttpRequestException h => h.Message,
        TimeoutException t => t.Message,
        IOException io when io.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase) => "There is not enough free space on the disk.",
        _ => ex.Message,
    };

    private void CheckQueueFinished()
    {
        bool idle;
        lock (running) idle = running.Count == 0;
        if (!idle || !completedSinceIdle) return;
        bool waiting = Snapshot().Any(i => i.Status == DownloadStatus.Queued);
        if (waiting) return;
        completedSinceIdle = false;
        QueueFinished?.Invoke();
    }

    // ---- housekeeping -----------------------------------------------------------------------------------------

    private void Tick()
    {
        if (disposed) return;
        try
        {
            bool open = ScheduleRules.IsOpen(Settings, DateTime.Now);
            QueueOpen = open;
            if (scheduleWasOpen && !open && Settings.ScheduleEnabled && Settings.ScheduleStopEnabled)
            {
                // The window closed: stop what is running, but keep it first in line for the next window.
                foreach (var it in Snapshot().Where(i => i.Status == DownloadStatus.Downloading && !i.Force))
                    if (running.TryGetValue(it.Id, out var cts)) { lock (stopTo) stopTo[it.Id] = DownloadStatus.Queued; cts.Cancel(); }
            }
            scheduleWasOpen = open;

            if (DateTime.Now - lastMeteredCheck > TimeSpan.FromSeconds(15))
            {
                lastMeteredCheck = DateTime.Now;
                bool metered = Settings.PauseOnMetered && IsMetered();
                if (metered && !HeldForMeteredNetwork)
                    foreach (var it in Snapshot().Where(i => i.Status == DownloadStatus.Downloading && !i.Force))
                        if (running.TryGetValue(it.Id, out var cts)) { lock (stopTo) stopTo[it.Id] = DownloadStatus.Queued; cts.Cancel(); }
                HeldForMeteredNetwork = metered;
            }

            Pump();
            if (dirty && DateTime.Now - lastSave > TimeSpan.FromSeconds(2)) SaveNow();
        }
        catch (Exception ex) { Log.Write("Queue tick failed", ex); }
    }

    public void SaveNow()
    {
        dirty = false;
        lastSave = DateTime.Now;
        DownloadStore.Save(Snapshot(), storePath);
    }

    /// <summary>Stops every download (keeping what was fetched) and saves the list. Call when the app exits.</summary>
    public async Task ShutdownAsync()
    {
        disposed = true;
        timer.Dispose();
        List<CancellationTokenSource> all;
        lock (running) all = running.Values.ToList();
        foreach (var it in Snapshot().Where(i => running.ContainsKey(i.Id))) lock (stopTo) stopTo[it.Id] = DownloadStatus.Queued;
        foreach (var c in all) c.Cancel();
        for (int i = 0; i < 50; i++) { lock (running) { if (running.Count == 0) break; } await Task.Delay(100).ConfigureAwait(false); }
        await TorrentDownloader.ShutdownAsync().ConfigureAwait(false);
        SaveNow();
    }

    public void Dispose()
    {
        if (!disposed) { disposed = true; timer.Dispose(); }
    }
}

/// <summary>Decides whether the clock is inside the user's download window.</summary>
public static class ScheduleRules
{
    public static bool IsOpen(AppSettings s, DateTime now)
    {
        if (!s.ScheduleEnabled) return true;
        if (!TimeSpan.TryParse(s.ScheduleStart, out var start)) return true;
        bool DayOn(DateTime d) => (s.ScheduleDays & (1 << (int)d.DayOfWeek)) != 0;

        if (!s.ScheduleStopEnabled || !TimeSpan.TryParse(s.ScheduleStop, out var stop))
            return DayOn(now) && now.TimeOfDay >= start; // opens at the start time and stays open for the day

        if (start <= stop) return DayOn(now) && now.TimeOfDay >= start && now.TimeOfDay < stop;
        // A window that runs past midnight (22:00 to 06:00) belongs to the day it started on.
        if (now.TimeOfDay >= start) return DayOn(now);
        return now.TimeOfDay < stop && DayOn(now.AddDays(-1));
    }
}
