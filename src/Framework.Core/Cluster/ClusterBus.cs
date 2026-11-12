using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Cluster;

// ================================================================================================================
//  Signals between application nodes (instances) that share one database.
//
//  Each node keeps some state in memory: active metadata, role caches, who is editing which document. When one
//  node changes such state it publishes a short signal; the other nodes update their copy. With PostgreSQL the
//  signals travel over LISTEN/NOTIFY (no Redis needed). A single node uses LocalClusterBus, which sends nothing.
//
//  Signals are hints, not a durable queue: a node that misses signals (e.g. while its connection to the database
//  was down) gets ClusterBus.Reconnected and re-reads what it caches.
// ================================================================================================================

public interface IClusterBus
{
    /// <summary>Identifier of this node (unique per process).</summary>
    string NodeId { get; }

    /// <summary>True when signals actually reach other nodes.</summary>
    bool IsDistributed { get; }

    /// <summary>Sends a signal to the other nodes (never to this one). Fire and forget; never throws.</summary>
    void Publish(string topic, string payload = "");

    /// <summary>Handler for signals of other nodes. Runs on a thread-pool thread.</summary>
    IDisposable Subscribe(string topic, Action<string> handler);

    /// <summary>Raised after the connection to the other nodes was lost and restored: signals may have been missed.</summary>
    event Action? Reconnected;
}

public static class ClusterTopics
{
    /// <summary>DocType/workflow/customization published; payload empty.</summary>
    public const string Metadata = "metadata";
    /// <summary>Role or module master changed; payload = tenant id.</summary>
    public const string Roles = "roles";
    /// <summary>Document presence (heartbeat/leave); payload = JSON <c>PresenceSignal</c>.</summary>
    public const string Presence = "presence";
    /// <summary>Server/Client Script saved or deleted; payload = "tenant:id".</summary>
    public const string Scripts = "scripts";
    /// <summary>Tenant translations changed; payload = tenant id.</summary>
    public const string Translations = "translations";
}

/// <summary>Subscriber list shared by the bus implementations.</summary>
public sealed class ClusterSubscriptions(ILogger? logger = null)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<string>>> _handlers = new(StringComparer.Ordinal);
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public IDisposable Add(string topic, Action<string> handler)
    {
        var id = Guid.NewGuid();
        _handlers.GetOrAdd(topic, _ => new())[id] = handler;
        return new Remover(() => { if (_handlers.TryGetValue(topic, out var h)) h.TryRemove(id, out _); });
    }

    /// <summary>Calls every handler of the topic on the thread pool; a failing handler does not affect the others.</summary>
    public void Dispatch(string topic, string payload)
    {
        if (!_handlers.TryGetValue(topic, out var handlers) || handlers.IsEmpty) return;
        foreach (var handler in handlers.Values)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { handler(payload); }
                catch (Exception ex) { _logger.LogWarning(ex, "Cluster signal handler for {Topic} failed", topic); }
            }, null);
        }
    }

    private sealed class Remover(Action remove) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) remove(); }
    }
}

/// <summary>Single node: there is nobody to tell. Publish does nothing.</summary>
public sealed class LocalClusterBus : IClusterBus
{
    private readonly ClusterSubscriptions _subs = new();

    public string NodeId { get; } = NewNodeId();
    public bool IsDistributed => false;
    public void Publish(string topic, string payload = "") { }
    public IDisposable Subscribe(string topic, Action<string> handler) => _subs.Add(topic, handler);
#pragma warning disable CS0067 // never raised: a single node cannot miss signals
    public event Action? Reconnected;
#pragma warning restore CS0067

    public static string NewNodeId() => $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
}
