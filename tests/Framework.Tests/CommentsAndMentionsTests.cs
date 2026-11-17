using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Tests for P1-4: Document Comments &amp; @mention Timeline.</summary>
sealed partial class Tests
{
    [Test]
    public Task P1_4_ParseMentions_and_Segments_handle_ids_emails_and_dedup()
    {
        var text = "Please check @andi, @Budi. and @ANDI! Also email help@acme.co.id if needed.";
        var mentions = DocumentComments.ParseMentions(text);

        Assert.Equal(2, mentions.Count, "two distinct mentions extracted");
        Assert.Equal("andi", mentions[0], "first mention preserved");
        Assert.Equal("Budi", mentions[1], "trailing dot stripped");

        var segs = DocumentComments.Segments(text);
        Assert.True(segs.Any(s => s.IsMention && s.Text == "@andi"), "mention segment highlighted");
        Assert.True(!segs.Any(s => s.IsMention && s.Text.Contains("acme.co.id")), "plain email not treated as mention");
        return Task.CompletedTask;
    }

    [Test]
    public async Task P1_4_AddCommentAsync_appends_to_audit_timeline_and_enforces_scope()
    {
        var andi = U("andi");
        var rina = U("rina");
        var tono = U("tono"); // different tenant (globex)

        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 100_000m)
        });

        await Assert.Throws<ValidationFailedException>(
            () => Docs.AddCommentAsync(andi, PR, pr.Name, "   "),
            "empty comment rejected");

        await Assert.Throws<ValidationFailedException>(
            () => Docs.AddCommentAsync(andi, PR, pr.Name, new string('x', DocumentComments.MaxLength + 1)),
            "oversized comment rejected");

        // Rina (Procurement) has read scope on all PRs and can comment
        var entry = await Docs.AddCommentAsync(rina, PR, pr.Name, "Checked vendor quote, @andi please confirm qty.");
        Assert.Equal(AuditActions.Comment, entry.Action, "audit action is Comment");
        Assert.Equal(rina.FullName, entry.Actor, "actor is Rina");

        var timeline = await Docs.GetAuditAsync(andi, PR, pr.Name);
        Assert.True(timeline.Any(a => a.Action == AuditActions.Comment && a.Comment!.Contains("@andi")), "comment appears in audit timeline");

        // Cross-tenant user cannot comment
        await Assert.Throws<DocumentNotFoundException>(
            () => Docs.AddCommentAsync(tono, PR, pr.Name, "Cross-tenant probe"),
            "cross-tenant comment rejected");
    }

    [Test]
    public async Task P1_4_CommentMentionNotifier_delivers_in_app_notifications_within_scope()
    {
        var inbox = sp.GetRequiredService<INotificationStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var andi = U("andi");
        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 250_000m)
        });

        // Andi mentions:
        // - @budi (Manager, IT department -> in scope, should receive notification)
        // - @dewi (Finance -> in scope, should receive notification)
        // - @andi (self -> should NOT receive notification)
        // - @tono (globex tenant -> should NOT receive notification)
        // - @maya (Manager, MARKETING department -> out of IT row scope, should NOT receive notification)
        await Docs.AddCommentAsync(andi, PR, pr.Name, "Review needed: @budi @dewi @andi @tono @maya");

        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var budiNotif = inbox.ForUser("acme", "budi").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("mentioned you"));
        var dewiNotif = inbox.ForUser("acme", "dewi").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("mentioned you"));
        var andiNotif = inbox.ForUser("acme", "andi").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("mentioned you"));
        var mayaNotif = inbox.ForUser("acme", "maya").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("mentioned you"));
        var tonoNotif = inbox.ForUser("globex", "tono").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("mentioned you"));

        Assert.True(budiNotif is not null, "in-scope manager Budi notified");
        Assert.True(dewiNotif is not null, "in-scope Finance Dewi notified");
        Assert.True(andiNotif is null, "author self-mention not notified");
        Assert.True(mayaNotif is null, "out-of-scope manager Maya not notified");
        Assert.True(tonoNotif is null, "cross-tenant user Tono not notified");
        Assert.True(DocumentComments.IsMentionNotification(budiNotif!), "IsMentionNotification recognizes direct mention");
    }

    [Test]
    public async Task P1_4_Everyone_mention_notifies_all_in_scope_users_without_duplicates_and_pushes_live_feed()
    {
        var inbox = sp.GetRequiredService<INotificationStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var liveFeed = sp.GetRequiredService<Framework.Notifications.INotificationLiveFeed>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var liveReceived = new List<Notification>();
        using var sub = liveFeed.Subscribe("acme", "dewi", n =>
        {
            liveReceived.Add(n);
            return Task.CompletedTask;
        });

        var andi = U("andi");
        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 300_000m)
        });

        await Docs.AddCommentAsync(andi, PR, pr.Name, "FYI @budi and @everyone, please review this PR.");
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var budiMentions = inbox.ForUser("acme", "budi").Where(n => n.DocName == pr.Name && DocumentComments.IsMentionNotification(n)).ToList();
        var dewiMention = inbox.ForUser("acme", "dewi").FirstOrDefault(n => n.DocName == pr.Name && DocumentComments.IsMentionNotification(n));
        var rinaMention = inbox.ForUser("acme", "rina").FirstOrDefault(n => n.DocName == pr.Name && DocumentComments.IsMentionNotification(n));
        var andiMention = inbox.ForUser("acme", "andi").FirstOrDefault(n => n.DocName == pr.Name && DocumentComments.IsMentionNotification(n));
        var mayaMention = inbox.ForUser("acme", "maya").FirstOrDefault(n => n.DocName == pr.Name && DocumentComments.IsMentionNotification(n));
        var tonoMention = inbox.ForUser("globex", "tono").FirstOrDefault(n => n.DocName == pr.Name && DocumentComments.IsMentionNotification(n));

        Assert.Equal(1, budiMentions.Count, "direct + @everyone sends only one notification to Budi");
        Assert.True(dewiMention is not null, "Finance Dewi notified via @everyone");
        Assert.True(dewiMention!.Subject.Contains("@everyone"), "subject indicates @everyone mention");
        Assert.True(rinaMention is not null, "Procurement Rina notified via @everyone");
        Assert.True(andiMention is null, "author not notified via @everyone");
        Assert.True(mayaMention is null, "out-of-scope Maya not notified via @everyone");
        Assert.True(tonoMention is null, "cross-tenant Tono not notified via @everyone");
        Assert.True(liveReceived.Any(n => n.DocName == pr.Name && DocumentComments.IsMentionNotification(n)), "live feed pushed real-time notification to online user");
    }

    [Test]
    public Task P1_4_UiOptions_reads_pagination_settings_with_defaults()
    {
        var empty = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var defaults = Framework.Web.UiOptions.From(empty);
        Assert.Equal(20, defaults.MentionsPageSize, "default mentions page size is 20");
        Assert.Equal(10, defaults.HistoryCommentsPageSize, "default history & comments page size is 10");

        var customCfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ui:MentionsPageSize"] = "35",
                ["Ui:HistoryCommentsPageSize"] = "15"
            })
            .Build();
        var custom = Framework.Web.UiOptions.From(customCfg);
        Assert.Equal(35, custom.MentionsPageSize, "custom mentions page size loaded from config");
        Assert.Equal(15, custom.HistoryCommentsPageSize, "custom history & comments page size loaded from config");

        var invalidCfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ui:MentionsPageSize"] = "0",
                ["Ui:HistoryCommentsPageSize"] = "-5"
            })
            .Build();
        var fallback = Framework.Web.UiOptions.From(invalidCfg);
        Assert.Equal(20, fallback.MentionsPageSize, "non-positive mentions page size falls back to 20");
        Assert.Equal(10, fallback.HistoryCommentsPageSize, "non-positive history & comments page size falls back to 10");
        return Task.CompletedTask;
    }
}
