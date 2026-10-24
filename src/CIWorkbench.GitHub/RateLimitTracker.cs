using CIWorkbench.Core.Models;

namespace CIWorkbench.GitHub;

/// <summary>Observes rate-limit headers of every response so the UI can say whether requests are being throttled.</summary>
public sealed class RateLimitTracker
{
    private readonly object _gate = new();
    public RateLimitInfo? Last { get; private set; }
    public DateTimeOffset? ThrottledUntil { get; private set; }
    public int RetriesPerformed { get; private set; }
    public event Action? Changed;

    public bool IsThrottled(DateTimeOffset now) => ThrottledUntil is { } t && t > now;

    internal void Observe(HttpResponseMessage r)
    {
        if (TryHeader(r, "x-ratelimit-limit", out var limit) && TryHeader(r, "x-ratelimit-remaining", out var rem) && TryHeader(r, "x-ratelimit-reset", out var reset))
        {
            lock (_gate) Last = new RateLimitInfo(limit, rem, DateTimeOffset.FromUnixTimeSeconds(reset));
            Changed?.Invoke();
        }
    }

    internal void SetThrottled(DateTimeOffset until) { lock (_gate) { ThrottledUntil = until; RetriesPerformed++; } Changed?.Invoke(); }
    internal void ClearThrottle() { lock (_gate) ThrottledUntil = null; Changed?.Invoke(); }

    internal static bool TryHeader(HttpResponseMessage r, string name, out int value)
    {
        value = 0;
        return r.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out value);
    }
}

