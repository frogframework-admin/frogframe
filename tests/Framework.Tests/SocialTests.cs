using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Social;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// P3-3: tags (shared, editable by users who can edit the document, filterable in lists), personal bookmarks, and
/// follow (followers are notified of changes they did not make, if they can still read the document).
/// </summary>
sealed partial class Tests
{
    private sealed class SocialUsers(params UserContext[] users) : IUserDirectory
    {
        public Task<UserContext?> FindAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult(users.FirstOrDefault(u => string.Equals(u.Id, userId, StringComparison.OrdinalIgnoreCase)));
        public Task<IReadOnlyList<UserContext>> ListAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserContext>>(users.Where(u => u.TenantId == tenantId).ToList());
    }

    private static readonly UserContext SocAna = new("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
    private static readonly UserContext SocBudi = new("budi", "Budi", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
    private static readonly UserContext SocCarol = new("carol", "Carol", "acme", new HashSet<string> { "Guest" }, new Dictionary<string, string>());
    private static readonly UserContext SocTono = new("tono", "Tono", "globex", new HashSet<string> { "Staff" }, new Dictionary<string, string>());

    private static ServiceProvider BuildSocialProvider(Action<IServiceCollection>? extra = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-social-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Staff read every task, but only edit their own; Guest has no access.
        File.WriteAllText(Path.Combine(dir, "SocTask.doctype.json"), """
        { "name": "SocTask", "version": 1, "title": "Task", "module": "CORE", "naming": { "pattern": "T-{###}" },
          "fields": [ { "name": "subject", "type": "Data", "label": "Subject" } ],
          "listView": { "titleField": "subject", "columns": ["subject"] },
          "permissions": [ { "role": "Staff", "actions": ["read"], "scope": "all" },
                           { "role": "Staff", "actions": ["create", "write", "delete"], "scope": "owner" } ] }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrogFramework(o => o.LoadFrom(dir));
        services.AddSingleton<IUserDirectory>(new SocialUsers(SocAna, SocBudi, SocCarol, SocTono));
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Social_tags_bookmarks_and_follow_respect_permissions_and_notify_followers()
    {
        await using var sp = BuildSocialProvider();
        var docs = sp.GetRequiredService<DocumentService>();
        var social = sp.GetRequiredService<DocSocialService>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var processor = new OutboxProcessor(sp.GetRequiredService<IOutboxStore>(), sp.GetServices<IEventHandler>(), TimeProvider.System);
        async Task Drain() { while ((await processor.ProcessDueAsync()).Processed > 0) { } }

        var t1 = await docs.InsertAsync(SocAna, "SocTask", new Dictionary<string, object?> { ["subject"] = "Renew contract" });
        var t2 = await docs.InsertAsync(SocAna, "SocTask", new Dictionary<string, object?> { ["subject"] = "Order laptops" });
        var t3 = await docs.InsertAsync(SocBudi, "SocTask", new Dictionary<string, object?> { ["subject"] = "Budi's task" });
        await Drain();

        // ---- tags
        Assert.Equal("Urgent", string.Join(",", await social.AddTagAsync(SocAna, "SocTask", t1.Name, "  Urgent ")), "tag trimmed");
        Assert.Equal("Urgent", string.Join(",", await social.AddTagAsync(SocAna, "SocTask", t1.Name, "urgent")), "same tag in another case is not added twice");
        Assert.Equal("Q4 plan|Urgent", string.Join("|", await social.AddTagAsync(SocAna, "SocTask", t1.Name, "Q4   plan")), "spaces collapsed, sorted");
        await Assert.Throws<InvalidPayloadException>(() => social.AddTagAsync(SocAna, "SocTask", t1.Name, "bad,tag"), "comma not allowed");
        await Assert.Throws<InvalidPayloadException>(() => social.AddTagAsync(SocAna, "SocTask", t1.Name, new string('x', 41)), "too long");
        await Assert.Throws<PermissionDeniedException>(() => social.AddTagAsync(SocBudi, "SocTask", t1.Name, "mine"), "Budi cannot edit Ana's task, so cannot tag it");
        await Assert.Throws<PermissionDeniedException>(() => social.StateAsync(SocCarol, "SocTask", t1.Name), "no read access, no state");
        await Assert.Throws<DocumentNotFoundException>(() => social.StateAsync(SocTono, "SocTask", t1.Name), "other tenant");

        var budiView = await social.StateAsync(SocBudi, "SocTask", t1.Name);
        Assert.True(budiView is { CanEditTags: false, Bookmarked: false, Following: false } && budiView.Tags.SequenceEqual(["Q4 plan", "Urgent"]), "readers see tags");
        Assert.Equal("Urgent", string.Join(",", await social.AddTagAsync(SocAna, "SocTask", t2.Name, "URGENT")), "existing spelling reused on the DocType");
        await social.AddTagAsync(SocBudi, "SocTask", t3.Name, "Budget");

        var usage = await social.TagSuggestionsAsync(SocBudi, "SocTask", null);
        Assert.Equal("Urgent:2,Budget:1,Q4 plan:1", string.Join(",", usage.Select(u => $"{u.Tag}:{u.Count}")), "suggestions, most used first");
        Assert.Equal("Q4 plan", usage.Where(u => u.Tag.StartsWith("q", StringComparison.OrdinalIgnoreCase)).Single().Tag, "prefix filter");

        // List: tag filter (case-insensitive) and the tags of each row.
        var urgent = await docs.ListAsync(SocBudi, "SocTask", new ListRequest([new FilterClause(DocTags.FilterField, "=", "urgent")]));
        Assert.Equal($"{t1.Name},{t2.Name}", string.Join(",", urgent.Items.Select(i => i["name"]).Order()), "tag filter");
        var row = urgent.Items.Single(i => (string)i["name"]! == t1.Name);
        Assert.True(row[DocTags.ListKey] is IReadOnlyList<string> rt && rt.SequenceEqual(["Q4 plan", "Urgent"]), "row carries its tags");
        Assert.Equal(0, (await docs.ListAsync(SocBudi, "SocTask", new ListRequest([new FilterClause(DocTags.FilterField, "=", "nothing")]))).Total, "unknown tag: no rows");
        var both = await docs.ListAsync(SocBudi, "SocTask", new ListRequest([new FilterClause(DocTags.FilterField, "=", "Urgent"), new FilterClause(DocTags.FilterField, "=", "Q4 plan")]));
        Assert.Equal(t1.Name, (string)both.Items.Single()["name"]!, "two tag filters = both tags");
        await Assert.Throws<InvalidPayloadException>(() => docs.ListAsync(SocBudi, "SocTask", new ListRequest([new FilterClause(DocTags.FilterField, "like", "urg")])), "only '='");

        Assert.Equal("Q4 plan", string.Join(",", await social.RemoveTagAsync(SocAna, "SocTask", t1.Name, "URGENT")), "remove in any case");
        var history = await docs.GetAuditAsync(SocAna, "SocTask", t1.Name);
        Assert.True(history.Any(a => a.Action == DocSocialService.AuditTagged && a.Comment == "Tag added: Urgent")
                    && history.Any(a => a.Action == DocSocialService.AuditUntagged && a.Comment == "Tag removed: Urgent"), "tag changes are in the history");

        // ---- bookmarks (personal)
        Assert.True(await social.SetBookmarkAsync(SocBudi, "SocTask", t1.Name, true), "bookmarked");
        Assert.True(await social.SetBookmarkAsync(SocBudi, "SocTask", t1.Name, true), "bookmarking twice is fine");
        var budiBookmarks = await social.BookmarksAsync(SocBudi);
        Assert.True(budiBookmarks.Single() is { DocType: "SocTask", DocTypeTitle: "Task", Title: "Renew contract" } b && b.Name == t1.Name, "bookmark with title");
        Assert.Equal(0, (await social.BookmarksAsync(SocAna)).Count, "bookmarks are personal");
        Assert.True((await social.StateAsync(SocBudi, "SocTask", t1.Name)).Bookmarked, "state shows bookmark");
        await Assert.Throws<PermissionDeniedException>(() => social.SetBookmarkAsync(SocCarol, "SocTask", t1.Name, true), "cannot bookmark what you cannot read");

        // ---- follow
        Assert.True(await social.SetFollowAsync(SocBudi, "SocTask", t1.Name, true), "Budi follows T1");
        Assert.True(await social.SetFollowAsync(SocAna, "SocTask", t1.Name, true), "Ana follows T1");
        Assert.Equal(2, (await social.StateAsync(SocAna, "SocTask", t1.Name)).Followers, "two followers");
        Assert.Equal(t1.Name, (await social.FollowingAsync(SocBudi)).Single().Name, "following list");

        IReadOnlyList<Notification> FollowNotes(UserContext u) => inbox.ForUser(u.TenantId, u.Id).Where(n => n.DocName == t1.Name || n.Subject.Contains(t1.Name)).ToList();

        var current = await docs.GetAsync(SocAna, "SocTask", t1.Name);
        await docs.UpdateAsync(SocAna, "SocTask", t1.Name, current.Version, new Dictionary<string, object?> { ["subject"] = "Renew contract 2027" });
        await Drain();
        Assert.Equal("Ana updated Task " + t1.Name, FollowNotes(SocBudi).Single().Subject, "follower notified of the update");
        Assert.Equal(0, FollowNotes(SocAna).Count, "the user who made the change is not notified");
        await Drain();
        Assert.Equal(1, FollowNotes(SocBudi).Count, "processing again does not duplicate");

        await docs.AddCommentAsync(SocAna, "SocTask", t1.Name, "Please check the price");
        await Drain();
        Assert.True(FollowNotes(SocBudi).Any(n => n.Subject == $"Ana commented on Task {t1.Name}" && n.Body == "Please check the price"), "comment notification with preview");
        await docs.AddCommentAsync(SocAna, "SocTask", t1.Name, "@budi please approve");
        await Drain();
        var budiAll = FollowNotes(SocBudi);
        Assert.True(budiAll.Any(n => n.Subject.Contains("mentioned you")) && budiAll.Count(n => n.Subject.Contains("commented on")) == 1,
            "a mentioned follower gets the mention, not a second follow notification");

        // Unfollow stops notifications.
        await social.SetFollowAsync(SocBudi, "SocTask", t1.Name, false);
        var before = FollowNotes(SocBudi).Count;
        current = await docs.GetAsync(SocAna, "SocTask", t1.Name);
        await docs.UpdateAsync(SocAna, "SocTask", t1.Name, current.Version, new Dictionary<string, object?> { ["subject"] = "Renew contract 2028" });
        await Drain();
        Assert.Equal(before, FollowNotes(SocBudi).Count, "unfollowed: no notification");

        // Delete: remaining followers are told, and tags, bookmarks and follows of the document are removed.
        await social.SetFollowAsync(SocBudi, "SocTask", t1.Name, true);
        await docs.DeleteAsync(SocAna, "SocTask", t1.Name, (await docs.GetAsync(SocAna, "SocTask", t1.Name)).Version);
        await Drain();
        Assert.True(inbox.ForUser("acme", "budi").Any(n => n.Subject == $"Ana deleted Task {t1.Name}" && n.DocName is null), "delete notification without a link");
        var store = sp.GetRequiredService<IDocSocialStore>();
        Assert.True((await store.TagsAsync("acme", "SocTask", t1.Name)).Count == 0 && (await store.FollowersAsync("acme", "SocTask", t1.Name)).Count == 0
                    && !await store.IsBookmarkedAsync("acme", "budi", "SocTask", t1.Name), "social data of the deleted document removed");
        Assert.Equal(0, (await social.BookmarksAsync(SocBudi)).Count, "deleted bookmark gone");
    }

    [Test]
    public Task Social_follow_notification_texts_and_tag_rules()
    {
        Dictionary<string, string?> P(params (string K, string? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);
        Assert.Equal("Ana moved Task T-1 to Approved", DocFollowNotifier.Describe(EventTypes.WorkflowTransitioned, P(("workflowState", "Approved")), "Ana", "Task", "T-1").Subject, "workflow");
        Assert.Equal("Ana submitted Task T-1", DocFollowNotifier.Describe(EventTypes.DocumentSubmit, P(), "Ana", "Task", "T-1").Subject, "submit");
        Assert.Equal("Ana ran 'close' on Task T-1", DocFollowNotifier.Describe(EventTypes.DocumentAction("close"), P(), "Ana", "Task", "T-1").Subject, "custom action");
        Assert.True(new DocFollowNotifier(null!, null!, null!, null!, null!, null!, TimeProvider.System) is var n
                    && n.CanHandle(EventTypes.DocumentUpdated) && n.CanHandle(EventTypes.DocumentAction("close")) && n.CanHandle(EventTypes.CommentAdded)
                    && !n.CanHandle(EventTypes.DocumentInserted) && !n.CanHandle(EventTypes.AssignmentChanged) && !n.CanHandle("webhook.sent"), "handled events");

        Assert.Equal("Proyek Q4 & Audit #2", DocTags.Normalize("  Proyek   Q4 & Audit #2 ", out _), "letters, digits, spaces and symbols");
        Assert.Equal("Résumé/2026", DocTags.Normalize("Résumé/2026", out _), "unicode letters");
        Assert.True(DocTags.Normalize("", out var e1) is null && e1 == "Enter a tag.", "empty");
        Assert.True(DocTags.Normalize("-start", out _) is null, "must start with a letter or digit");
        Assert.True(DocTags.Normalize("a<b>", out _) is null, "no markup characters");
        Assert.True(DocTags.Hue("Urgent") == DocTags.Hue("URGENT") && DocTags.Hue("Urgent") is >= 0 and < 360 && DocTags.Hue("Urgent") != DocTags.Hue("Budget"),
            "fixed colour per tag, case-insensitive");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Social_postgres_store_round_trip()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        await using var sp = BuildSocialProvider(s => s.AddFrogFrameworkPostgreSql(pg));
        await sp.MigrateFrogFrameworkDatabaseAsync();
        var store = sp.GetRequiredService<IDocSocialStore>();
        Assert.True(store is PostgresDocSocialStore, "PostgreSQL store registered");
        var tenant = "soc-" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;

        Assert.True(await store.AddTagAsync(new DocTag(tenant, "SocTask", "T-1", "Urgent", "ana", now)), "tag added");
        Assert.True(!await store.AddTagAsync(new DocTag(tenant, "SocTask", "T-1", "URGENT", "ana", now)), "case-insensitive unique");
        await store.AddTagAsync(new DocTag(tenant, "SocTask", "T-1", "100%_done", "ana", now.AddSeconds(1)));
        await store.AddTagAsync(new DocTag(tenant, "SocTask", "T-2", "urgent", "budi", now.AddSeconds(2)));
        Assert.Equal("100%_done,Urgent", string.Join(",", (await store.TagsAsync(tenant, "SocTask", "T-1")).Select(t => t.Tag)), "tags of a document");
        Assert.Equal("T-1,T-2", string.Join(",", await store.NamesWithTagAsync(tenant, "SocTask", "uRGENT", 10)), "names with tag");
        var map = await store.TagsForAsync(tenant, "SocTask", ["T-1", "T-2", "T-9"]);
        Assert.True(map.Count == 2 && map["T-2"].Single() == "urgent", "tags for a page of rows");
        Assert.Equal("Urgent:2", string.Join(",", (await store.TagUsageAsync(tenant, "SocTask", "u", 10)).Select(u => $"{u.Tag}:{u.Count}")), "usage keeps first spelling");
        Assert.Equal("100%_done:1", string.Join(",", (await store.TagUsageAsync(tenant, "SocTask", "100%", 10)).Select(u => $"{u.Tag}:{u.Count}")), "prefix with LIKE characters is literal");
        Assert.Equal(0, (await store.TagUsageAsync(tenant, "SocTask", "1_0", 10)).Count, "'_' is not a wildcard");
        Assert.True(await store.RemoveTagAsync(tenant, "SocTask", "T-1", "urgent"), "remove in any case");

        Assert.True(await store.AddBookmarkAsync(new Bookmark(tenant, "Budi", "SocTask", "T-1", now)), "bookmark");
        Assert.True(!await store.AddBookmarkAsync(new Bookmark(tenant, "budi", "SocTask", "T-1", now)), "user id case-insensitive");
        Assert.True(await store.IsBookmarkedAsync(tenant, "BUDI", "SocTask", "T-1") && await store.CountBookmarksAsync(tenant, "budi") == 1, "bookmark lookups");
        Assert.True(await store.FollowAsync(new DocFollow(tenant, "SocTask", "T-1", "budi", now)), "follow");
        Assert.True(await store.FollowAsync(new DocFollow(tenant, "SocTask", "T-2", "budi", now.AddSeconds(5))), "follow 2");
        Assert.Equal("T-2,T-1", string.Join(",", (await store.FollowingAsync(tenant, "Budi", 10)).Select(f => f.DocName)), "following, newest first");
        Assert.Equal(1, (await store.FollowersAsync(tenant, "SocTask", "T-1")).Count, "followers");

        Assert.Equal(3, await store.RemoveDocumentAsync(tenant, "SocTask", "T-1"), "tag + bookmark + follow removed");
        Assert.True(!await store.IsFollowingAsync(tenant, "SocTask", "T-1", "budi") && (await store.FollowersAsync(tenant, "SocTask", "T-2")).Count == 1, "only T-1 removed");
        await store.RemoveDocumentAsync(tenant, "SocTask", "T-2");
    }
}
