namespace EveryDownload.Models;

/// <summary>Human-readable sizes, speeds and times.</summary>
public static class Format
{
    public static string Bytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{n:N0} B" : $"{v:0.#} {units[i]}";
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        if (t.TotalSeconds >= 1) return $"{t.Minutes}:{t.Seconds:00}";
        return "<1 s";
    }
}
