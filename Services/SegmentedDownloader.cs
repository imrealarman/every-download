using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using EveryDownload.Models;
using Microsoft.Win32.SafeHandles;

namespace EveryDownload.Services;

/// <summary>Limits and hooks for one run of the HTTP engine.</summary>
public sealed class EngineOptions
{
    /// <summary>Most connections Smart Boost may open.</summary>
    public int MaxConnections { get; set; } = 16;
    /// <summary>Connections to use when the download does not choose; 0 is Smart Boost.</summary>
    public int DefaultConnections { get; set; }
    /// <summary>A range is only split in two when both halves would be at least this big.</summary>
    public int MinSplitBytes { get; set; } = 256 * 1024;
    public int MaxRetries { get; set; } = 8;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public string UserAgent { get; set; } = AppSettings.DefaultUserAgent;
    public RateLimiter? Global { get; set; }
    /// <summary>Called after the server answered the first probe so the owner can pick the final name and folder.</summary>
    public Action<DownloadItem, ProbeResult>? OnProbed { get; set; }
    /// <summary>Called every few seconds so progress can be saved.</summary>
    public Action? Save { get; set; }
}

/// <summary>
/// The multi-connection HTTP engine. A file is cut into byte ranges, each range is pulled over its own connection and
/// written straight to its place in the file, and a connection that finishes early takes half of whichever range has the
/// most left (IDM-style dynamic segmentation). With Smart Boost the engine keeps opening connections while the total
/// speed keeps climbing and stops at the point where more connections no longer help, or the server pushes back.
/// </summary>
public sealed class SegmentedDownloader
{
    private sealed class RestartException : Exception
    {
        public bool NoRanges { get; }
        public RestartException(bool noRanges, string message) : base(message) => NoRanges = noRanges;
    }

    private readonly DownloadItem item;
    private readonly HttpClient http;
    private readonly EngineOptions options;
    private readonly RateLimiter itemLimiter = new();

    private long windowBytes;
    private int active;
    private int target;
    private int cap = int.MaxValue; // lowered when the server starts refusing connections
    private int mirrorSeed;

    public SegmentedDownloader(DownloadItem item, HttpClient http, EngineOptions options)
    {
        this.item = item;
        this.http = http;
        this.options = options;
    }

