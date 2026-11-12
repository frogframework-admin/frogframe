using System.Collections.Concurrent;
using System.Text.Json;
using Framework.Cluster;
using Framework.Security;

namespace Framework.Presence;

/// <summary>
/// Implementasi in-memory pelacak kehadiran pengguna (presence) realtime per dokumen dan per tenant.
/// Thread-safe, berkinerja tinggi, dan otomatis membersihkan sesi kedaluwarsa.
/// Multi-node: setiap heartbeat/leave dikirim ke node lain lewat <see cref="IClusterBus"/>, dan sesi dari node lain
/// diterima lewat <see cref="ApplyRemote"/>, sehingga pengguna di node berbeda tetap saling melihat.
/// </summary>
public sealed class DocumentPresenceService : IDocumentPresenceService
{
    private readonly TimeProvider clock;
    private readonly IClusterBus? _cluster;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DocumentPresenceUser>> _docSessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<PresenceChange>>> _subscribers = new();
    private static readonly JsonSerializerOptions SignalJson = new(JsonSerializerDefaults.Web);

    public DocumentPresenceService(TimeProvider clock, TimeSpan? sessionTimeout = null, IClusterBus? cluster = null)
    {
        this.clock = clock;
        _cluster = cluster;
        _timeout = sessionTimeout ?? TimeSpan.FromSeconds(25);
    }

    /// <summary>Heartbeat or leave of a session on another node (payload of <see cref="ClusterTopics.Presence"/>).</summary>
    public sealed record PresenceSignal(string Op, string TenantId, string DocType, string DocName, string SessionId,
        string? UserId = null, string? FullName = null, string? AvatarId = null, PresenceStatus Status = PresenceStatus.Viewing,
        DateTimeOffset ConnectedAt = default);

    private void Send(PresenceSignal signal)
    {
        if (_cluster is not { IsDistributed: true }) return;
        _cluster.Publish(ClusterTopics.Presence, JsonSerializer.Serialize(signal, SignalJson));
    }

    /// <summary>Applies a presence signal from another node; subscribers on this node are notified of changes.</summary>
    public void ApplyRemote(string payload)
    {
        PresenceSignal? s;
        try { s = JsonSerializer.Deserialize<PresenceSignal>(payload, SignalJson); }
        catch (JsonException) { return; }
        if (s is null || string.IsNullOrEmpty(s.TenantId) || string.IsNullOrEmpty(s.SessionId)) return;

        var key = MakeKey(s.TenantId, s.DocType, s.DocName);
        var now = clock.GetUtcNow();
        if (s.Op == "leave")
        {
            if (_docSessions.TryGetValue(key, out var existing) && existing.TryRemove(s.SessionId, out _))
                NotifySubscribers(key, s.TenantId, s.DocType, s.DocName, GetActiveUsersInternal(existing, now, s.DocType, s.DocName, s.TenantId));
            return;
        }

        var sessions = _docSessions.GetOrAdd(key, _ => new ConcurrentDictionary<string, DocumentPresenceUser>());
        var changed = false;
        // LastHeartbeat uses this node's clock, so clock differences between nodes do not expire sessions early.
        sessions.AddOrUpdate(s.SessionId,
            _ =>
            {
                changed = true;
                return new DocumentPresenceUser(s.SessionId, s.TenantId, s.DocType, s.DocName, s.UserId ?? "", s.FullName ?? "",
                    s.AvatarId, s.Status, s.ConnectedAt == default ? now : s.ConnectedAt, now);
            },
            (_, old) =>
            {
                if (old.Status != s.Status) changed = true;
                return old with { Status = s.Status, LastHeartbeat = now, FullName = s.FullName ?? old.FullName, AvatarId = s.AvatarId };
            });
        foreach (var (sid, item) in sessions)
            if (now - item.LastHeartbeat > _timeout && sessions.TryRemove(sid, out _)) changed = true;
        if (changed && _subscribers.ContainsKey(key))
            NotifySubscribers(key, s.TenantId, s.DocType, s.DocName, GetActiveUsersInternal(sessions, now, s.DocType, s.DocName, s.TenantId));
    }

    private static string MakeKey(string tenantId, string docType, string docName) =>
        $"{tenantId.Trim()}:{docType.Trim().ToLowerInvariant()}:{docName.Trim().ToLowerInvariant()}";

