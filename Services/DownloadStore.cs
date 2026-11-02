using System.Text.Json;
using System.Text.Json.Serialization;
using EveryDownload.Models;

namespace EveryDownload.Services;

/// <summary>Keeps the download list (including each file's saved byte ranges) as JSON in %AppData%\EveryDownload.</summary>
public static class DownloadStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public static string FilePath => Path.Combine(AppSettings.Folder, "downloads.json");

    public static List<DownloadItem> Load(string? path = null)
    {
        path ??= FilePath;
        foreach (var candidate in new[] { path, path + ".bak" }) // the backup covers a save that was cut off
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var items = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(candidate), Json);
                if (items != null) return items;
            }
            catch (Exception ex) { Log.Write("The download list could not be read: " + candidate, ex); }
        }
        return new List<DownloadItem>();
    }

    public static void Save(IEnumerable<DownloadItem> items, string? path = null)
    {
        try
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var list = items.ToList();
            string json;
            lock (typeof(DownloadStore))
            {
                foreach (var it in list) Monitor.Enter(it.Gate); // range lists change while connections run
                try { json = JsonSerializer.Serialize(list, Json); }
                finally { foreach (var it in list) Monitor.Exit(it.Gate); }

                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
        }
        catch (Exception ex) { Log.Write("The download list could not be saved", ex); }
    }
}