    private List<string> Urls
    {
        get
        {
            var urls = new List<string> { item.Url };
            urls.AddRange(item.Mirrors.Where(m => Uri.TryCreate(m, UriKind.Absolute, out _)));
            return urls;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        bool forceSingle = false;
        for (int pass = 0; ; pass++)
        {
            try
            {
                await RunOnceAsync(forceSingle, ct).ConfigureAwait(false);
                return;
            }
            catch (RestartException ex) when (pass < 2)
            {
                // The server ignored the range, or the file changed under us: start this download again from byte 0.
                forceSingle |= ex.NoRanges;
                lock (item.Gate) { item.Segments = new List<Segment>(); item.DoneBytes = 0; item.Probed = false; item.ETag = ""; item.LastModified = ""; }
                TryDelete(item.PartPath);
            }
        }
    }

    private async Task RunOnceAsync(bool forceSingle, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(item.Directory);
        item.Error = "";
        item.Note = "";
        itemLimiter.BytesPerSecond = item.SpeedLimitKb * 1024L;

        if (!item.Probed)
        {
            item.Note = "Contacting server…";
            ProbeResult? result = null;
            Exception? last = null;
            foreach (var url in Urls) // the first address that answers wins
            {
                try { result = await Probe.RunAsync(http, url, item, options.UserAgent, ct).ConfigureAwait(false); break; }
                catch (FatalDownloadException ex) { last = ex; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException) { last = ex; }
            }
            if (result == null) ExceptionDispatchInfo.Capture(last ?? new IOException("Could not reach the server.")).Throw();
            item.TotalBytes = result!.Size;
            item.Resumable = result.Resumable && !forceSingle;
            item.ETag = result.ETag;
            item.LastModified = result.LastModified;
            if (!item.NameLocked) item.FileName = result.FileName;
            options.OnProbed?.Invoke(item, result);
            item.Probed = true;
            options.Save?.Invoke();
        }
        item.Note = "";

        if (item.TotalBytes == 0)
        {
            File.WriteAllBytes(item.FilePath, Array.Empty<byte>());
            item.Segments = new List<Segment>();
            item.DoneBytes = 0;
            return;
        }

        PrepareSegments();
        using var file = File.OpenHandle(item.PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        try
        {
            if (item.TotalBytes > 0 && RandomAccess.GetLength(file) != item.TotalBytes) RandomAccess.SetLength(file, item.TotalBytes);
        }
        catch (IOException ex) { throw new FatalDownloadException("Could not reserve the file on disk: " + ex.Message); }

        try
        {
            await RunWorkersAsync(file, ct).ConfigureAwait(false);
            if (item.TotalBytes <= 0) item.TotalBytes = item.DoneBytes;
            RandomAccess.FlushToDisk(file);
        }
        finally
        {
            lock (item.Gate) foreach (var s in item.Segments) s.Busy = false;
            item.ActiveConnections = 0;
            item.Speed = 0;
        }
        file.Close();
        Finish();
    }

    /// <summary>Cuts a fresh download into its first ranges, or keeps the ranges saved by an earlier run.</summary>
    private void PrepareSegments()
    {
        lock (item.Gate)
        {
            bool canResume = item.Resumable && item.Segments.Count > 0 && File.Exists(item.PartPath);
            if (canResume) return;

            item.DoneBytes = 0;
            TryDelete(item.PartPath);
            item.Segments = new List<Segment>();
            long total = item.TotalBytes;
            int count = 1;
            if (item.Resumable && total > 0)
            {
                int wanted = item.Connections > 0 ? item.Connections : options.DefaultConnections > 0 ? options.DefaultConnections : 4;
                wanted = Math.Min(wanted, options.MaxConnections);
                count = (int)Math.Clamp(total / options.MinSplitBytes, 1, wanted);
            }
            for (int i = 0; i < count; i++)
            {
                long start = total > 0 ? total * i / count : 0;
                long end = total > 0 ? total * (i + 1) / count - 1 : long.MaxValue;
                item.Segments.Add(new Segment { Start = start, End = end, Position = start });
            }
        }
    }

    // ---- workers ----------------------------------------------------------------------------------------------

    private async Task RunWorkersAsync(SafeFileHandle file, CancellationToken ct)
    {
        bool smart = item.Connections == 0 && options.DefaultConnections == 0 && item.Resumable;
        int max = Math.Clamp(item.Connections > 0 ? item.Connections : options.MaxConnections, 1, 32);
        int initial;
        lock (item.Gate) initial = item.Segments.Count(s => !s.IsDone);
        target = item.Resumable ? Math.Max(1, initial) : 1;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<Task>();
        void Spawn()
        {
            var seg = Claim();
            if (seg != null) running.Add(Task.Run(() => WorkerAsync(seg, file, linked.Token)));
        }
        for (int i = 0; i < target; i++) Spawn();

        var clock = Stopwatch.StartNew();
        double lastTick = 0, lastBoost = 0, lastSave = 0, boostBase = 0;
        long tickBytes = Interlocked.Read(ref windowBytes), boostBytes = tickBytes;
        double ema = 0;
        bool boosting = smart;
        var history = new List<double>(120);

        while (true)
        {
            var failed = running.FirstOrDefault(t => t.IsFaulted);
            if (failed != null || ct.IsCancellationRequested)
            {
                linked.Cancel();
                try { await Task.WhenAll(running).ConfigureAwait(false); } catch { /* the first failure is the one reported */ }
                ct.ThrowIfCancellationRequested();
                ExceptionDispatchInfo.Capture(failed!.Exception!.GetBaseException()).Throw();
            }
            running.RemoveAll(t => t.IsCompleted);

            if (running.Count == 0)
            {
                if (AllDone()) break;
                Spawn();
                if (running.Count == 0) throw new IOException("The download stopped before the file was complete.");
            }
            else if (running.Count < Math.Min(target, cap)) Spawn();

            await Task.WhenAny(Task.WhenAny(running.Count > 0 ? running : new List<Task> { Task.CompletedTask }), Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);

            double now = clock.Elapsed.TotalSeconds;
            if (now - lastTick >= 0.5)
            {
                long bytes = Interlocked.Read(ref windowBytes);
                double instant = (bytes - tickBytes) / (now - lastTick);
                ema = ema == 0 ? instant : ema * 0.6 + instant * 0.4;
                tickBytes = bytes;
                lastTick = now;
                item.Speed = ema;
                item.ActiveConnections = Volatile.Read(ref active);
                item.DoneBytes = ComputeDone();
                history.Add(ema);
                if (history.Count > 120) history.RemoveAt(0);
                item.SpeedHistory = history.ToArray();
            }

            if (boosting && now - lastBoost >= 3)
            {
                double speed = (Interlocked.Read(ref windowBytes) - boostBytes) / (now - lastBoost);
                boostBytes = Interlocked.Read(ref windowBytes);
                lastBoost = now;
                long left = item.TotalBytes - item.DoneBytes;
                bool worthIt = speed > 0 && left / speed > 6 && target < Math.Min(max, cap);
                if (worthIt && (boostBase == 0 || speed > boostBase * 1.10))
                {
                    boostBase = speed;
                    target = Math.Min(Math.Min(max, cap), target + 2);
                }
                else if (boostBase > 0) boosting = false; // more connections stopped paying off
                if (cap < max) boosting = false;
            }

            if (now - lastSave >= 3)
            {
                lastSave = now;
                options.Save?.Invoke();
            }
        }
        item.DoneBytes = ComputeDone();
    }

    private async Task WorkerAsync(Segment first, SafeFileHandle file, CancellationToken ct)
    {
        Interlocked.Increment(ref active);
        try
        {
            Segment? seg = first;
            while (seg != null)
            {
                await PullWithRetriesAsync(seg, file, ct).ConfigureAwait(false);
                lock (item.Gate) seg.Busy = false;
                seg = ct.IsCancellationRequested || Volatile.Read(ref active) > Math.Min(target, cap) ? null : Claim();
            }
        }
        finally { Interlocked.Decrement(ref active); }
    }

    private async Task PullWithRetriesAsync(Segment seg, SafeFileHandle file, CancellationToken ct)
    {
        int seed = Interlocked.Increment(ref mirrorSeed);
        var urls = Urls;
        while (true)
        {
            string url = urls[(seed + seg.Failures) % urls.Count]; // a failing range moves on to the next mirror
            long before = seg.Position;
            try
            {
                await PullAsync(seg, url, file, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (FatalDownloadException) when (urls.Count > 1 && seg.Failures < urls.Count - 1)
            {
                seg.Failures++; // this address refuses us (404, 403...): the next mirror may not
            }
            catch (Exception ex) when (ex is FatalDownloadException or RestartException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable })
                    Interlocked.Exchange(ref cap, Math.Max(1, Volatile.Read(ref active) - 1)); // the server wants fewer connections
                if (seg.Position > before) seg.Failures = 0; else seg.Failures++;
                if (seg.Failures > options.MaxRetries)
                    throw ex is OperationCanceledException ? new TimeoutException("The server stopped sending data.") : ex;
                item.Note = "Reconnecting…";
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, Math.Max(0, seg.Failures - 1)))), ct).ConfigureAwait(false);
                item.Note = "";
            }
        }
    }

    /// <summary>Downloads one range over one connection and writes it where it belongs in the file.</summary>
    private async Task PullAsync(Segment seg, string url, SafeFileHandle file, CancellationToken ct)
    {
        long pos = seg.Position;
        if (pos > seg.End) return;
        bool ranged = item.Resumable;

        using var request = HttpFactory.Request(url, item, options.UserAgent);
        if (ranged)
        {
            request.Headers.Range = new RangeHeaderValue(pos, seg.End == long.MaxValue ? null : seg.End);
            if (item.ETag.Length > 0) request.Headers.TryAddWithoutValidation("If-Range", item.ETag);
            else if (item.LastModified.Length > 0) request.Headers.TryAddWithoutValidation("If-Range", item.LastModified);
        }
        else if (pos > 0)
        {
            seg.Position = pos = seg.Start; // a server that cannot resume starts over
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (item.TotalBytes > 0 && pos >= item.TotalBytes) { seg.Position = seg.End + 1; return; }
            throw new RestartException(false, "The file changed on the server.");
        }
        Probe.ThrowIfBad(response);

        if (ranged && response.StatusCode == HttpStatusCode.OK)
        {
            // Asked for a range, got everything: either the server cannot do ranges or the file is not the one we started.
            bool onlyOne;
            lock (item.Gate) onlyOne = item.Segments.Count == 1 && pos == 0;
            if (!onlyOne) throw new RestartException(item.ETag.Length == 0 && item.LastModified.Length == 0, "The server did not honour the range request.");
        }
        else if (ranged && response.Content.Headers.ContentRange?.From is { } from && from != pos)
            throw new IOException("The server answered with the wrong part of the file.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (true)
            {
                long end = seg.End;
                if (pos > end) break; // another connection took the rest of this range
                int want = end == long.MaxValue ? buffer.Length : (int)Math.Min(buffer.Length, end - pos + 1);

                idle.CancelAfter(options.IdleTimeout);
                int read = await stream.ReadAsync(buffer.AsMemory(0, want), idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (seg.End == long.MaxValue) { seg.End = pos - 1; break; } // length unknown: the stream's end is the file's end
                    throw new IOException("The connection closed before the range was complete.");
                }

                if (options.Global != null) await options.Global.WaitAsync(read, ct).ConfigureAwait(false);
                await itemLimiter.WaitAsync(read, ct).ConfigureAwait(false);

                end = seg.End; // may have shrunk while we waited
                int take = end == long.MaxValue ? read : (int)Math.Max(0, Math.Min(read, end - pos + 1));
                if (take <= 0) break;
                await RandomAccess.WriteAsync(file, buffer.AsMemory(0, take), pos, ct).ConfigureAwait(false);
                pos += take;
                seg.Position = pos;
                Interlocked.Add(ref windowBytes, take);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // ---- ranges -----------------------------------------------------------------------------------------------

    /// <summary>The next range nobody is working on, or half of the biggest one that is still being pulled.</summary>
    private Segment? Claim()
    {
        lock (item.Gate)
        {
            foreach (var s in item.Segments)
                if (!s.Busy && !s.IsDone) { s.Busy = true; return s; }
            if (!item.Resumable) return null;

            Segment? best = null;
            foreach (var s in item.Segments)
                if (s.Busy && s.End != long.MaxValue && (best == null || s.Remaining > best.Remaining)) best = s;
            if (best == null || best.Remaining < 2L * options.MinSplitBytes) return null;

            long mid = best.Position + best.Remaining / 2;
            var taken = new Segment { Start = mid, End = best.End, Position = mid, Busy = true };
            best.End = mid - 1;
            item.Segments.Insert(item.Segments.IndexOf(best) + 1, taken);
            return taken;
        }
    }

    private bool AllDone()
    {
        lock (item.Gate) return item.Segments.All(s => s.IsDone);
    }

    private long ComputeDone()
    {
        lock (item.Gate)
        {
            long done = 0;
            foreach (var s in item.Segments)
            {
                long upTo = s.End == long.MaxValue ? s.Position : Math.Min(s.Position, s.End + 1);
                done += Math.Max(0, upTo - s.Start);
            }
            return done;
        }
    }

    /// <summary>Gives the finished .edpart file its real name.</summary>
    private void Finish()
    {
        string target = item.FilePath;
        if (File.Exists(target))
        {
            item.FileName = PathUtil.UniqueName(item.Directory, item.FileName);
            target = item.FilePath;
        }
        File.Move(item.PartPath, target);
        if (DateTimeOffset.TryParse(item.LastModified, out var modified))
        {
            try { File.SetLastWriteTimeUtc(target, modified.UtcDateTime); } catch { /* the date is a courtesy */ }
        }
        lock (item.Gate) item.Segments = new List<Segment>();
        item.DoneBytes = item.TotalBytes;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* it will be overwritten */ }
    }
}
