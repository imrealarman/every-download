using System.ComponentModel;
using System.Text.Json.Serialization;

namespace EveryDownload.Models;

public enum DownloadKind { Http, Ftp, Hls, Torrent }

/// <summary>Where a download stands in the queue. Higher goes first.</summary>
public enum DownloadPriority { Low = -1, Normal = 0, High = 1 }

public enum DownloadStatus { Queued, Downloading, Paused, Verifying, Scanning, Completed, Failed }

/// <summary>
/// One byte range of a file, owned by one connection at a time. Ranges are split on the fly when a connection runs out
/// of work, so a fast server keeps every connection busy until the very last byte.
/// </summary>
public sealed class Segment
{
    // Connections read and move these while other threads look at them, so every access is a volatile one.
    private long start, end, position;

    public long Start { get => Volatile.Read(ref start); set => Volatile.Write(ref start, value); }
    /// <summary>Last byte of the range (inclusive). <see cref="long.MaxValue"/> while the length is unknown.</summary>
    public long End { get => Volatile.Read(ref end); set => Volatile.Write(ref end, value); }
    /// <summary>Next byte to write; everything before it is on disk.</summary>
    public long Position { get => Volatile.Read(ref position); set => Volatile.Write(ref position, value); }

    [JsonIgnore] public bool Busy { get; set; }
    [JsonIgnore] public int Failures { get; set; }

    [JsonIgnore] public bool IsDone => Position > End;
    [JsonIgnore] public long Remaining => End == long.MaxValue ? long.MaxValue : Math.Max(0, End - Position + 1);
}

