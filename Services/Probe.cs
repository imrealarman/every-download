using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using EveryDownload.Models;

namespace EveryDownload.Services;

/// <summary>What the server told us about a file before we download it.</summary>
public sealed record ProbeResult(long Size, bool Resumable, string FileName, string? ContentType, string ETag, string LastModified);

/// <summary>Raised for failures that retrying will not fix (not found, no access, link expired).</summary>
public sealed class FatalDownloadException : Exception
{
    public FatalDownloadException(string message) : base(message) { }
}

/// <summary>Asks for the first byte of a file to learn its size, name and whether the server can resume.</summary>
public static class Probe
{
    public static async Task<ProbeResult> RunAsync(HttpClient http, string url, DownloadItem item, string userAgent, CancellationToken ct)
    {
        using var request = HttpFactory.Request(url, item, userAgent);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        ThrowIfBad(response);

        long size = -1;
        bool resumable = false;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            size = response.Content.Headers.ContentRange?.Length ?? -1;
            resumable = size > 0;
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            size = response.Content.Headers.ContentRange?.Length ?? 0; // an empty file cannot satisfy "byte 0"
            resumable = true;
        }
        else
        {
            size = response.Content.Headers.ContentLength ?? -1;
            // Some servers answer 200 but still take ranges later; the engine falls back if they turn out not to.
            resumable = size > 0 && response.Headers.AcceptRanges.Contains("bytes");
        }

        string name = FileNameFrom(response, url);
        return new ProbeResult(size, resumable, name, response.Content.Headers.ContentType?.MediaType,
            response.Headers.ETag is { IsWeak: false } tag ? tag.Tag : "",
            response.Content.Headers.LastModified?.ToString("R") ?? "");
    }

    /// <summary>Throws a <see cref="FatalDownloadException"/> for answers that will not change on retry.</summary>
    public static void ThrowIfBad(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return;
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound: throw new FatalDownloadException("The file was not found (404).");
            case HttpStatusCode.Gone: throw new FatalDownloadException("The link has expired (410). Refresh the link and resume.");
            case HttpStatusCode.Forbidden: throw new FatalDownloadException("The server refused access (403). The link may have expired; refresh it and resume.");
            case HttpStatusCode.Unauthorized: throw new FatalDownloadException("A user name and password are needed (401).");
            case HttpStatusCode.ProxyAuthenticationRequired: throw new FatalDownloadException("The proxy needs a user name and password (407).");
        }
        throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
    }

    public static string FileNameFrom(HttpResponseMessage response, string url)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        string? name = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            // Some servers send a header HttpClient cannot parse; read the raw text.
            if (response.Content.Headers.TryGetValues("Content-Disposition", out var raw))
            {
                var m = Regex.Match(string.Join(";", raw), "filename\\*?=(?:UTF-8'')?\"?([^\";]+)\"?", RegexOptions.IgnoreCase);
                if (m.Success) name = m.Groups[1].Value;
            }
        }
        name = string.IsNullOrWhiteSpace(name) ? PathUtil.NameFromUrl(response.RequestMessage?.RequestUri?.ToString() ?? url) : name.Trim('"');
        return PathUtil.WithExtension(PathUtil.SafeName(name), response.Content.Headers.ContentType?.MediaType);
    }
}
