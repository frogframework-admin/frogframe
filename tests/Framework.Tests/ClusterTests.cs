using Framework.Cluster;
using Framework.Metadata;
using Framework.Navigation;
using Framework.Presence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Multi-node: signals between application nodes that share one database.</summary>
sealed partial class Tests
{
    /// <summary>Several "nodes" in one process; a signal reaches every other node at once (synchronously).</summary>
    private sealed class FakeClusterNetwork
    {
        private readonly List<FakeClusterBus> _nodes = [];
        public FakeClusterBus Node() { var n = new FakeClusterBus(this, $"node{_nodes.Count + 1}"); _nodes.Add(n); return n; }
        public void Send(FakeClusterBus from, string topic, string payload)
        {
            foreach (var n in _nodes.Where(n => n != from)) n.Deliver(topic, payload);
        }
    }

    private sealed class FakeClusterBus(FakeClusterNetwork network, string id) : IClusterBus
    {
        private readonly List<(string Topic, Action<string> Handler)> _handlers = [];
        public List<(string Topic, string Payload)> Sent { get; } = [];
        public string NodeId => id;
        public bool IsDistributed => true;
        public event Action? Reconnected;
        public void Publish(string topic, string payload = "") { Sent.Add((topic, payload)); network.Send(this, topic, payload); }
        public IDisposable Subscribe(string topic, Action<string> handler)
        {
            var entry = (topic, handler);
            _handlers.Add(entry);
            return new Unsub(() => _handlers.Remove(entry));
        }
        public void Deliver(string topic, string payload) { foreach (var h in _handlers.Where(h => h.Topic == topic).ToList()) h.Handler(payload); }
        public void SimulateReconnect() => Reconnected?.Invoke();
        private sealed class Unsub(Action a) : IDisposable { public void Dispose() => a(); }
    }

    /// <summary>Shared metadata store of the "database" (only what MetadataRegistry needs).</summary>
    private sealed class ClusterMetadataStore : IMetadataVersionStore
    {
        private readonly List<MetadataVersion> _versions = [];
        private readonly List<WorkflowVersion> _workflows = [];
        public IReadOnlyList<MetadataVersion> LoadAll() { lock (_versions) return _versions.ToList(); }
        public void Save(MetadataVersion version)
        {
            lock (_versions)
            {
                for (var i = 0; i < _versions.Count; i++)
                    if (_versions[i].DocType == version.DocType && _versions[i].Status == MetadataStatus.Published)
                        _versions[i] = _versions[i] with { Status = MetadataStatus.Deprecated };
                _versions.Add(version);
            }
        }
        public IReadOnlyList<WorkflowVersion> LoadAllWorkflows() { lock (_workflows) return _workflows.ToList(); }
        public void SaveWorkflow(WorkflowVersion version) { lock (_workflows) _workflows.Add(version); }
    }

    private static DocTypeDef ClusterDocType(int version, params string[] extraFields) => new()
    {
        Name = "ClusterNote", Title = "Note", Version = version,
        Naming = new NamingDef { Pattern = "CN-{#####}" },
        Fields = [new FieldDef { Name = "title", Type = "Data", Label = "Title" }, .. extraFields.Select(f => new FieldDef { Name = f, Type = "Data", Label = f })],
        Permissions = [new PermissionDef { Role = UserContext.SystemManager, Actions = ["read", "create", "write"], Scope = "all" }],
        ListView = new ListViewDef { TitleField = "title", SearchFields = ["name", "title"] },
    };

