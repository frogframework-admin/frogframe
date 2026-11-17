using System.Collections.Concurrent;
using System.Text.Json;
using Framework.Cluster;
using Framework.Documents;

namespace Framework.Notifications;

/// <summary>
/// Real-time notification feed for connected users (Blazor Server SignalR circuits and SignalR hubs).
/// </summary>
public interface INotificationLiveFeed
{
    ValueTask PublishAsync(Notification notification, CancellationToken ct = default);
    IDisposable Subscribe(string tenantId, string userId, Func<Notification, Task> handler);
    IDisposable SubscribeAll(Func<Notification, Task> handler);
}

public sealed class NotificationLiveFeed(IClusterBus? cluster = null) : INotificationLiveFeed
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Func<Notification, Task>>> _userSubs = new();
    private readonly ConcurrentDictionary<Guid, Func<Notification, Task>> _globalSubs = new();

    private static string Key(string tenantId, string userId) =>
        $"{tenantId.Trim()}:{userId.Trim().ToLowerInvariant()}";

    public async ValueTask PublishAsync(Notification notification, CancellationToken ct = default)
    {
        await DispatchLocalAsync(notification);
        if (cluster is { IsDistributed: true })
            cluster.Publish(ClusterTopics.Notifications, JsonSerializer.Serialize(notification, JsonOpts));
    }

    public void ApplyRemote(string payload)
    {
        Notification? notification;
        try { notification = JsonSerializer.Deserialize<Notification>(payload, JsonOpts); }
        catch (JsonException) { return; }
        if (notification is null || string.IsNullOrWhiteSpace(notification.TenantId) || string.IsNullOrWhiteSpace(notification.UserId))
            return;

        _ = DispatchLocalAsync(notification);
    }

    public IDisposable Subscribe(string tenantId, string userId, Func<Notification, Task> handler)
    {
        var key = Key(tenantId, userId);
        var id = Guid.NewGuid();
        _userSubs.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Func<Notification, Task>>())[id] = handler;
        return new Unsubscriber(() =>
        {
            if (_userSubs.TryGetValue(key, out var map))
                map.TryRemove(id, out _);
        });
    }

    public IDisposable SubscribeAll(Func<Notification, Task> handler)
    {
        var id = Guid.NewGuid();
        _globalSubs[id] = handler;
        return new Unsubscriber(() => _globalSubs.TryRemove(id, out _));
    }

    private async Task DispatchLocalAsync(Notification notification)
    {
        if (_userSubs.TryGetValue(Key(notification.TenantId, notification.UserId), out var map))
        {
            foreach (var handler in map.Values)
            {
                try { await handler(notification); }
                catch { /* Keep other live subscribers healthy */ }
            }
        }

        foreach (var handler in _globalSubs.Values)
        {
            try { await handler(notification); }
            catch { /* Keep hub/other subscribers healthy */ }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                dispose();
        }
    }
}
