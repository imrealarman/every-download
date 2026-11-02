using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EveryDownload.Models;

namespace EveryDownload.Services;

/// <summary>Builds the shared HttpClient (proxy, no automatic decompression) and the per-download requests.</summary>
public static class HttpFactory
{
    public static HttpClient Create(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            // Ranges address the bytes on the wire, so a server must never gzip the body behind our back.
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            UseCookies = false, // cookies come from the browser, per download
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 64,
        };
        switch (settings.ProxyMode)
        {
            case "none":
                handler.UseProxy = false;
                break;
            case "manual" when settings.ProxyHost.Trim().Length > 0:
                var proxy = new WebProxy(settings.ProxyHost.Trim(), settings.ProxyPort);
                if (settings.ProxyUser.Length > 0) proxy.Credentials = new NetworkCredential(settings.ProxyUser, settings.ProxyPassword);
                handler.Proxy = proxy;
                handler.UseProxy = true;
                break;
            default:
                handler.UseProxy = true; // the Windows proxy settings
                handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                break;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>A GET for <paramref name="url"/> carrying the download's referrer, cookies, headers and login.</summary>
    public static HttpRequestMessage Request(string url, DownloadItem item, string defaultUserAgent)
    {
        var uri = new Uri(url);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", item.UserAgent.Length > 0 ? item.UserAgent : defaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (Uri.TryCreate(item.Referer, UriKind.Absolute, out var referer)) request.Headers.Referrer = referer;
        if (item.Cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", item.Cookie);

        foreach (var line in item.Headers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) request.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        string user = item.Username, password = item.Password;
        if (user.Length == 0 && uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        if (user.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        return request;
    }
}
