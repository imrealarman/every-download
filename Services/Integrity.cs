using System.Diagnostics;
using System.Security.Cryptography;

namespace EveryDownload.Services;

/// <summary>Checksum verification and the antivirus scan that run after a download finishes.</summary>
public static class Integrity
{
    /// <summary>The algorithm for a hex checksum, judged by its length (MD5, SHA-1, SHA-256, SHA-512). Null if it is none of them.</summary>
    public static HashAlgorithmName? AlgorithmFor(string hash) => hash.Length switch
    {
        32 => HashAlgorithmName.MD5,
        40 => HashAlgorithmName.SHA1,
        64 => HashAlgorithmName.SHA256,
        128 => HashAlgorithmName.SHA512,
        _ => null,
    };

    /// <summary>Accepts "sha256:abc…", "abc…" and checksum files' "abc…  name" lines; returns just the hex digits.</summary>
    public static string Clean(string text)
    {
        text = text.Trim();
        int colon = text.IndexOf(':');
        if (colon > 0 && colon < 8) text = text[(colon + 1)..].Trim();
        return text.Split(' ', '\t', '*')[0].Trim().ToLowerInvariant();
    }

    public static async Task<string> HashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(algorithm);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hasher.AppendData(buffer, 0, read);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The Microsoft Defender command-line scanner, if this PC has one.</summary>
    public static string? DefenderPath()
    {
        string platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        if (Directory.Exists(platform))
        {
            var newest = Directory.GetDirectories(platform).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "MpCmdRun.exe")).FirstOrDefault(File.Exists);
            if (newest != null) return newest;
        }
        string classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(classic) ? classic : null;
    }

    /// <summary>Scans a file with Defender. Null when no scanner is available; otherwise whether the file is clean.</summary>
    public static async Task<bool?> ScanAsync(string path, CancellationToken ct)
    {
        string? scanner = DefenderPath();
        if (scanner == null) return null;
        try
        {
            var info = new ProcessStartInfo(scanner) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var a in new[] { "-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation" }) info.ArgumentList.Add(a);
            using var process = Process.Start(info);
            if (process == null) return null;
            _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0 ? true : process.ExitCode == 2 ? false : null; // 2 = threat found; anything else = scan did not run
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write("The virus scan could not run", ex);
            return null;
        }
    }
}
