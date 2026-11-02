using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EveryDownload.Models;

namespace EveryDownload.Services;

/// <summary>
/// Downloads an HLS stream (.m3u8): picks the best quality, fetches the pieces side by side (decrypting AES-128 ones),
/// and joins them into one file. Finished pieces stay on disk, so a paused or interrupted download carries on from them.
/// </summary>
public sealed class HlsDownloader
{
    private sealed record Piece(int Index, string Url, byte[]? Key, byte[]? Iv, double Seconds);
    private sealed record Playlist(List<Piece> Pieces, string? InitUrl, bool Ended);

    private readonly DownloadItem item;
    private readonly HttpClient http;
    private readonly EngineOptions options;
    private readonly bool remux;

    public HlsDownloader(DownloadItem item, HttpClient http, EngineOptions options, bool remux)
    {
        this.item = item;
        this.http = http;
        this.options = options;
        this.remux = remux;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(item.Directory);
        item.Error = "";
        item.Note = "Reading playlist…";

        var playlist = await LoadAsync(item.Url, ct).ConfigureAwait(false);
        if (!playlist.Ended) throw new FatalDownloadException("This is a live stream, which cannot be saved as a file.");
        if (playlist.Pieces.Count == 0) throw new FatalDownloadException("The playlist has no video pieces.");

        string work = item.FilePath + ".edparts";
        System.IO.Directory.CreateDirectory(work);
        string PartFile(int i) => Path.Combine(work, i.ToString("D6") + ".seg");

        // Pieces already on disk from an earlier run are kept.
        long done = 0;
        int finished = 0;
        var todo = new Queue<Piece>();
        foreach (var piece in playlist.Pieces)
        {
            string path = PartFile(piece.Index);
            if (File.Exists(path)) { done += new FileInfo(path).Length; finished++; }
            else todo.Enqueue(piece);
        }
        if (playlist.InitUrl != null && !File.Exists(Path.Combine(work, "init.seg")))
            await FetchAsync(playlist.InitUrl, null, null, Path.Combine(work, "init.seg"), ct).ConfigureAwait(false);

        item.Note = "";
        item.Resumable = true;
        long counted = done;
        int total = playlist.Pieces.Count;
        void Report()
        {
            long bytes = Interlocked.Read(ref counted);
            int pieces = Volatile.Read(ref finished);
            item.DoneBytes = bytes;
            item.TotalBytes = pieces == 0 ? -1 : Math.Max(bytes, bytes * total / pieces); // an estimate that firms up as pieces arrive
        }
        Report();

        var gate = new object();
        int workers = Math.Clamp(item.Connections > 0 ? item.Connections : options.DefaultConnections > 0 ? options.DefaultConnections : 8, 1, 32);
        var clock = Stopwatch.StartNew();
        long lastBytes = counted;
        double lastTime = 0, ema = 0;
        var history = new List<double>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Worker()
        {
            Interlocked.Increment(ref active);
            try
            {
                while (true)
                {
                    Piece piece;
                    lock (gate) { if (todo.Count == 0) return; piece = todo.Dequeue(); }
                    long size = await FetchAsync(piece.Url, piece.Key, piece.Iv, PartFile(piece.Index), linked.Token).ConfigureAwait(false);
                    Interlocked.Add(ref counted, size);
                    Interlocked.Increment(ref finished);
                }
            }
            finally { Interlocked.Decrement(ref active); }
        }

        var tasks = Enumerable.Range(0, Math.Min(workers, todo.Count)).Select(_ => Task.Run(Worker)).ToList();
        while (tasks.Count > 0)
        {
            await Task.WhenAny(Task.WhenAny(tasks), Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
            var failed = tasks.FirstOrDefault(t => t.IsFaulted);
            if (failed != null || ct.IsCancellationRequested)
            {
                linked.Cancel();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* reported below */ }
                ct.ThrowIfCancellationRequested();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed!.Exception!.GetBaseException()).Throw();
            }
            tasks.RemoveAll(t => t.IsCompleted);

            double now = clock.Elapsed.TotalSeconds;
            if (now - lastTime >= 0.5)
            {
                long bytes = Interlocked.Read(ref counted);
                double instant = (bytes - lastBytes) / (now - lastTime);
                ema = ema == 0 ? instant : ema * 0.6 + instant * 0.4;
                lastBytes = bytes;
                lastTime = now;
                item.Speed = ema;
                item.ActiveConnections = Volatile.Read(ref active);
                history.Add(ema);
                if (history.Count > 120) history.RemoveAt(0);
                item.SpeedHistory = history.ToArray();
                Report();
            }
        }
        Report();
        item.Speed = 0;
        item.ActiveConnections = 0;

