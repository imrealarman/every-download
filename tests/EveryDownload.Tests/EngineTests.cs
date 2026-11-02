using System.Security.Cryptography;
using EveryDownload.Models;
using EveryDownload.Services;

namespace EveryDownload.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "everydownload-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestServer server = new();
    private readonly HttpClient http;

    public EngineTests()
    {
        Directory.CreateDirectory(root);
        http = HttpFactory.Create(new AppSettings { ProxyMode = "none" });
    }

    public void Dispose()
    {
        server.Dispose();
        http.Dispose();
        try { Directory.Delete(root, true); } catch { /* temp folder, the OS will clean it eventually */ }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private DownloadItem Item(string path, int connections = 0) => new()
    {
        Url = server.Url(path),
        Directory = root,
        FileName = Path.GetFileName(path),
        Connections = connections,
    };

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string Hash(string file) => Hash(File.ReadAllBytes(file));

    // ---- HTTP engine ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Downloads_a_file_over_several_connections_byte_for_byte()
    {
        var data = Bytes(8 * 1024 * 1024 + 123, 1);
        server.Add("/big.bin", data);
        server.SlowMs = 1;
        var item = Item("/big.bin", connections: 4);

        await new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(item.FilePath));
        Assert.True(server.PeakConcurrent >= 3, $"expected parallel connections, peak was {server.PeakConcurrent}");
        Assert.False(File.Exists(item.PartPath));
    }

    [Fact]
    public async Task A_finished_connection_takes_over_part_of_a_slower_range()
    {
        var data = Bytes(6 * 1024 * 1024, 2);
        server.Add("/split.bin", data);
        server.SlowMs = 1;
        var item = Item("/split.bin", connections: 2);

        await new SegmentedDownloader(item, http, new EngineOptions { MinSplitBytes = 64 * 1024 }).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(item.FilePath));
        Assert.Equal(data.Length, item.DoneBytes);
    }

    [Fact]
    public async Task Smart_boost_downloads_correctly()
    {
        var data = Bytes(10 * 1024 * 1024, 3);
        server.Add("/auto.bin", data);
        var item = Item("/auto.bin", connections: 0);

        await new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(item.FilePath));
    }

    [Fact]
    public async Task Falls_back_to_one_connection_when_the_server_ignores_ranges()
    {
        var data = Bytes(2 * 1024 * 1024, 4);
        server.Add("/norange.bin", data, ignoreRange: true);
        var item = Item("/norange.bin", connections: 4);

        await new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(item.FilePath));
        Assert.False(item.Resumable);
    }

    [Fact]
    public async Task Recovers_when_a_connection_drops_halfway()
    {
        var data = Bytes(3 * 1024 * 1024, 5);
        server.Add("/flaky.bin", data, dropFirstConnection: true);
        var item = Item("/flaky.bin", connections: 1);

        await new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(item.FilePath));
    }

    [Fact]
    public async Task Resumes_from_where_it_stopped_after_a_pause()
    {
        var data = Bytes(6 * 1024 * 1024, 6);
        server.Add("/resume.bin", data);
        server.SlowMs = 4;
        var item = Item("/resume.bin", connections: 2);

        using (var cts = new CancellationTokenSource())
        {
            var run = new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(cts.Token);
            while (item.DoneBytes < 1024 * 1024) await Task.Delay(50);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        long before = item.Segments.Sum(s => s.Position - s.Start);
        Assert.InRange(before, 1, data.Length - 1);
        Assert.True(File.Exists(item.PartPath));

        lock (server.Requests) server.Requests.Clear();
        server.SlowMs = 0;
        await new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(item.FilePath));
        long refetched = 0;
        lock (server.Requests) refetched = server.Requests.Count(r => r.From == 0);
        Assert.True(refetched <= 1, "the bytes already on disk should not be fetched again");
    }

    [Fact]
    public async Task Uses_the_file_name_the_server_suggests()
    {
        server.Add("/dl", Bytes(1000, 7));
        server.Add("/named.txt", Bytes(1000, 8));
        var item = new DownloadItem { Url = server.Url("/named.txt"), Directory = root, FileName = "ignored" };

        await new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None);

        Assert.Equal("named.txt", item.FileName);
        Assert.True(File.Exists(Path.Combine(root, "named.txt")));
    }

    [Fact]
    public async Task A_missing_file_fails_without_retrying_forever()
    {
        var item = Item("/nothing.bin");
        var ex = await Assert.ThrowsAsync<FatalDownloadException>(() => new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Another_mirror_is_used_when_the_first_address_is_dead()
    {
        var data = Bytes(1024 * 1024, 9);
        server.Add("/mirror.bin", data);
        var item = Item("/mirror.bin", connections: 2);
        item.Url = server.Url("/gone.bin");
        item.Mirrors.Add(server.Url("/mirror.bin"));
        item.NameLocked = true;
        item.FileName = "mirror.bin";

        await new SegmentedDownloader(item, http, new EngineOptions()).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(item.FilePath));
    }

    [Fact]
    public async Task The_speed_limit_slows_the_download_down()
    {
        var data = Bytes(1024 * 1024, 10);
        server.Add("/limited.bin", data);
        var item = Item("/limited.bin", connections: 1);
        var limiter = new RateLimiter { BytesPerSecond = 512 * 1024 };

        var started = DateTime.UtcNow;
        await new SegmentedDownloader(item, http, new EngineOptions { Global = limiter }).RunAsync(CancellationToken.None);

        Assert.True((DateTime.UtcNow - started).TotalSeconds > 1.0, "1 MB at 512 KB/s cannot take under a second");
        Assert.Equal(Hash(data), Hash(item.FilePath));
    }

    // ---- HLS --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Downloads_and_joins_an_hls_stream_including_encrypted_pieces()
    {
        var key = Bytes(16, 11);
        var plain = Enumerable.Range(0, 4).Select(i => Bytes(50_000, 100 + i)).ToList();
        for (int i = 0; i < plain.Count; i++)
        {
            byte[] stored = plain[i];
            if (i >= 2) // the last two pieces are AES-128 encrypted with the sequence number as IV
            {
                var iv = new byte[16];
                iv[15] = (byte)i;
                using var aes = Aes.Create();
                aes.Key = key;
                stored = aes.EncryptCbc(plain[i], iv, PaddingMode.PKCS7);
            }
            server.Add($"/hls/seg{i}.ts", stored);
        }
        server.Add("/hls/key.bin", key);
        var m3u8 = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:4,\nseg0.ts\n#EXTINF:4,\nseg1.ts\n" +
                   "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\nseg2.ts\n#EXTINF:4,\nseg3.ts\n#EXT-X-ENDLIST\n";
        server.Add("/hls/video.m3u8", System.Text.Encoding.UTF8.GetBytes(m3u8));
        var item = new DownloadItem { Url = server.Url("/hls/video.m3u8"), Kind = DownloadKind.Hls, Directory = root, FileName = "video" };

        await new HlsDownloader(item, http, new EngineOptions(), remux: false).RunAsync(CancellationToken.None);

        Assert.EndsWith(".ts", item.FileName);
        Assert.Equal(Hash(plain.SelectMany(p => p).ToArray()), Hash(item.FilePath));
        Assert.False(Directory.Exists(Path.Combine(root, "video.edparts")));
    }

    // ---- manager ----------------------------------------------------------------------------------------------

    private DownloadManager Manager(out AppSettings settings)
    {
        settings = new AppSettings { BaseFolder = root, ProxyMode = "none", ScanDownloads = false, AutoCategorize = true };
        return new DownloadManager(settings, Path.Combine(root, "list.json"), autoLoad: false);
    }

    private static async Task WaitFor(Func<bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > until) throw new TimeoutException("the condition was not reached in time"); await Task.Delay(50); }
    }

    [Fact]
    public async Task The_manager_runs_a_download_sorts_it_into_a_category_and_verifies_the_checksum()
    {
        var data = Bytes(500_000, 12);
        server.Add("/movie.mp4", data);
        using var manager = Manager(out _);
        var request = new AddRequest { ExpectedHash = Hash(data) };
        request.Urls.Add(server.Url("/movie.mp4"));

        var item = manager.Add(request).Single();
        await WaitFor(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(Categories.Video, item.Category);
        Assert.Equal(Path.Combine(root, "Video"), item.Directory);
        Assert.Equal(Hash(data), Hash(item.FilePath));
    }

    [Fact]
    public async Task A_wrong_checksum_marks_the_download_as_failed()
    {
        server.Add("/doc.pdf", Bytes(10_000, 13));
        using var manager = Manager(out _);
        var request = new AddRequest { ExpectedHash = new string('0', 64) };
        request.Urls.Add(server.Url("/doc.pdf"));

        var item = manager.Add(request).Single();
        await WaitFor(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Contains("checksum", item.Error);
    }

    [Fact]
    public async Task Only_the_allowed_number_of_downloads_run_at_once_and_the_rest_wait()
    {
        server.SlowMs = 5;
        var request = new AddRequest();
        for (int i = 0; i < 4; i++) { server.Add($"/q{i}.bin", Bytes(400_000, 20 + i)); request.Urls.Add(server.Url($"/q{i}.bin")); }
        using var manager = Manager(out var settings);
        settings.MaxSimultaneous = 2;

        var items = manager.Add(request);
        await Task.Delay(300);
        Assert.True(items.Count(i => i.Status == DownloadStatus.Downloading) <= 2);

        await WaitFor(() => items.All(i => i.Status == DownloadStatus.Completed), 60);
    }

    [Fact]
    public async Task A_paused_download_can_be_started_again_and_finishes()
    {
        var data = Bytes(4 * 1024 * 1024, 30);
        server.Add("/pause.bin", data);
        server.SlowMs = 4;
        using var manager = Manager(out _);
        var request = new AddRequest { Connections = 2 };
        request.Urls.Add(server.Url("/pause.bin"));
        var item = manager.Add(request).Single();

        await WaitFor(() => item.DoneBytes > 500_000);
        manager.Pause(item);
        await WaitFor(() => item.Status == DownloadStatus.Paused);

        server.SlowMs = 0;
        manager.Start(item);
        await WaitFor(() => item.Status == DownloadStatus.Completed);
        Assert.Equal(Hash(data), Hash(item.FilePath));
    }

    [Fact]
    public async Task The_list_survives_a_restart_with_its_progress()
    {
        server.Add("/saved.bin", Bytes(3 * 1024 * 1024, 31));
        server.SlowMs = 6;
        string store = Path.Combine(root, "list.json");
        var settings = new AppSettings { BaseFolder = root, ProxyMode = "none", ScanDownloads = false };
        using (var first = new DownloadManager(settings, store, autoLoad: false))
        {
            var request = new AddRequest { Connections = 2 };
            request.Urls.Add(server.Url("/saved.bin"));
            var item = first.Add(request).Single();
            await WaitFor(() => item.DoneBytes > 300_000);
            await first.ShutdownAsync();
        }

        server.SlowMs = 0;
        using var second = new DownloadManager(settings, store);
        var restored = second.Snapshot().Single();
        Assert.True(restored.DoneBytes > 0 || restored.Segments.Any(s => s.Position > s.Start));
        second.Start(restored);
        await WaitFor(() => restored.Status == DownloadStatus.Completed);
        Assert.Equal(3 * 1024 * 1024, new FileInfo(restored.FilePath).Length);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    [Fact]
    public void Url_patterns_expand_numbers_letters_and_steps()
    {
        Assert.Equal(new[] { "p01.jpg", "p02.jpg", "p03.jpg" }, UrlPattern.Expand("p[01-03].jpg"));
        Assert.Equal(new[] { "x1", "x3", "x5" }, UrlPattern.Expand("x[1-5:2]"));
        Assert.Equal(new[] { "a-1", "a-2", "b-1", "b-2" }, UrlPattern.Expand("[a-b]-[1-2]"));
        Assert.Equal(new[] { "plain" }, UrlPattern.Expand("plain"));
        Assert.Equal(UrlPattern.Limit, UrlPattern.Expand("f[1-999999]").Count);
    }

    [Fact]
    public void Schedule_window_handles_overnight_ranges_and_days()
    {
        var s = new AppSettings { ScheduleEnabled = true, ScheduleStart = "22:00", ScheduleStop = "06:00", ScheduleStopEnabled = true };
        var monday = new DateTime(2026, 11, 2); // a Monday
        Assert.True(ScheduleRules.IsOpen(s, monday.AddHours(23)));
        Assert.True(ScheduleRules.IsOpen(s, monday.AddDays(1).AddHours(5)));
        Assert.False(ScheduleRules.IsOpen(s, monday.AddHours(12)));

        s.ScheduleDays = 1 << (int)DayOfWeek.Saturday;
        Assert.False(ScheduleRules.IsOpen(s, monday.AddHours(23)));
        Assert.True(ScheduleRules.IsOpen(new AppSettings(), monday)); // no schedule: always open
    }

    [Fact]
    public void File_names_from_servers_cannot_escape_the_download_folder()
    {
        Assert.Equal("evil.exe", PathUtil.SafeName("..\\..\\evil.exe"));
        Assert.Equal("evil.exe", PathUtil.SafeName("../../evil.exe"));
        Assert.Equal("_CON.txt", PathUtil.SafeName("CON.txt"));
        Assert.Equal("a_b.txt", PathUtil.SafeName("a:b.txt"));
        Assert.Equal("my file.zip", PathUtil.NameFromUrl("http://x.com/dir/my%20file.zip?token=1"));
    }

    [Fact]
    public void Launch_arguments_from_the_browser_are_understood()
    {
        var r = LaunchRequest.Parse(new[] { "--add", "http://x/y.zip", "--referer", "http://x/", "--cookie", "a=b", "--name", "y.zip", "magnet:?xt=urn:btih:abc", "--grab", "http://x/page" });
        Assert.Equal(2, r.Adds.Count);
        Assert.Equal("http://x/", r.Adds[0].Referer);
        Assert.Equal("a=b", r.Adds[0].Cookie);
        Assert.Equal("y.zip", r.Adds[0].FileName);
        Assert.Equal("http://x/page", r.GrabUrl);
        Assert.Equal(DownloadKind.Torrent, PathUtil.KindOf(r.Adds[1].Urls[0]));
    }

    [Fact]
    public async Task The_site_grabber_lists_files_and_skips_pages()
    {
        var found = await SiteGrabber.GrabAsync(http, server.Url("/page"), new DownloadItem(), AppSettings.DefaultUserAgent, null, 1, CancellationToken.None);
        Assert.Equal(new[] { "a.zip", "b.pdf", "pic.png" }.OrderBy(x => x), found.Select(f => f.Name).OrderBy(x => x));

        var onlyZip = await SiteGrabber.GrabAsync(http, server.Url("/page"), new DownloadItem(), AppSettings.DefaultUserAgent, new[] { "zip" }, 1, CancellationToken.None);
        Assert.Single(onlyZip);
    }

    [Fact]
    public async Task Checksums_are_recognised_by_length()
    {
        string file = Path.Combine(root, "h.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        Assert.Equal(HashAlgorithmName.SHA256, Integrity.AlgorithmFor(new string('a', 64)));
        Assert.Equal(HashAlgorithmName.MD5, Integrity.AlgorithmFor(new string('a', 32)));
        Assert.Null(Integrity.AlgorithmFor("abc"));
        Assert.Equal("abc123", Integrity.Clean("SHA256: ABC123  file.zip"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(), await Integrity.HashAsync(file, HashAlgorithmName.SHA256, CancellationToken.None));
    }
}