/// <summary>
/// A download and everything the app knows about it. The engine updates the plain properties from background threads;
/// the window calls <see cref="Refresh"/> on its own timer, so bindings only ever change on the UI thread.
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what to fetch ----------------------------------------------------------------------------------------
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    /// <summary>Other places that serve the same file; connections are spread across them.</summary>
    public List<string> Mirrors { get; set; } = new();
    public DownloadKind Kind { get; set; }
    public string Referer { get; set; } = "";
    public string Cookie { get; set; } = "";
    public string UserAgent { get; set; } = "";
    /// <summary>Extra request headers, one "Name: value" per line.</summary>
    public string Headers { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    // ---- where it goes ----------------------------------------------------------------------------------------
    public string FileName { get; set; } = "download";
    public string Directory { get; set; } = "";
    public string Category { get; set; } = Categories.Other;
    /// <summary>The user picked the name or folder; probing the server must not change it.</summary>
    public bool NameLocked { get; set; }
    public bool DirectoryLocked { get; set; }

    // ---- options ----------------------------------------------------------------------------------------------
    /// <summary>Connections for this download; 0 lets the engine choose (and boost) on its own.</summary>
    public int Connections { get; set; }
    /// <summary>Speed limit for this download in KB/s; 0 is unlimited.</summary>
    public int SpeedLimitKb { get; set; }
    public DateTime? StartAt { get; set; }
    public DownloadPriority Priority { get; set; } = DownloadPriority.Normal;
    public string ExpectedHash { get; set; } = "";

    // ---- progress ---------------------------------------------------------------------------------------------
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long TotalBytes { get; set; } = -1;
    public long DoneBytes { get; set; }
    public bool Probed { get; set; }
    public bool Resumable { get; set; }
    public string ETag { get; set; } = "";
    public string LastModified { get; set; } = "";
    public List<Segment> Segments { get; set; } = new();
    public string Error { get; set; } = "";
    public string Note { get; set; } = "";
    public string ActualHash { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    // ---- live state, never saved ------------------------------------------------------------------------------
    [JsonIgnore] public object Gate { get; } = new();
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public int ActiveConnections { get; set; }
    [JsonIgnore] public int Attempts { get; set; }
    /// <summary>Start as soon as possible, ignoring the simultaneous-download limit and the schedule.</summary>
    [JsonIgnore] public bool Force { get; set; }
    [JsonIgnore] public DateTime? RetryAt { get; set; }
    [JsonIgnore] public double[] SpeedHistory { get; set; } = Array.Empty<double>();

    [JsonIgnore] public string FilePath => System.IO.Path.Combine(Directory, FileName);
    [JsonIgnore] public string PartPath => FilePath + ".edpart";
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Downloading or DownloadStatus.Verifying or DownloadStatus.Scanning;
    [JsonIgnore] public bool IsFinished => Status == DownloadStatus.Completed;
    [JsonIgnore] public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    [JsonIgnore] public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Failed;

    [JsonIgnore]
    public double Percent => TotalBytes > 0 ? Math.Clamp(DoneBytes * 100.0 / TotalBytes, 0, 100) : (IsFinished ? 100 : 0);
    [JsonIgnore] public bool Indeterminate => Status == DownloadStatus.Downloading && TotalBytes <= 0;
    [JsonIgnore] public TimeSpan? Eta => Speed > 1 && TotalBytes > 0 ? TimeSpan.FromSeconds(Math.Max(0, TotalBytes - DoneBytes) / Speed) : null;

    // ---- text for the list ------------------------------------------------------------------------------------
    [JsonIgnore] public string Glyph => Kind == DownloadKind.Torrent ? Categories.Glyph(Categories.Torrents) : Categories.Glyph(Category);
    [JsonIgnore] public string PercentText => IsFinished ? "100%" : TotalBytes > 0 ? $"{Percent:0.#}%" : "";
    [JsonIgnore] public string SpeedText => Status == DownloadStatus.Downloading && Speed > 1 ? Format.Speed(Speed) : "";
    [JsonIgnore] public string EtaText => Status == DownloadStatus.Downloading && Eta is { } e ? Format.Duration(e) + " left" : "";
    [JsonIgnore] public string ConnectionsText => Status == DownloadStatus.Downloading && ActiveConnections > 0
        ? (Kind == DownloadKind.Torrent ? $"{ActiveConnections} peers" : $"{ActiveConnections} connection{(ActiveConnections == 1 ? "" : "s")}") : "";
    [JsonIgnore] public string SizeText => TotalBytes > 0
        ? (IsFinished ? Format.Bytes(TotalBytes) : $"{Format.Bytes(DoneBytes)} of {Format.Bytes(TotalBytes)}")
        : (DoneBytes > 0 ? Format.Bytes(DoneBytes) : "Unknown size");

    [JsonIgnore]
    public string StatusText => Status switch
    {
        DownloadStatus.Downloading => Note.Length > 0 ? Note : "Downloading",
        DownloadStatus.Queued when RetryAt is { } r && r > DateTime.Now => $"{Error} Retrying in {Math.Max(1, (int)(r - DateTime.Now).TotalSeconds)} s",
        DownloadStatus.Queued when StartAt is { } s && s > DateTime.Now => "Scheduled for " + (s.Date == DateTime.Today ? s.ToString("t") : s.ToString("g")),
        DownloadStatus.Queued => "Waiting in queue",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Verifying => "Verifying checksum…",
        DownloadStatus.Scanning => "Scanning for viruses…",
        DownloadStatus.Completed => Note.Length > 0 ? "Done · " + Note : "Done",
        DownloadStatus.Failed => Error.Length > 0 ? "Failed · " + Error : "Failed",
        _ => "",
    };

    [JsonIgnore] public bool ShowProgress => Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Verifying or DownloadStatus.Scanning
        || (Status == DownloadStatus.Queued && DoneBytes > 0);
    /// <summary>The glyph of the row's main button: pause while running, play when stopped, open when done.</summary>
    [JsonIgnore] public string PrimaryGlyph => Status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Verifying or DownloadStatus.Scanning => "",
        DownloadStatus.Completed => "",
        DownloadStatus.Queued => "",
        _ => "",
    };
    [JsonIgnore] public string PrimaryTip => Status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Queued => "Pause",
        DownloadStatus.Completed => "Open file",
        DownloadStatus.Verifying or DownloadStatus.Scanning => "Working…",
        _ => "Resume",
    };

    [JsonIgnore] public string PriorityText => Priority switch { DownloadPriority.High => "▲ High priority", DownloadPriority.Low => "▼ Low priority", _ => "" };

    [JsonIgnore] public string DetailLine => string.Join("  ·  ", new[] { PriorityText, SizeText, SpeedText, EtaText, ConnectionsText }.Where(s => s.Length > 0));

    /// <summary>Tells the bindings that something changed. Call on the UI thread.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
