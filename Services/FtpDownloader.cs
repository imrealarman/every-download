using System.Diagnostics;
using System.Net;
using EveryDownload.Models;

namespace EveryDownload.Services;

/// <summary>Downloads one file over FTP on a single connection, resuming from the bytes already on disk.</summary>
public sealed class FtpDownloader
{
    private readonly DownloadItem item;
    private readonly EngineOptions options;

    public FtpDownloader(DownloadItem item, EngineOptions options)
    {
        this.item = item;
        this.options = options;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(item.Directory);
        item.Error = "";
        var uri = new Uri(item.Url);
        var credentials = Credentials(uri);

        if (!item.Probed)
        {
            item.Note = "Contacting server…";
            var size = (FtpWebRequest)WebRequest.Create(uri);
            size.Method = WebRequestMethods.Ftp.GetFileSize;
            size.Credentials = credentials;
            size.EnableSsl = uri.Scheme == "ftps";
            try
            {
                using var response = (FtpWebResponse)await size.GetResponseAsync().ConfigureAwait(false);
                item.TotalBytes = response.ContentLength;
            }
            catch (WebException ex) { throw Translate(ex); }
            item.Resumable = true;
            if (!item.NameLocked) item.FileName = PathUtil.SafeName(uri.Segments[^1], "download");
            item.Probed = true;
        }
        item.Note = "";

        long existing = File.Exists(item.PartPath) ? new FileInfo(item.PartPath).Length : 0;
        if (item.TotalBytes > 0 && existing > item.TotalBytes) existing = 0;

        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = WebRequestMethods.Ftp.DownloadFile;
        request.Credentials = credentials;
        request.EnableSsl = uri.Scheme == "ftps";
        request.UseBinary = true;
        request.ContentOffset = existing;

        FtpWebResponse reply;
        try { reply = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
        catch (WebException ex) { throw Translate(ex); }

        using (reply)
        using (var network = reply.GetResponseStream())
        using (var file = new FileStream(item.PartPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true))
        {
            var buffer = new byte[64 * 1024];
            long done = existing, last = done;
            var clock = Stopwatch.StartNew();
            double lastTime = 0, ema = 0;
            var history = new List<double>();
            item.ActiveConnections = 1;
            while (true)
            {
                int read = await network.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (options.Global != null) await options.Global.WaitAsync(read, ct).ConfigureAwait(false);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                item.DoneBytes = done;
                double now = clock.Elapsed.TotalSeconds;
                if (now - lastTime >= 0.5)
                {
                    double instant = (done - last) / (now - lastTime);
                    ema = ema == 0 ? instant : ema * 0.6 + instant * 0.4;
                    item.Speed = ema;
                    history.Add(ema);
                    if (history.Count > 120) history.RemoveAt(0);
                    item.SpeedHistory = history.ToArray();
                    last = done;
                    lastTime = now;
                }
            }
            if (item.TotalBytes <= 0) item.TotalBytes = done;
            else if (done < item.TotalBytes) throw new IOException("The connection closed before the file was complete.");
        }
        item.Speed = 0;
        item.ActiveConnections = 0;

        string name = File.Exists(item.FilePath) ? PathUtil.UniqueName(item.Directory, item.FileName) : item.FileName;
        File.Move(item.PartPath, Path.Combine(item.Directory, name));
        item.FileName = name;
        item.DoneBytes = item.TotalBytes;
    }

    private NetworkCredential Credentials(Uri uri)
    {
        if (item.Username.Length > 0) return new NetworkCredential(item.Username, item.Password);
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            return new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return new NetworkCredential("anonymous", "everydownload@example.com");
    }

    private static Exception Translate(WebException ex)
    {
        if (ex.Response is FtpWebResponse r)
        {
            return r.StatusCode switch
            {
                FtpStatusCode.ActionNotTakenFileUnavailable => new FatalDownloadException("The file was not found on the FTP server."),
                FtpStatusCode.NotLoggedIn => new FatalDownloadException("The FTP server needs a user name and password."),
                _ => new IOException("FTP error: " + r.StatusDescription?.Trim(), ex),
            };
        }
        return new IOException(ex.Message, ex);
    }
}
