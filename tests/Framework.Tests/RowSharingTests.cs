using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Sharing;
using Framework.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Row sharing (P1-6): per-document read / write / share grants widen the row scope of users whose roles already
/// give the DocType permission. Covers list and single-document access, edit through a write share, who may share
/// and with which rights, audit and notification, leaving a share, cleanup on delete, and (with FROGFRAME_TEST_PG)
/// the PostgreSQL store and query scope.
/// </summary>
sealed partial class Tests
{
    private sealed class ShareTestDirectory(IEnumerable<UserContext> list) : IUserDirectory
    {
        private readonly List<UserContext> users = list.ToList();
        public Task<UserContext?> FindAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult(users.FirstOrDefault(u => string.Equals(u.Id, userId, StringComparison.OrdinalIgnoreCase)));
        public Task<IReadOnlyList<UserContext>> ListAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserContext>>(users.Where(u => u.TenantId == tenantId).ToList());
    }

    private static UserContext ShareUser(string id, string tenant, params string[] roles) =>
        new(id, char.ToUpperInvariant(id[0]) + id[1..], tenant, new HashSet<string>(roles), new Dictionary<string, string>());

    private static ServiceProvider BuildShareProvider(IEnumerable<UserContext> users, Action<IServiceCollection>? extra = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ShareNote.doctype.json"), """
        {
          "name": "ShareNote", "version": 1, "title": "Share Note", "module": "CORE",
          "naming": { "pattern": "SN-{#####}" },
          "document": { "allowDelete": true },
          "listView": { "titleField": "subject", "searchFields": ["name", "subject"] },
          "fields": [ { "name": "subject", "type": "Data", "label": "Subject", "requiredWhen": "true", "inListView": true } ],
          "permissions": [
            { "role": "Staff", "actions": ["read", "create", "write", "delete"], "scope": "owner" },
            { "role": "Viewer", "actions": ["read"], "scope": "owner" }
          ]
        }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        extra?.Invoke(services);
        services.AddFrogFramework(o => o.LoadFrom(dir));
        services.Replace(ServiceDescriptor.Singleton<IUserDirectory>(new ShareTestDirectory(users)));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task RowSharing_grants_read_write_share_within_role_permissions()
    {
        UserContext ana = ShareUser("ana", "acme", "Staff"), budi = ShareUser("budi", "acme", "Staff"), cici = ShareUser("cici", "acme", "Staff"),
            eve = ShareUser("eve", "acme", "Viewer"), dodi = ShareUser("dodi", "acme", "Other"), zed = ShareUser("zed", "globex", "Staff");
        await using var provider = BuildShareProvider([ana, budi, cici, eve, dodi, zed]);
        var docs = provider.GetRequiredService<DocumentService>();
        var sharing = provider.GetRequiredService<DocShareService>();
        var inbox = provider.GetRequiredService<INotificationStore>();
        const string DT = "ShareNote";
        async Task<IReadOnlyList<string>> Visible(UserContext u) =>
            (await docs.ListAsync(u, DT, new ListRequest(), allowAllReadableFields: true)).Items.Select(i => (string)i["name"]!).ToList();

        var n1 = await docs.InsertAsync(ana, DT, new Dictionary<string, object?> { ["subject"] = "Budget draft" });
        var n2 = await docs.InsertAsync(ana, DT, new Dictionary<string, object?> { ["subject"] = "Private" });
        var b1 = await docs.InsertAsync(budi, DT, new Dictionary<string, object?> { ["subject"] = "Budi's own" });

        // Before sharing: owner scope only.
        Assert.True((await Visible(budi)).SequenceEqual([b1.Name]), "budi sees only his own note");
        await Assert.Throws<DocumentNotFoundException>(() => docs.GetAsync(budi, DT, n1.Name), "not shared: not found");
        await Assert.Throws<DocumentNotFoundException>(() => sharing.ShareAsync(budi, DT, n1.Name, "cici", false, false), "cannot share what you cannot see");

        // Read share.
        await sharing.ShareAsync(ana, DT, n1.Name, "BUDI", canWrite: false, canShare: false);
        Assert.True((await Visible(budi)).OrderBy(x => x).SequenceEqual(new[] { b1.Name, n1.Name }.OrderBy(x => x)), "shared note joins budi's list (owner OR shared)");
        Assert.Equal("Budget draft", (await docs.GetAsync(budi, DT, n1.Name)).Data["subject"], "budi can open the shared note");
        await Assert.Throws<DocumentNotFoundException>(() => docs.GetAsync(budi, DT, n2.Name), "other notes stay private");
        await Assert.Throws<PermissionDeniedException>(() =>
            docs.UpdateAsync(budi, DT, n1.Name, n1.Version, new Dictionary<string, object?> { ["subject"] = "x" }), "read share: no edit");
        var budiRights = await sharing.RightsAsync(budi, DT, n1.Name);
        Assert.True(!budiRights.CanManage, "read share without Share right cannot manage shares");
        await Assert.Throws<PermissionDeniedException>(() => sharing.ShareAsync(budi, DT, n1.Name, "cici", false, false), "and cannot share further");
        Assert.True(inbox.ForUser("acme", "budi").Any(n => n.DocName == n1.Name && n.Subject.Contains("shared")), "budi is notified");

        // Upgrade to write + share: budi edits and passes it on; he may grant write because he can write.
        await sharing.ShareAsync(ana, DT, n1.Name, "budi", canWrite: true, canShare: true);
        Assert.Equal(1, (await sharing.ListAsync(ana, DT, n1.Name)).Count, "upgrade replaces the share");
        var edited = await docs.UpdateAsync(budi, DT, n1.Name, n1.Version, new Dictionary<string, object?> { ["subject"] = "Budget v2" });
        Assert.Equal("Budget v2", edited.Data["subject"], "write share: edit allowed");
        var r = await sharing.RightsAsync(budi, DT, n1.Name);
        Assert.True(r.CanManage && r.CanGrantWrite && r.CanGrantShare, "write + share rights");
        await sharing.ShareAsync(budi, DT, n1.Name, "cici", canWrite: false, canShare: false);
        Assert.True((await Visible(cici)).Contains(n1.Name), "cici got it from budi");
        Assert.True(!(await sharing.RightsAsync(cici, DT, n1.Name)).CanGrantWrite, "cici (read only) cannot grant write");

        // A share never goes beyond the roles: Viewer has read only, dodi has no role on the DocType.
        await sharing.ShareAsync(ana, DT, n1.Name, "eve", canWrite: true, canShare: false);
        Assert.Equal("Budget v2", (await docs.GetAsync(eve, DT, n1.Name)).Data["subject"], "viewer reads the shared note");
        await Assert.Throws<PermissionDeniedException>(() =>
            docs.UpdateAsync(eve, DT, n1.Name, edited.Version, new Dictionary<string, object?> { ["subject"] = "y" }), "write share without a write role: no edit");
        await sharing.ShareAsync(ana, DT, n1.Name, "dodi", canWrite: false, canShare: false);
        await Assert.Throws<PermissionDeniedException>(() => docs.ListAsync(dodi, DT, new ListRequest()), "no role: the share has no effect");
        var views = await sharing.ListAsync(ana, DT, n1.Name);
        var dodiView = views.Single(v => v.Share.UserId == "dodi");
        var eveView = views.Single(v => v.Share.UserId == "eve");
        Assert.True(!dodiView.HasRoleRead && eveView.HasRoleRead && !eveView.HasRoleWrite, "drawer warns about missing roles");
        await Assert.Throws<PermissionDeniedException>(() =>
            docs.DeleteAsync(budi, DT, n1.Name, edited.Version), "a share never grants delete");

        // Invalid targets.
        await Assert.Throws<InvalidPayloadException>(() => sharing.ShareAsync(ana, DT, n1.Name, "ana", false, false), "not with yourself");
        await Assert.Throws<InvalidPayloadException>(() => sharing.ShareAsync(budi, DT, n1.Name, "ana", false, false), "not with the owner");
        await Assert.Throws<InvalidPayloadException>(() => sharing.ShareAsync(ana, DT, n1.Name, "zed", false, false), "not across tenants");
        await Assert.Throws<InvalidPayloadException>(() => sharing.ShareAsync(ana, DT, n1.Name, "nobody", false, false), "unknown user");

        // History.
        var audit = await docs.GetAuditAsync(ana, DT, n1.Name);
        Assert.True(audit.Any(a => a.Action == DocShareService.AuditShared && a.Comment == "Shared with Budi (read)"), "share recorded");
        Assert.True(audit.Any(a => a.Comment == "Share with Budi changed from read to read, write, share"), "change recorded");

        // Leaving and stopping.
        await Assert.Throws<PermissionDeniedException>(() => sharing.UnshareAsync(cici, DT, n1.Name, "eve"), "cici cannot remove others");
        Assert.True(await sharing.UnshareAsync(cici, DT, n1.Name, "cici"), "cici leaves");
        await Assert.Throws<DocumentNotFoundException>(() => docs.GetAsync(cici, DT, n1.Name), "and loses access");
        Assert.True(await sharing.UnshareAsync(ana, DT, n1.Name, "eve"), "owner stops sharing with eve");
        Assert.True(audit.Count < (await docs.GetAuditAsync(ana, DT, n1.Name)).Count, "unshares recorded");

        // Cleanup when the document is deleted.
        var current = await docs.GetAsync(ana, DT, n1.Name);
        await docs.DeleteAsync(ana, DT, n1.Name, current.Version);
        var cleanup = provider.GetServices<IEventHandler>().OfType<DocShareCleanupHandler>().Single();
        await cleanup.HandleAsync(new OutboxMessage
        {
            TenantId = "acme", Type = Framework.Jobs.EventTypes.DocumentDeleted, IdempotencyKey = Guid.NewGuid().ToString(),
            Payload = new() { ["docType"] = DT, ["name"] = n1.Name },
        }, CancellationToken.None);
        Assert.Equal(0, (await provider.GetRequiredService<IDocShareStore>().ForDocumentAsync("acme", DT, n1.Name)).Count, "shares removed with the document");
        Assert.True((await Visible(budi)).SequenceEqual([b1.Name]), "budi's list is back to his own");
    }

    [Test]
    public async Task RowSharing_postgres_store_and_query_scope()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        var tenant = "share-" + Guid.NewGuid().ToString("N")[..8];
        UserContext ana = ShareUser("ana", tenant, "Staff"), budi = ShareUser("Budi", tenant, "Staff");
        await using var provider = BuildShareProvider([ana, budi], s => s.AddFrogFrameworkPostgreSql(pg));
        await provider.MigrateFrogFrameworkDatabaseAsync();
        var docs = provider.GetRequiredService<DocumentService>();
        var sharing = provider.GetRequiredService<DocShareService>();
        var store = provider.GetRequiredService<IDocShareStore>();
        Assert.True(store is Framework.Persistence.PostgreSql.PostgresDocShareStore, "PostgreSQL share store is registered");

        var n1 = await docs.InsertAsync(ana, "ShareNote", new Dictionary<string, object?> { ["subject"] = "Shared" });
        await docs.InsertAsync(ana, "ShareNote", new Dictionary<string, object?> { ["subject"] = "Private" });
        var b1 = await docs.InsertAsync(budi, "ShareNote", new Dictionary<string, object?> { ["subject"] = "Own" });
        async Task<List<string>> Visible() => (await docs.ListAsync(budi, "ShareNote", new ListRequest(), allowAllReadableFields: true)).Items.Select(i => (string)i["name"]!).OrderBy(x => x).ToList();

        Assert.True((await Visible()).SequenceEqual([b1.Name]), "before sharing (SQL owner scope)");
        await sharing.ShareAsync(ana, "ShareNote", n1.Name, "budi", canWrite: false, canShare: false);
        Assert.True((await Visible()).SequenceEqual(new[] { b1.Name, n1.Name }.OrderBy(x => x)), "SQL scope: owner OR name = ANY(shared)");
        Assert.Equal("Budi", (await store.FindAsync(tenant, "ShareNote", n1.Name, "BUDI"))?.UserId, "case-insensitive lookup keeps the stored id");
        await Assert.Throws<PermissionDeniedException>(() =>
            docs.UpdateAsync(budi, "ShareNote", n1.Name, n1.Version, new Dictionary<string, object?> { ["subject"] = "x" }), "read share");
        await sharing.ShareAsync(ana, "ShareNote", n1.Name, "BUDI", canWrite: true, canShare: false);
        Assert.Equal(1, (await store.ForDocumentAsync(tenant, "ShareNote", n1.Name)).Count, "upsert on LOWER(user_id)");
        var upd = await docs.UpdateAsync(budi, "ShareNote", n1.Name, n1.Version, new Dictionary<string, object?> { ["subject"] = "Edited by Budi" });
        Assert.Equal("Edited by Budi", upd.Data["subject"], "write share in PostgreSQL");
        Assert.True(await sharing.UnshareAsync(ana, "ShareNote", n1.Name, "budi"), "unshare");
        Assert.True((await Visible()).SequenceEqual([b1.Name]), "back to owner scope");
        Assert.Equal(0, await store.RemoveDocumentAsync(tenant, "ShareNote", n1.Name), "nothing left to remove");
    }
}
