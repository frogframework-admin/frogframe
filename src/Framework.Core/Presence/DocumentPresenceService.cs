using System.Collections.Concurrent;
using Framework.Security;

namespace Framework.Presence;

/// <summary>
/// Implementasi in-memory pelacak kehadiran pengguna (presence) realtime per dokumen dan per tenant.
/// Thread-safe, berkinerja tinggi, dan otomatis membersihkan sesi kedaluwarsa.
/// </summary>
public sealed class DocumentPresenceService(TimeProvider clock, TimeSpan? sessionTimeout = null) : IDocumentPresenceService
{
    private readonly TimeSpan _timeout = sessionTimeout ?? TimeSpan.FromSeconds(25);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DocumentPresenceUser>> _docSessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<PresenceChange>>> _subscribers = new();

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

    private static IReadOnlyList<DocumentPresenceUser> GetActiveUsersInternal(
        ConcurrentDictionary<string, DocumentPresenceUser> sessions,
        DateTimeOffset now,
        string defaultDocType,
        string defaultDocName,
        string defaultTenantId)
    {
        return sessions.Values
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

