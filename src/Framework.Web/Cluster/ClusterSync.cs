using Framework.Metadata;
using Framework.Navigation;
using Framework.Presence;
using Framework.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Framework.Cluster;

/// <summary>
/// Keeps the in-memory state of this node in line with the other nodes:
/// <list type="bullet">
/// <item>metadata published elsewhere (DocType, workflow, customization) → reload from the shared store,</item>
/// <item>role/module master changed (here or elsewhere) → clear the role cache (before, it only expired after 30 s),</item>
/// <item>presence of users on other nodes → shown in the "being edited by" banner.</item>
/// </list>
/// After missed signals (lost database connection) metadata is reloaded and role caches are cleared.
/// </summary>
public sealed class ClusterSync(
    IClusterBus bus,
    MetadataRegistry registry,
    Func<MetadataRegistry> rebuildRegistry,
    RoleCatalog roles,
    IRoleStore roleStore,
    INavigationMenuStore navigation,
    DocumentPresenceService? presence,
    ILogger<ClusterSync> log) : IHostedService, IDisposable
{
    /// <summary>Several publishes in a row (e.g. a DocType and its workflow) cause one reload.</summary>
    public static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly List<IDisposable> _subscriptions = [];
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private int _reloadPending;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Also on a single node: a new role or module is usable at once (the cache used to expire after 30 s only).
        roleStore.Changed += OnRolesChanged;
        navigation.Changed += OnRolesChanged;
        if (!bus.IsDistributed) return Task.CompletedTask; // single node: nothing else to synchronize

        registry.Published += OnLocalPublish;
        _subscriptions.Add(bus.Subscribe(ClusterTopics.Metadata, _ => ScheduleReload()));
        _subscriptions.Add(bus.Subscribe(ClusterTopics.Roles, tenant =>
        {
            if (tenant is "" or "*") roles.InvalidateAllLocal();
            else roles.InvalidateLocal(tenant);
        }));
        if (presence is not null) _subscriptions.Add(bus.Subscribe(ClusterTopics.Presence, presence.ApplyRemote));
        bus.Reconnected += OnReconnected;
        log.LogInformation("Multi-node synchronization active (node {Node})", bus.NodeId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        roleStore.Changed -= OnRolesChanged;
        navigation.Changed -= OnRolesChanged;
        registry.Published -= OnLocalPublish;
        bus.Reconnected -= OnReconnected;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }

    private void OnLocalPublish(MetadataSnapshot _) => bus.Publish(ClusterTopics.Metadata);

    /// <summary>The stores do not say which tenant changed: clear all role/module caches (they reload lazily).</summary>
    private void OnRolesChanged()
    {
        roles.InvalidateAllLocal();
        bus.Publish(ClusterTopics.Roles, "*");
    }

    private void OnReconnected()
    {
        roles.InvalidateAllLocal();
        ScheduleReload();
    }

    /// <summary>Waits a moment, then reloads once for any number of signals received meanwhile.</summary>
    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(ReloadDelay);
            await _reloadGate.WaitAsync();
            try
            {
                Interlocked.Exchange(ref _reloadPending, 0);
                await ReloadNowAsync();
            }
            finally { _reloadGate.Release(); }
        });
    }

    /// <summary>Reloads metadata from the shared store (also used by tests).</summary>
    public Task ReloadNowAsync()
    {
        try
        {
            var before = registry.Current;
            registry.ReloadFrom(rebuildRegistry);
            log.LogInformation("Metadata reloaded after a change on another node ({Count} DocTypes)", registry.Current.DocTypes.Count);
            if (ReferenceEquals(before, registry.Current)) log.LogDebug("Metadata reload produced the same snapshot");
        }
        catch (Exception ex)
        {
            // Keep serving the current metadata; the next signal or a restart tries again.
            log.LogError(ex, "Metadata reload after a change on another node failed");
        }
        return Task.CompletedTask;
    }
}
