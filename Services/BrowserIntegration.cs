using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace EveryDownload.Services;

/// <summary>
/// The link between Chrome / Edge / Brave and the app. The browser extension talks to this very exe through Chrome's
/// native messaging: the browser starts it with the extension's address as an argument, sends length-prefixed JSON on
/// stdin, and the exe passes each request on to the running app (starting it if needed).
/// </summary>
public static class BrowserIntegration
{
    public const string HostName = "com.everydownload.host";
    /// <summary>Fixed by the public key in extension/manifest.json, so the host manifest can name the one extension that may call it.</summary>
    public const string ExtensionId = "lapamfhoglklddddhffgnjonciafnomd";

    private static readonly string[] BrowserKeys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\Vivaldi\NativeMessagingHosts",
    };

    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");
    private static string ManifestPath => Path.Combine(AppSettings.Folder, "native", HostName + ".json");

    /// <summary>True when the browser has been told where the host lives (checks Chrome's key, the one every Chromium browser falls back to).</summary>
    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(BrowserKeys[0] + "\\" + HostName);
        return key?.GetValue(null) is string path && File.Exists(path);
    }

    /// <summary>Writes the host manifest and registers it for each browser (HKCU, no administrator rights needed).</summary>
    public static void Install(string exePath)
    {
        string manifest = ManifestPath;
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        var doc = new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "EveryDownload browser integration",
            ["path"] = exePath,
            ["type"] = "stdio",
            ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
        };
        File.WriteAllText(manifest, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var browser in BrowserKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(browser + "\\" + HostName, true);
            key.SetValue(null, manifest);
        }
    }

    public static void Uninstall()
    {
        foreach (var browser in BrowserKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(browser + "\\" + HostName, false); } catch { /* not registered */ }
        }
        try { File.Delete(ManifestPath); } catch { /* already gone */ }
    }

    /// <summary>True when this process was started by a browser as the native messaging host.</summary>
    public static bool IsHostLaunch(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) || args.Contains("--native-host", StringComparer.OrdinalIgnoreCase);

    /// <summary>Answers the browser until it closes the pipe. Each request is handed to the running app.</summary>
    public static void RunHost(string exePath)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Log.Write($"Browser host started (stdin {input.GetType().Name})");
        var lengthBytes = new byte[4];
        while (ReadFully(input, lengthBytes, 4))
        {
            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > 4 * 1024 * 1024) { Log.Write("Browser host: bad message length " + length); break; }
            var body = new byte[length];
            if (!ReadFully(input, body, length)) break;

            object reply;
            try { reply = Handle(Encoding.UTF8.GetString(body), exePath); }
            catch (Exception ex) { Log.Write("Browser request failed", ex); reply = new { ok = false, error = ex.Message }; }

            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
            output.Write(BitConverter.GetBytes(json.Length));
            output.Write(json);
            output.Flush();
        }
        Log.Write("Browser host finished");
    }

    private static object Handle(string message, string exePath)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        string type = Text(root, "type");
        switch (type)
        {
            case "ping":
                return new { ok = true, version = "1.0.0" };

            case "add":
            {
                var args = new List<string> { "--add", Text(root, "url") };
                // A selection of several links arrives as one request, so the app shows one Add dialog with all of them.
                if (root.TryGetProperty("urls", out var many) && many.ValueKind == JsonValueKind.Array)
                {
                    var list = many.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").Where(u => u.Length > 0).ToList();
                    if (list.Count > 0) args[1] = list[0];
                    foreach (var extra in list.Skip(1)) { args.Add("--also"); args.Add(extra); }
                }
                void Option(string flag, string field) { string v = Text(root, field); if (v.Length > 0) { args.Add(flag); args.Add(v); } }
                Option("--referer", "referrer");
                Option("--cookie", "cookie");
                Option("--ua", "userAgent");
                Option("--name", "filename");
                if (root.TryGetProperty("silent", out var silent) && silent.ValueKind == JsonValueKind.True) args.Add("--silent");
                Deliver(args.ToArray(), exePath);
                return new { ok = true };
            }
            case "grab":
                Deliver(new[] { "--grab", Text(root, "url") }, exePath);
                return new { ok = true };

            case "open":
                Deliver(new[] { "--show" }, exePath);
                return new { ok = true };
        }
        return new { ok = false, error = "Unknown request: " + type };
    }

    /// <summary>Gives the arguments to the running app, or starts the app with them.</summary>
    private static void Deliver(string[] args, string exePath)
    {
        if (SingleInstance.TrySend(args)) return;
        var info = new ProcessStartInfo(exePath) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        Process.Start(info);
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool ReadFully(Stream s, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>Registers EveryDownload for magnet: links and .torrent files (per user, no administrator rights).</summary>
public static class FileAssociations
{
    private const string ProgId = "EveryDownload.Torrent";

    public static void Install(string exePath)
    {
        string command = $"\"{exePath}\" \"%1\"";
        using (var magnet = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet", true))
        {
            magnet.SetValue(null, "URL:Magnet link");
            magnet.SetValue("URL Protocol", "");
            using var open = magnet.CreateSubKey(@"shell\open\command", true);
            open.SetValue(null, command);
        }
        using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId, true))
        {
            prog.SetValue(null, "Torrent file");
            using var open = prog.CreateSubKey(@"shell\open\command", true);
            open.SetValue(null, command);
        }
        using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent", true))
            ext.SetValue(null, ProgId);
    }

    /// <summary>Removes only what EveryDownload registered; a magnet or .torrent handler that belongs to another app is left alone.</summary>
    public static void Uninstall()
    {
        try
        {
            using (var open = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command"))
                if (open?.GetValue(null) is string command && command.Contains("EveryDownload", StringComparison.OrdinalIgnoreCase))
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\magnet", false);
            using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent"))
                if (ext?.GetValue(null) as string == ProgId) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.torrent", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        }
        catch (Exception ex) { Log.Write("Could not remove the file associations", ex); }
    }
}