    [Test]
    public async Task Cluster_presence_is_shared_between_nodes()
    {
        var network = new FakeClusterNetwork();
        var busA = network.Node();
        var busB = network.Node();
        var clock = new ManualClock();
        var a = new DocumentPresenceService(clock, TimeSpan.FromSeconds(25), busA);
        var b = new DocumentPresenceService(clock, TimeSpan.FromSeconds(25), busB);
        busA.Subscribe(ClusterTopics.Presence, a.ApplyRemote);
        busB.Subscribe(ClusterTopics.Presence, b.ApplyRemote);

        var changes = new List<PresenceChange>();
        using var sub = b.Subscribe("acme", "PurchaseRequest", "PR-1", changes.Add);

        await a.HeartbeatAsync("acme", "PurchaseRequest", "PR-1", "s-andi", U("andi"), PresenceStatus.Editing);
        var seenOnB = b.GetActiveUsers("acme", "PurchaseRequest", "PR-1");
        Assert.Equal(1, seenOnB.Count, "user on node A visible on node B");
        Assert.Equal(PresenceStatus.Editing, seenOnB[0].Status, "status travels");
        Assert.True(changes.Count == 1, "subscriber on node B notified");

        // Both nodes see both users.
        var onB = await b.HeartbeatAsync("acme", "PurchaseRequest", "PR-1", "s-admin", U("admin"), PresenceStatus.Viewing);
        Assert.Equal(2, onB.Count, "node B: own + remote");
        Assert.Equal(2, a.GetActiveUsers("acme", "PurchaseRequest", "PR-1").Count, "node A: own + remote");
        Assert.Equal(0, b.GetActiveUsers("other", "PurchaseRequest", "PR-1").Count, "tenants stay apart");

        await a.LeaveAsync("acme", "PurchaseRequest", "PR-1", "s-andi");
        Assert.Equal(1, b.GetActiveUsers("acme", "PurchaseRequest", "PR-1").Count, "leave travels");

        // A node that stops (no leave) disappears after the timeout.
        await a.HeartbeatAsync("acme", "PurchaseRequest", "PR-2", "s-andi", U("andi"), PresenceStatus.Viewing);
        Assert.Equal(1, b.GetActiveUsers("acme", "PurchaseRequest", "PR-2").Count, "visible");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, b.GetActiveUsers("acme", "PurchaseRequest", "PR-2").Count, "expired without heartbeats");
    }

    [Test]
    public async Task Cluster_metadata_published_on_one_node_reaches_the_other()
    {
        var store = new ClusterMetadataStore();
        var compiler = sp.GetRequiredService<MetadataCompiler>();
        MetadataRegistry NewNode() => new(compiler, [], [], TimeProvider.System, store);
        var a = NewNode();
        var b = NewNode();

        a.Publish(ClusterDocType(1), "admin", acknowledgeRisk: true);
        Assert.True(!b.Current.TryGet("ClusterNote", out _), "node B does not know yet");

        b.ReloadFrom(NewNode);
        Assert.True(b.Current.TryGet("ClusterNote", out var meta), "node B after reload");
        Assert.Equal(1, meta.Version, "version 1");
        Assert.Equal(1, b.History("ClusterNote").Count, "history taken over");

        // Through the hosted service: publish on A → signal → B reloads (after a short delay).
        var network = new FakeClusterNetwork();
        var busA = network.Node();
        var busB = network.Node();
        var roles = sp.GetRequiredService<RoleCatalog>();
        var roleStore = sp.GetRequiredService<IRoleStore>();
        var menus = sp.GetRequiredService<INavigationMenuStore>();
        using var syncA = new ClusterSync(busA, a, NewNode, roles, roleStore, menus, null, NullLogger<ClusterSync>.Instance);
        using var syncB = new ClusterSync(busB, b, NewNode, roles, roleStore, menus, null, NullLogger<ClusterSync>.Instance);
        await syncA.StartAsync(default);
        await syncB.StartAsync(default);

        a.Publish(ClusterDocType(2, "body"), "admin", acknowledgeRisk: true);
        Assert.True(busA.Sent.Any(s => s.Topic == ClusterTopics.Metadata), "node A signalled the publish");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (b.Current.Get("ClusterNote").Version < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(2, b.Current.Get("ClusterNote").Version, "node B reloaded after the signal");
        Assert.True(b.Current.Get("ClusterNote").ByName.ContainsKey("body"), "new field on node B");
        Assert.True(!busB.Sent.Any(s => s.Topic == ClusterTopics.Metadata), "a reload is not signalled again (no ping-pong)");

        // Publishing on B after the reload works (B's state is consistent) and reaches A.
        b.Publish(ClusterDocType(3, "body", "tags"), "admin", acknowledgeRisk: true);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (a.Current.Get("ClusterNote").Version < 3 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(3, a.Current.Get("ClusterNote").Version, "and back to node A");
    }

    [Test]
    public async Task Cluster_role_changes_clear_role_caches_on_every_node()
    {
        var network = new FakeClusterNetwork();
        var busA = network.Node();
        var busB = network.Node();
        var roleStore = sp.GetRequiredService<IRoleStore>();
        var accounts = sp.GetRequiredService<IUserAccountRepository>();
        var menus = sp.GetRequiredService<INavigationMenuStore>();
        var rolesA = new RoleCatalog(roleStore, accounts, menus, busA);
        var rolesB = new RoleCatalog(roleStore, accounts, menus, busB);
        var reg = sp.GetRequiredService<MetadataRegistry>();
        // Node B's store does not raise events for changes made on node A: give it a store of its own.
        using var syncB = new ClusterSync(busB, reg, () => reg, rolesB, new Framework.Security.InMemoryRoleStore(TimeProvider.System), menus, null, NullLogger<ClusterSync>.Instance);
        await syncB.StartAsync(default);

        Assert.True(!rolesB.Names("acme").Contains("Auditor Cluster"), "cached without the new role");
        await roleStore.SaveAsync(new RoleDef { Id = "role-auditor-cluster", TenantId = "acme", Name = "Auditor Cluster" });

        // The change happened "on node A": node A's sync clears its cache and signals node B.
        using var syncA = new ClusterSync(busA, reg, () => reg, rolesA, roleStore, menus, null, NullLogger<ClusterSync>.Instance);
        await syncA.StartAsync(default);
        await roleStore.SaveAsync(new RoleDef { Id = "role-auditor-cluster", TenantId = "acme", Name = "Auditor Cluster", Description = "x" });
        Assert.True(busA.Sent.Any(s => s.Topic == ClusterTopics.Roles), "role change signalled");
        Assert.True(rolesB.Names("acme").Contains("Auditor Cluster"), "node B sees the new role at once");
    }
}