        item.Note = "Joining pieces…";
        string joined = item.FilePath + ".edpart";
        using (var output = new FileStream(joined, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            string init = Path.Combine(work, "init.seg");
            if (File.Exists(init)) { using var s = File.OpenRead(init); await s.CopyToAsync(output, ct).ConfigureAwait(false); }
            foreach (var piece in playlist.Pieces)
            {
                using var s = File.OpenRead(PartFile(piece.Index));
                await s.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }

        // Pieces of fragmented MP4 streams join into a playable .mp4; transport-stream pieces into a .ts.
        string extension = playlist.InitUrl != null ? ".mp4" : ".ts";
        string stem = Path.GetFileNameWithoutExtension(item.FileName);
        string final = PathUtil.UniqueName(item.Directory, stem + extension);
        File.Move(joined, Path.Combine(item.Directory, final));
        item.FileName = final;
        item.Category = Categories.Video;

        if (remux && extension == ".ts" && Ffmpeg.Find() is { } ffmpeg)
        {
            item.Note = "Converting to MP4…";
            string mp4 = Path.Combine(item.Directory, PathUtil.UniqueName(item.Directory, stem + ".mp4"));
            if (await Ffmpeg.RemuxAsync(ffmpeg, item.FilePath, mp4, ct).ConfigureAwait(false))
            {
                File.Delete(item.FilePath);
                item.FileName = Path.GetFileName(mp4);
            }
        }
        item.Note = "";
        try { System.IO.Directory.Delete(work, true); } catch { /* leftover pieces are harmless */ }
        item.TotalBytes = new FileInfo(item.FilePath).Length;
        item.DoneBytes = item.TotalBytes;
    }

    private int active;

    // ---- playlist parsing -------------------------------------------------------------------------------------

    private async Task<Playlist> LoadAsync(string url, CancellationToken ct)
    {
        string text = await GetTextAsync(url, ct).ConfigureAwait(false);
        if (!text.TrimStart().StartsWith("#EXTM3U")) throw new FatalDownloadException("That address is not a video playlist.");

        if (text.Contains("#EXT-X-STREAM-INF"))
        {
            // A master playlist lists the qualities: take the one with the highest bandwidth.
            string? best = null;
            long bestBandwidth = -1;
            var lines = text.Split('\n').Select(l => l.Trim()).ToList();
            for (int i = 0; i < lines.Count - 1; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF")) continue;
                var m = Regex.Match(lines[i], @"BANDWIDTH=(\d+)");
                long bandwidth = m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                int next = i + 1;
                while (next < lines.Count && (lines[next].Length == 0 || lines[next].StartsWith('#'))) next++;
                if (next < lines.Count && bandwidth > bestBandwidth) { bestBandwidth = bandwidth; best = lines[next]; }
            }
            if (best == null) throw new FatalDownloadException("The playlist lists no video.");
            return await LoadAsync(new Uri(new Uri(url), best).ToString(), ct).ConfigureAwait(false);
        }
        return await ParseAsync(text, url, ct).ConfigureAwait(false);
    }

    private async Task<Playlist> ParseAsync(string text, string baseUrl, CancellationToken ct)
    {
        var pieces = new List<Piece>();
        string? initUrl = null;
        bool ended = false;
        long sequence = 0;
        string? keyUri = null;
        byte[]? iv = null, key = null;
        double seconds = 0;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) sequence = long.Parse(line[22..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("#EXT-X-ENDLIST")) ended = true;
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var m = Regex.Match(line, "URI=\"([^\"]+)\"");
                if (m.Success) initUrl = new Uri(new Uri(baseUrl), m.Groups[1].Value).ToString();
            }
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                string method = Regex.Match(line, "METHOD=([^,]+)").Groups[1].Value;
                if (method == "NONE") { key = null; keyUri = null; iv = null; continue; }
                if (method != "AES-128") throw new FatalDownloadException($"This stream uses {method} protection, which is not supported.");
                var uri = Regex.Match(line, "URI=\"([^\"]+)\"");
                var ivMatch = Regex.Match(line, "IV=0[xX]([0-9a-fA-F]+)");
                string newKeyUri = new Uri(new Uri(baseUrl), uri.Groups[1].Value).ToString();
                if (newKeyUri != keyUri) { key = await GetBytesAsync(newKeyUri, ct).ConfigureAwait(false); keyUri = newKeyUri; }
                iv = ivMatch.Success ? Convert.FromHexString(ivMatch.Groups[1].Value.PadLeft(32, '0')) : null;
            }
            else if (line.StartsWith("#EXTINF:"))
            {
                double.TryParse(line[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            }
            else if (!line.StartsWith('#'))
            {
                byte[]? pieceIv = null;
                if (key != null)
                {
                    pieceIv = iv ?? new byte[16];
                    if (iv == null) // with no IV given, the piece's sequence number is the IV
                        for (int i = 0; i < 8; i++) pieceIv[15 - i] = (byte)(sequence >> (8 * i));
                }
                pieces.Add(new Piece(pieces.Count, new Uri(new Uri(baseUrl), line).ToString(), key, pieceIv, seconds));
                sequence++;
            }
        }
        return new Playlist(pieces, initUrl, ended);
    }

