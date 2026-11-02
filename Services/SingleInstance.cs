using System.IO.Pipes;
using System.Text.Json;

namespace EveryDownload.Services;

/// <summary>Keeps one copy running; launching another (a browser hand-off, a magnet link) passes its arguments to the first and exits.</summary>
public static class SingleInstance
{
    private const string Name = "EveryDownload.7C41A9E6";
    private static Mutex? mutex;

    /// <summary>Returns false (after passing <paramref name="args"/> to the running copy) if another instance already owns the app.</summary>
    public static bool Acquire(string[] args)
    {
        mutex = new Mutex(true, @"Local\" + Name, out bool owned);
        if (owned) return true;
        TrySend(args, attempts: 15);
        return false;
    }

    /// <summary>Hands <paramref name="args"/> to the running copy. False if none is listening.</summary>
    public static bool TrySend(string[] args, int attempts = 1)
    {
        for (int attempt = 0; attempt < attempts; attempt++) // the other copy may still be opening its pipe
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
                client.Connect(400);
                using var writer = new StreamWriter(client);
                writer.Write(JsonSerializer.Serialize(args));
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException) { if (attempts > 1) Thread.Sleep(200); }
        }
        return false;
    }

    /// <summary>Calls <paramref name="onLaunch"/> (on a background thread) with the arguments of every later launch.</summary>
    public static void Listen(Action<string[]> onLaunch)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var args = JsonSerializer.Deserialize<string[]>(reader.ReadToEnd());
                    if (args != null) onLaunch(args);
                }
                catch { Thread.Sleep(100); }
            }
        })
        { IsBackground = true, Name = "single-instance" }.Start();
    }
}