    public ValueTask<IReadOnlyList<DocumentPresenceUser>> HeartbeatAsync(
        string tenantId,
        string docType,
        string docName,
        string sessionId,
        UserContext user,
        PresenceStatus status,
        CancellationToken ct = default)
    {
        var key = MakeKey(tenantId, docType, docName);
        var sessions = _docSessions.GetOrAdd(key, _ => new ConcurrentDictionary<string, DocumentPresenceUser>());
        var now = clock.GetUtcNow();
        var avatarId = user.Attributes.TryGetValue("avatar", out var av) ? av : null;
        var changed = false;

        sessions.AddOrUpdate(
            sessionId,
            _ =>
            {
                changed = true;
                return new DocumentPresenceUser(
                    sessionId,
                    tenantId,
                    docType,
                    docName,
                    user.Id,
                    user.FullName,
                    avatarId,
                    status,
                    ConnectedAt: now,
                    LastHeartbeat: now
                );
            },
            (_, existing) =>
            {
                if (existing.Status != status) changed = true;
                return existing with
                {
                    Status = status,
                    LastHeartbeat = now,
                    FullName = user.FullName,
                    AvatarId = avatarId
                };
            }
        );

        // Bersihkan sesi yang kedaluwarsa (heartbeat terhenti)
        foreach (var (sid, item) in sessions)
        {
            if (now - item.LastHeartbeat > _timeout)
            {
                if (sessions.TryRemove(sid, out _))
                {
                    changed = true;
                }
            }
        }

        var active = GetActiveUsersInternal(sessions, now, docType, docName, tenantId);

        if (changed)
        {
            NotifySubscribers(key, tenantId, docType, docName, active);
        }

        // Every heartbeat goes to the other nodes: it also keeps the session alive there.
        if (sessions.TryGetValue(sessionId, out var mine))
            Send(new PresenceSignal("heartbeat", tenantId, docType, docName, sessionId, user.Id, user.FullName, avatarId, status, mine.ConnectedAt));

        return ValueTask.FromResult(active);
    }

    public ValueTask LeaveAsync(
        string tenantId,
        string docType,
        string docName,
        string sessionId,
        CancellationToken ct = default)
    {
        var key = MakeKey(tenantId, docType, docName);
        if (_docSessions.TryGetValue(key, out var sessions))
        {
            if (sessions.TryRemove(sessionId, out _))
            {
                var now = clock.GetUtcNow();
                var active = GetActiveUsersInternal(sessions, now, docType, docName, tenantId);

                if (sessions.IsEmpty)
                {
                    _docSessions.TryRemove(key, out _);
                }

                NotifySubscribers(key, tenantId, docType, docName, active);
            }
        }

        Send(new PresenceSignal("leave", tenantId, docType, docName, sessionId));
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<DocumentPresenceUser> GetActiveUsers(
        string tenantId,
        string docType,
        string docName)
    {
        var key = MakeKey(tenantId, docType, docName);
        if (!_docSessions.TryGetValue(key, out var sessions))
        {
            return Array.Empty<DocumentPresenceUser>();
        }

        var now = clock.GetUtcNow();
        return GetActiveUsersInternal(sessions, now, docType, docName, tenantId);
    }

    public IDisposable Subscribe(
        string tenantId,
        string docType,
        string docName,
        Action<PresenceChange> onChange)
    {
        var key = MakeKey(tenantId, docType, docName);
        var docSubs = _subscribers.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Action<PresenceChange>>());
        var subId = Guid.NewGuid();
        docSubs[subId] = onChange;

        return new Subscription(this, key, subId);
    }

    private void Unsubscribe(string key, Guid subId)
    {
        if (_subscribers.TryGetValue(key, out var docSubs))
        {
            docSubs.TryRemove(subId, out _);
            if (docSubs.IsEmpty)
            {
                _subscribers.TryRemove(key, out _);
            }
        }
    }

    private IReadOnlyList<DocumentPresenceUser> GetActiveUsersInternal(
        ConcurrentDictionary<string, DocumentPresenceUser> sessions,
        DateTimeOffset now,
        string defaultDocType,
        string defaultDocName,
        string defaultTenantId)
    {
        // Sessions whose heartbeats stopped (closed tab, node gone) are not reported, even before they are cleaned up.
        return sessions.Values
            .Where(u => now - u.LastHeartbeat <= _timeout)
            .OrderBy(u => u.ConnectedAt)
            .ToList();
    }

    private void NotifySubscribers(
        string key,
        string tenantId,
        string docType,
        string docName,
        IReadOnlyList<DocumentPresenceUser> active)
    {
        if (_subscribers.TryGetValue(key, out var docSubs))
        {
            var change = new PresenceChange(tenantId, docType, docName, active);
            foreach (var (_, callback) in docSubs)
            {
                try
                {
                    callback(change);
                }
                catch
                {
                    // Jangan biarkan kegagalan sirkuit pelanggan merusak sirkuit lain
                }
            }
        }
    }

    private sealed class Subscription(DocumentPresenceService service, string key, Guid subId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                service.Unsubscribe(key, subId);
            }
        }
    }
}