    // ---- fetching ---------------------------------------------------------------------------------------------

    private async Task<string> GetTextAsync(string url, CancellationToken ct) => System.Text.Encoding.UTF8.GetString(await GetBytesAsync(url, ct).ConfigureAwait(false));

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var request = HttpFactory.Request(url, item, options.UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        Probe.ThrowIfBad(response);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Downloads one piece to <paramref name="path"/> (decrypting it), retrying a few times. Returns its size.</summary>
    private async Task<long> FetchAsync(string url, byte[]? key, byte[]? iv, string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = HttpFactory.Request(url, item, options.UserAgent);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                Probe.ThrowIfBad(response);
                byte[] data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (options.Global != null) await options.Global.WaitAsync(data.Length, ct).ConfigureAwait(false);
                if (key != null && iv != null)
                {
                    using var aes = Aes.Create();
                    aes.Key = key;
                    data = aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
                }
                string temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true); // a half-written piece never looks finished
                return data.Length;
            }
            catch (Exception ex) when (attempt < options.MaxRetries && ex is HttpRequestException or IOException or CryptographicException && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << attempt)), ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Finds ffmpeg (next to the app or on PATH) so streams can be turned into plain MP4 files.</summary>
public static class Ffmpeg
{
    public static string? Find()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* a malformed PATH entry */ }
        }
        return null;
    }

    public static async Task<bool> RemuxAsync(string ffmpeg, string input, string output, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in new[] { "-y", "-v", "error", "-i", input, "-c", "copy", "-movflags", "+faststart", output }) info.ArgumentList.Add(a);
            using var process = Process.Start(info);
            if (process == null) return false;
            _ = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write("ffmpeg could not convert the stream", ex);
            return false;
        }
    }
}
