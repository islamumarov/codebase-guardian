namespace Mcp.Events;

/// <summary>Delivery attempt outcomes over <see cref="WebhookOptions.SuspendWindow"/>, used to decide suspension (wire-format B8).</summary>
internal sealed class DeliveryStats(WebhookOptions options, TimeProvider time)
{
    private readonly Queue<(DateTimeOffset At, bool Succeeded)> _attempts = new();
    private readonly object _gate = new();

    public void Record(bool succeeded)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _attempts.Enqueue((now, succeeded));
            Trim(now);
        }
    }

    /// <summary>True when the failure rate over the window reached the threshold with enough attempts.</summary>
    public bool ShouldSuspend()
    {
        lock (_gate)
        {
            Trim(time.GetUtcNow());
            if (_attempts.Count == 0 || _attempts.Count < options.SuspendMinAttempts)
            {
                return false;
            }

            var failures = _attempts.Count(a => !a.Succeeded);
            return (double)failures / _attempts.Count >= options.SuspendFailureRate;
        }
    }

    public void Clear()
    {
        lock (_gate) _attempts.Clear();
    }

    // Must hold _gate.
    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - options.SuspendWindow;
        while (_attempts.TryPeek(out var oldest) && oldest.At < cutoff)
        {
            _attempts.Dequeue();
        }
    }
}
