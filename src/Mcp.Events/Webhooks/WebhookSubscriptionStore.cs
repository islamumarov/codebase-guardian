using System.Diagnostics.CodeAnalysis;

namespace Mcp.Events;

/// <summary>In-memory registry of webhook subscriptions, keyed by their deterministic id.</summary>
public sealed class WebhookSubscriptionStore
{
    private readonly Dictionary<string, WebhookSubscription> _byId = new(StringComparer.Ordinal);

    /// <summary>Lock for reading and mutating a subscription's mutable fields.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>Raised after a subscription is added, refreshed (<see cref="NotifyChanged"/>) or removed.</summary>
    public event Action<WebhookSubscription>? Changed;

    public bool TryGet(string id, [NotNullWhen(true)] out WebhookSubscription? subscription)
    {
        lock (SyncRoot) return _byId.TryGetValue(id, out subscription);
    }

    public void Add(WebhookSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        lock (SyncRoot) _byId[subscription.Id] = subscription;
        Changed?.Invoke(subscription);
    }

    /// <summary>Adds unless the id exists or the principal is at <paramref name="maxPerPrincipal"/>; raises <see cref="Changed"/> after releasing the lock.</summary>
    internal WebhookAddResult TryAdd(WebhookSubscription subscription, int maxPerPrincipal)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        lock (SyncRoot)
        {
            if (_byId.ContainsKey(subscription.Id)) return WebhookAddResult.Exists;
            if (_byId.Values.Count(s => s.Principal == subscription.Principal) >= maxPerPrincipal) return WebhookAddResult.LimitReached;
            _byId[subscription.Id] = subscription;
        }

        Changed?.Invoke(subscription);
        return WebhookAddResult.Added;
    }

    public bool Remove(string id)
    {
        WebhookSubscription? removed;
        lock (SyncRoot)
        {
            if (!_byId.Remove(id, out removed)) return false;
        }

        Changed?.Invoke(removed);
        return true;
    }

    public int CountFor(string principal)
    {
        lock (SyncRoot) return _byId.Values.Count(s => s.Principal == principal);
    }

    public IReadOnlyList<WebhookSubscription> Snapshot()
    {
        lock (SyncRoot) return [.. _byId.Values];
    }

    /// <summary>Signals that <paramref name="subscription"/> was refreshed in place.</summary>
    public void NotifyChanged(WebhookSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        Changed?.Invoke(subscription);
    }
}

internal enum WebhookAddResult { Added, Exists, LimitReached }
