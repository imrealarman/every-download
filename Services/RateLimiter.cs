using System.Diagnostics;

namespace EveryDownload.Services;

/// <summary>
/// A token bucket shared by every connection that counts against it. Callers that go over the limit are told to wait,
/// so the sum of all connections stays at the limit no matter how many there are.
/// </summary>
public sealed class RateLimiter
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double tokens;
    private double lastSeconds;
    private long limit;

    /// <summary>Bytes per second; 0 or less means no limit.</summary>
    public long BytesPerSecond
    {
        get { lock (gate) return limit; }
        set { lock (gate) { limit = Math.Max(0, value); tokens = 0; lastSeconds = clock.Elapsed.TotalSeconds; } }
    }

    /// <summary>Completes once <paramref name="bytes"/> may be sent without exceeding the limit.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        double delay;
        lock (gate)
        {
            if (limit <= 0) return;
            double now = clock.Elapsed.TotalSeconds;
            // A quarter of a second of burst is plenty; more makes the first moments after a pause overshoot.
            tokens = Math.Min(limit / 4.0, tokens + (now - lastSeconds) * limit);
            lastSeconds = now;
            tokens -= bytes;
            delay = tokens < 0 ? -tokens / limit : 0;
        }
        if (delay > 0.001) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
    }
}
