using Framework.Assignments;
using Framework.Documents;
using Framework.Jobs;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Framework.Validation;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Tests for P1-7: Assign To / ToDo &amp; "My Tasks" Widget.</summary>
sealed partial class Tests
{
    [Test]
    public async Task P1_7_Assign_happy_path_dedup_audit_and_validation()
    {
        var assignSvc = sp.GetRequiredService<AssignmentService>();
        var andi = U("andi");

        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 100_000m)
        });

        await Assert.Throws<ValidationFailedException>(
            () => assignSvc.AssignAsync(andi, PR, pr.Name, []),
            "empty assignees rejected");

        await Assert.Throws<ValidationFailedException>(
            () => assignSvc.AssignAsync(andi, PR, pr.Name, ["budi"], dueDate: assignSvc.Today.AddDays(-1)),
            "past due date rejected");

        await Assert.Throws<ValidationFailedException>(
            () => assignSvc.AssignAsync(andi, PR, pr.Name, ["budi"], description: new string('a', AssignmentLimits.MaxDescriptionLength + 1)),
            "oversized note rejected");

        await Assert.Throws<ValidationFailedException>(
            () => assignSvc.AssignAsync(andi, PR, pr.Name, ["nonexistent"]),
            "unknown user rejected");

        var due = assignSvc.Today.AddDays(3);
        var created = await assignSvc.AssignAsync(andi, PR, pr.Name, ["budi", "dewi", "BUDI"], due, AssignmentPriority.High, "Please review vendor quote.");
        Assert.Equal(2, created.Count, "two distinct assignees created");

        // Re-assigning budi while open skips duplicate cleanly, only creating any new ones
        var again = await assignSvc.AssignAsync(andi, PR, pr.Name, ["budi"], due, AssignmentPriority.Medium, "Duplicate");
        Assert.Equal(0, again.Count, "duplicate open assignment skipped");

        var docAssignments = await assignSvc.ListForDocumentAsync(andi, PR, pr.Name);
        Assert.Equal(2, docAssignments.Count, "two assignments stored on document");

        var timeline = await Docs.GetAuditAsync(andi, PR, pr.Name);
        Assert.Equal(2, timeline.Count(a => a.Action == AssignmentAuditActions.Assigned), "audit entries recorded for both assignments");
    }

    [Test]
    public async Task P1_7_Assign_enforces_scope_and_tenant_isolation()
    {
        var assignSvc = sp.GetRequiredService<AssignmentService>();
        var andi = U("andi");
        var tono = U("tono"); // globex tenant

        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 150_000m)
        });

        var eligible = await assignSvc.ListEligibleAssigneesAsync(andi, PR, pr.Name);
        Assert.True(eligible.Any(u => u.Id == "budi"), "in-scope IT manager Budi is eligible");
        Assert.True(eligible.Any(u => u.Id == "dewi"), "Finance Dewi is eligible");
        Assert.True(!eligible.Any(u => u.Id == "maya"), "out-of-scope Marketing manager Maya is excluded");
        Assert.True(!eligible.Any(u => u.Id == "tono"), "cross-tenant Tono is excluded");

        await Assert.Throws<ValidationFailedException>(
            () => assignSvc.AssignAsync(andi, PR, pr.Name, ["maya"]),
            "out-of-scope user cannot be assigned");

        await Assert.Throws<ValidationFailedException>(
            () => assignSvc.AssignAsync(andi, PR, pr.Name, ["tono"]),
            "cross-tenant user cannot be assigned");

        await Assert.Throws<DocumentNotFoundException>(
            () => assignSvc.ListForDocumentAsync(tono, PR, pr.Name),
            "cross-tenant listing blocked");
    }

    [Test]
    public async Task P1_7_Complete_Cancel_and_Update_permission_matrix()
    {
        var assignSvc = sp.GetRequiredService<AssignmentService>();
        var andi = U("andi");
        var budi = U("budi");
        var rina = U("rina");
        var dewi = U("dewi");

        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 200_000m)
        });

        var created = await assignSvc.AssignAsync(andi, PR, pr.Name, ["budi"], assignSvc.Today.AddDays(2), AssignmentPriority.Medium, "Initial");
        var task = created[0];

        // Unrelated user cannot close or edit
        await Assert.Throws<PermissionDeniedException>(
            () => assignSvc.CompleteAsync(rina, task.Id),
            "unrelated user cannot complete");

        await Assert.Throws<PermissionDeniedException>(
            () => assignSvc.CancelAsync(rina, task.Id),
            "unrelated user cannot cancel");

        // Assignee cannot edit due date/priority
        await Assert.Throws<PermissionDeniedException>(
            () => assignSvc.UpdateAsync(budi, task.Id, new UpdateAssignmentRequest(Priority: AssignmentPriority.High)),
            "assignee cannot edit assignment settings");

        // Assigner can update priority and due date
        var newDue = assignSvc.Today.AddDays(5);
        var updated = await assignSvc.UpdateAsync(andi, task.Id, new UpdateAssignmentRequest(
            DueDate: newDue, UpdateDueDate: true, Priority: AssignmentPriority.High, Description: "Urgent check", UpdateDescription: true));
        Assert.Equal(AssignmentPriority.High, updated.Priority, "priority updated");
        Assert.Equal(newDue, updated.DueDate, "due date updated");

        // Assignee can complete
        var completed = await assignSvc.CompleteAsync(budi, task.Id, "Approved and checked");
        Assert.Equal(AssignmentStatus.Completed, completed.Status, "assignment completed");
        Assert.Equal("budi", completed.ClosedBy, "closedBy set to Budi");

        // Cannot complete again
        await Assert.Throws<InvalidStateException>(
            () => assignSvc.CompleteAsync(budi, task.Id),
            "cannot complete already closed assignment");

        // Assign to Dewi then cancel by assigner
        var forDewi = (await assignSvc.AssignAsync(andi, PR, pr.Name, ["dewi"]))[0];
        var cancelled = await assignSvc.CancelAsync(andi, forDewi.Id, "No longer needed");
        Assert.Equal(AssignmentStatus.Cancelled, cancelled.Status, "assignment cancelled");

        // Re-assigning Dewi after cancellation works because unique index only guards open assignments
        var reopened = await assignSvc.AssignAsync(andi, PR, pr.Name, ["dewi"]);
        Assert.Equal(1, reopened.Count, "can assign again after prior task was closed");
    }

    [Test]
    public async Task P1_7_AssignmentNotifier_delivers_idempotent_notifications_and_live_feed()
    {
        var assignSvc = sp.GetRequiredService<AssignmentService>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var liveFeed = sp.GetRequiredService<INotificationLiveFeed>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var liveReceived = new List<Notification>();
        using var sub = liveFeed.Subscribe("acme", "budi", n =>
        {
            liveReceived.Add(n);
            return Task.CompletedTask;
        });

        var andi = U("andi");
        var budi = U("budi");

        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 250_000m)
        });

        // Self-assignment should succeed but not notify Andi himself; Budi should be notified + receive live push
        var created = await assignSvc.AssignAsync(andi, PR, pr.Name, ["andi", "budi"], assignSvc.Today.AddDays(1), AssignmentPriority.High, "Check lines");
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var budiNotif = inbox.ForUser("acme", "budi").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("assigned"));
        var andiSelfNotif = inbox.ForUser("acme", "andi").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("assigned"));

        Assert.True(budiNotif is not null, "Budi received in-app assignment notification");
        Assert.True(andiSelfNotif is null, "Andi did not receive notification for self-assignment");
        Assert.True(liveReceived.Any(n => n.Id == budiNotif!.Id), "Budi received live SignalR feed event");

        // Budi completes task -> Andi receives completion notification
        var budiTask = created.First(a => a.AssignedTo == "budi");
        await assignSvc.CompleteAsync(budi, budiTask.Id, "Looks good");
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var completeNotif = inbox.ForUser("acme", "andi").FirstOrDefault(n => n.DocName == pr.Name && n.Subject.Contains("completed assignment"));
        Assert.True(completeNotif is not null, "Assigner Andi notified on task completion");
    }

    [Test]
    public async Task P1_7_DocumentDelete_cancels_open_assignments_and_hides_my_tasks()
    {
        var assignSvc = sp.GetRequiredService<AssignmentService>();
        var assignStore = sp.GetRequiredService<IAssignmentStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var andi = U("andi");
        var budi = U("budi");

        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 50_000m)
        });

        await assignSvc.AssignAsync(andi, PR, pr.Name, ["budi"]);
        var before = await assignSvc.CountMyTasksAsync(budi);
        Assert.Equal(1, before.Open, "Budi sees 1 open task before delete");

        await Docs.DeleteAsync(andi, PR, pr.Name, pr.Version);
        while ((await processor.ProcessDueAsync()).Processed > 0) { }

        var stored = await assignStore.QueryAsync(new AssignmentQuery("acme", DocType: PR, DocName: pr.Name));
        Assert.Equal(1, stored.Count, "assignment record remains in store for history");
        Assert.Equal(AssignmentStatus.Cancelled, stored[0].Status, "assignment automatically cancelled on document delete");
        Assert.Equal("Document deleted", stored[0].CloseNote, "close note indicates document deletion");

        var after = await assignSvc.CountMyTasksAsync(budi);
        Assert.Equal(0, after.Open, "My Tasks count is 0 after document deletion");
    }

    [Test]
    public async Task P1_7_DueToday_Overdue_counting_and_ReminderJob_once_per_day()
    {
        var clock = new ManualClock();
        await UseHookProviderAsync(s => s.AddSingleton<TimeProvider>(clock));

        var assignSvc = sp.GetRequiredService<AssignmentService>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var andi = U("andi");
        var budi = U("budi");

        var pr1 = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 100_000m)
        });
        var pr2 = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 120_000m)
        });

        var startDay = assignSvc.Today;
        await assignSvc.AssignAsync(andi, PR, pr1.Name, ["budi"], startDay, AssignmentPriority.High, "Due today initially");
        await assignSvc.AssignAsync(andi, PR, pr2.Name, ["budi"], startDay.AddDays(1), AssignmentPriority.Medium, "Due tomorrow initially");

        var countsDay0 = await assignSvc.CountMyTasksAsync(budi);
        Assert.Equal(2, countsDay0.Open, "2 open tasks on day 0");
        Assert.Equal(1, countsDay0.DueToday, "1 due today on day 0");
        Assert.Equal(0, countsDay0.Overdue, "0 overdue on day 0");

        // Advance clock by 1 day: pr1 becomes overdue, pr2 becomes due today
        clock.Advance(TimeSpan.FromDays(1));
        var countsDay1 = await assignSvc.CountMyTasksAsync(budi);
        Assert.Equal(2, countsDay1.Open, "2 open tasks on day 1");
        Assert.Equal(1, countsDay1.DueToday, "1 due today on day 1");
        Assert.Equal(1, countsDay1.Overdue, "1 overdue on day 1");

        var job = new AssignmentReminderJob();
        var ctx1 = new ScheduledJobContext
        {
            TenantId = "acme",
            SystemUser = UserContext.System("acme"),
            Docs = Docs,
            Services = sp,
            ScheduledTime = clock.GetUtcNow()
        };

        // Running reminder twice on the same day must only create 1 reminder per task
        await job.ExecuteAsync(ctx1, CancellationToken.None);
        await job.ExecuteAsync(ctx1, CancellationToken.None);

        var remindersDay1 = inbox.ForUser("acme", "budi")
            .Where(n => n.Subject.StartsWith("Overdue task:") || n.Subject.StartsWith("Task due today:"))
            .ToList();
        Assert.Equal(2, remindersDay1.Count, "exactly 2 reminders sent on day 1 despite 2 job runs");
        Assert.True(remindersDay1.Any(n => n.DocName == pr1.Name && n.Subject.StartsWith("Overdue task:")), "pr1 flagged overdue");
        Assert.True(remindersDay1.Any(n => n.DocName == pr2.Name && n.Subject.StartsWith("Task due today:")), "pr2 flagged due today");

        // Advance another day -> new daily reminders sent
        clock.Advance(TimeSpan.FromDays(1));
        await job.ExecuteAsync(new ScheduledJobContext
        {
            TenantId = "acme",
            SystemUser = UserContext.System("acme"),
            Docs = Docs,
            Services = sp,
            ScheduledTime = clock.GetUtcNow()
        }, CancellationToken.None);

        var remindersDay2 = inbox.ForUser("acme", "budi")
            .Where(n => n.Subject.StartsWith("Overdue task:") || n.Subject.StartsWith("Task due today:"))
            .ToList();
        Assert.Equal(4, remindersDay2.Count, "new reminders sent on day 2");
    }
}
