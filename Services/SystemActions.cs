using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Networking.Connectivity;

namespace EveryDownload.Services;

/// <summary>Small bits of Windows the app leans on: the metered-connection rule and the "when done" power actions.</summary>
public static class SystemActions
{
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    /// <summary>True on mobile data, a hotspot, or any connection Windows marks as metered.</summary>
    public static bool IsMetered()
    {
        try
        {
            var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return cost != null && cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
        }
        catch { return false; }
    }

    /// <summary>Runs "sleep", "hibernate" or "shutdown".</summary>
    public static void Power(string action)
    {
        switch (action)
        {
            case "sleep": SetSuspendState(false, false, false); break;
            case "hibernate": SetSuspendState(true, false, false); break;
            case "shutdown": Run("shutdown", "/s", "/t", "5", "/c", "EveryDownload finished all downloads."); break;
        }
    }

    private static void Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        try { Process.Start(info); } catch (Exception ex) { Log.Write("Could not run " + file, ex); }
    }

    public static uint ClipboardSequence => GetClipboardSequenceNumber();

    /// <summary>The text on the clipboard, or empty. Must run on the UI thread.</summary>
    public static async Task<string> ClipboardTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? (await content.GetTextAsync()) ?? "" : "";
        }
        catch { return ""; } // the clipboard is busy; the next change will be seen
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
        }
        catch (Exception ex) { Log.Write("Could not open Explorer", ex); }
    }

    public static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Could not open " + path, ex); }
    }
}
