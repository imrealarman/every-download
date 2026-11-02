using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryDownload.Services;

/// <summary>User choices, kept as JSON in %AppData%\EveryDownload.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    // ---- general ----------------------------------------------------------------------------------------------
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Closing the window keeps EveryDownload in the tray, where it still catches downloads from the browser.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool Notify { get; set; } = true;
    /// <summary>Offer links copied to the clipboard as new downloads.</summary>
    public bool WatchClipboard { get; set; } = true;
    /// <summary>What to do when the queue empties: "nothing", "sleep", "hibernate", "shutdown" or "exit".</summary>
    public string WhenDone { get; set; } = "nothing";
    public bool FirstRunDone { get; set; }

    // ---- downloads --------------------------------------------------------------------------------------------
    public string BaseFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    /// <summary>Put files in a sub-folder per type (Video, Music, Documents…).</summary>
    public bool AutoCategorize { get; set; } = true;
    public int MaxSimultaneous { get; set; } = 3;
    /// <summary>Connections per download; 0 is Smart Boost (start small, add connections while the speed keeps rising).</summary>
    public int DefaultConnections { get; set; }
    /// <summary>The most connections Smart Boost may open for one download.</summary>
    public int MaxConnections { get; set; } = 16;
    /// <summary>Total speed limit in KB/s, applied while <see cref="LimiterOn"/> is set.</summary>
    public int SpeedLimitKb { get; set; } = 1024;
    public bool LimiterOn { get; set; }
    public int AutoRetries { get; set; } = 5;
    public int RetrySeconds { get; set; } = 15;
    /// <summary>Scan finished files with Microsoft Defender when it is installed.</summary>
    public bool ScanDownloads { get; set; } = true;
    /// <summary>Turn finished HLS videos into .mp4 when ffmpeg is installed.</summary>
    public bool RemuxVideo { get; set; } = true;
    /// <summary>Keep sharing a finished torrent. Off stops it as soon as the files are complete.</summary>
    public bool SeedTorrents { get; set; }
    public string LastFolder { get; set; } = "";

    // ---- network ----------------------------------------------------------------------------------------------
    /// <summary>"system", "none" or "manual".</summary>
    public string ProxyMode { get; set; } = "system";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUser { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public string UserAgent { get; set; } = DefaultUserAgent;
    /// <summary>Hold downloads while the connection is metered (mobile data, a hotspot).</summary>
    public bool PauseOnMetered { get; set; }

    // ---- schedule ---------------------------------------------------------------------------------------------
    public bool ScheduleEnabled { get; set; }
    /// <summary>"HH:mm" when the queue starts running.</summary>
    public string ScheduleStart { get; set; } = "02:00";
    public bool ScheduleStopEnabled { get; set; } = true;
    public string ScheduleStop { get; set; } = "07:00";
    /// <summary>Bit 0 = Sunday … bit 6 = Saturday.</summary>
    public int ScheduleDays { get; set; } = 0b1111111;

    // ---- browser ----------------------------------------------------------------------------------------------
    /// <summary>Downloads sent by the browser open the Add dialog first (off: they join the queue straight away).</summary>
    public bool AskForBrowserDownloads { get; set; } = true;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryDownload");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? FilePath), Json);
            if (loaded != null)
            {
                loaded.MaxSimultaneous = Math.Clamp(loaded.MaxSimultaneous, 1, 16);
                loaded.MaxConnections = Math.Clamp(loaded.MaxConnections, 1, 32);
                loaded.DefaultConnections = Math.Clamp(loaded.DefaultConnections, 0, 32);
                loaded.AutoRetries = Math.Clamp(loaded.AutoRetries, 0, 50);
                loaded.RetrySeconds = Math.Clamp(loaded.RetrySeconds, 1, 3600);
                loaded.SpeedLimitKb = Math.Max(1, loaded.SpeedLimitKb);
                if (string.IsNullOrWhiteSpace(loaded.BaseFolder)) loaded.BaseFolder = new AppSettings().BaseFolder;
                if (string.IsNullOrWhiteSpace(loaded.UserAgent)) loaded.UserAgent = DefaultUserAgent;
                return loaded;
            }
        }
        catch { /* first run, or a damaged file: start from the defaults */ }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { /* losing a preference is not worth interrupting a download */ }
    }
}
