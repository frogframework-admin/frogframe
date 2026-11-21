using System.Text;
using Framework.Documents;
using Framework.Imports;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Tasks;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// P2-3: background tasks. Users queue long operations (imports, bulk actions); a worker runs them with the user's
/// current permissions, saves progress, honours cancel, reports the result and an output file, and notifies the user.
/// Tasks interrupted by a stopped server are not re-run automatically.
/// </summary>
sealed partial class Tests
{
    private sealed class TaskManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Test handler: "steps" progress reports; "fail" = user error, "crash" = unexpected error; waits on a gate.</summary>
    private sealed class StepHandler : IBackgroundTaskHandler
    {
        public string Kind => "test-steps";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? SeenUserRoles;
        public byte[]? SeenInput;

        public async Task<BackgroundTaskOutcome> RunAsync(BackgroundTaskContext ctx, CancellationToken ct)
        {
            SeenUserRoles = string.Join(",", ctx.User.Roles.Order());
            SeenInput = ctx.Task.Input;
            if (ctx.Task.Payload.Contains("fail")) throw new InvalidPayloadException("Row 3 has no subject.");
            if (ctx.Task.Payload.Contains("crash")) throw new NullReferenceException("internal detail");
            await ctx.ReportAsync(10, "step 1", ct);
            Started.TrySetResult();
            await Gate.Task.WaitAsync(ct);
            for (var i = 2; i <= 5; i++) { await Task.Delay(1100, ct); await ctx.ReportAsync(i * 20, $"step {i}", ct); }
            return new BackgroundTaskOutcome("5 steps done.", Encoding.UTF8.GetBytes("a,b\n"), "steps.csv");
        }
    }

    private static readonly UserContext BtAna = new("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
    private static readonly UserContext BtBudi = new("budi", "Budi", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
    private static readonly UserContext BtAdmin = new("root", "Root", "acme", new HashSet<string> { UserContext.SystemManager }, new Dictionary<string, string>());

    private static ServiceProvider BuildTaskProvider(StepHandler? steps = null, TimeProvider? clock = null, Action<IServiceCollection>? extra = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "BtNote.doctype.json"), """
        { "name": "BtNote", "version": 1, "title": "Note", "module": "CORE", "naming": { "pattern": "BN-{#####}" },
          "document": { "allowSubmit": true, "allowCancel": true, "allowDelete": true },
          "fields": [ { "name": "subject", "type": "Data", "label": "Subject", "requiredWhen": "true" },
                      { "name": "qty", "type": "Int", "label": "Qty" } ],
          "permissions": [ { "role": "Staff", "actions": ["read", "create", "write", "submit", "cancel", "delete"], "scope": "all" } ] }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrogFramework(o => o.LoadFrom(dir));
        services.AddSingleton<IUserDirectory>(new SocialUsers(BtAna, BtBudi, BtAdmin));
        if (steps is not null) services.AddSingleton<IBackgroundTaskHandler>(steps);
        if (clock is not null) services.AddSingleton<TimeProvider>(clock);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Background_tasks_run_with_progress_cancel_errors_and_notifications()
    {
        var steps = new StepHandler();
        await using var sp = BuildTaskProvider(steps);
        var tasks = sp.GetRequiredService<BackgroundTaskService>();
        var runner = sp.GetRequiredService<BackgroundTaskRunner>();
        var inbox = sp.GetRequiredService<INotificationStore>();

        await Assert.Throws<InvalidPayloadException>(() => tasks.EnqueueAsync(BtAna, "no-such-kind", "x"), "unknown kind");
        await Assert.Throws<InvalidPayloadException>(() => tasks.EnqueueAsync(BtAna, "test-steps", "x", "{}", new byte[BackgroundTaskLimits.MaxInputBytes + 1]), "input too large");

        // Success with progress, output and notification.
        var job = await tasks.EnqueueAsync(BtAna, "test-steps", "Count to five", "{}", [1, 2, 3]);
        Assert.Equal(BackgroundTaskStatus.Queued, job.Status, "queued");
        var run = Task.Run(() => runner.RunNextAsync());
        await steps.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var running = await tasks.GetAsync(BtAna, job.Id);
        Assert.True(running is { Status: BackgroundTaskStatus.Running, Progress: 10, ProgressMessage: "step 1" }, "running with progress");
        steps.Gate.SetResult();
        Assert.True(await run, "ran a task");
        var done = await tasks.GetAsync(BtAna, job.Id);
        Assert.True(done is { Status: BackgroundTaskStatus.Succeeded, Progress: 100, Result: "5 steps done.", OutputName: "steps.csv" } && done.FinishedAt is not null, "succeeded");
        Assert.True(steps.SeenUserRoles == "Staff" && steps.SeenInput!.SequenceEqual(new byte[] { 1, 2, 3 }), "ran as the user, with the input");
        Assert.Equal("a,b\n", Encoding.UTF8.GetString((await tasks.OutputAsync(BtAna, job.Id)).Content), "output file");
        Assert.True(inbox.ForUser("acme", "ana").Any(n => n.Subject == "Count to five finished" && n.Body == "5 steps done."), "user notified");
        Assert.True(!await runner.RunNextAsync(), "queue empty");

        // Visibility: own tasks; administrators may list everyone's.
        await Assert.Throws<DocumentNotFoundException>(() => tasks.GetAsync(BtBudi, job.Id), "other users cannot see it");
        Assert.Equal(0, (await tasks.ListAsync(BtBudi)).Count, "Budi has none");
        Assert.Equal(1, (await tasks.ListAsync(BtAdmin, allUsers: true)).Count, "admin sees all");
        Assert.Equal(0, (await tasks.ListAsync(BtBudi, allUsers: true)).Count, "non-admins only see their own, even when asking for all");

        // Errors: a user error shows its message; an unexpected one does not leak details.
        var bad = await tasks.EnqueueAsync(BtAna, "test-steps", "Bad file", "{\"fail\":1}");
        var crash = await tasks.EnqueueAsync(BtAna, "test-steps", "Crash", "{\"crash\":1}");
        await runner.RunNextAsync(); await runner.RunNextAsync();
        Assert.True(await tasks.GetAsync(BtAna, bad.Id) is { Status: BackgroundTaskStatus.Failed, Error: "Row 3 has no subject." }, "user error");
        var crashed = await tasks.GetAsync(BtAna, crash.Id);
        Assert.True(crashed.Status == BackgroundTaskStatus.Failed && !crashed.Error!.Contains("internal detail") && crashed.Error.Contains("unexpected"), crashed.Error!);
        Assert.True(inbox.ForUser("acme", "ana").Any(n => n.Subject == "Bad file failed"), "failure notified");

        // Cancel: a queued task never runs; a running one stops at its next report.
        var queued = await tasks.EnqueueAsync(BtAna, "test-steps", "Never runs");
        Assert.True(await tasks.CancelAsync(BtAna, queued.Id), "cancelled while queued");
        Assert.True(await tasks.GetAsync(BtAna, queued.Id) is { Status: BackgroundTaskStatus.Cancelled }, "cancelled");
        Assert.True(!await runner.RunNextAsync(), "a cancelled task is not claimed");

        steps.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = await tasks.EnqueueAsync(BtAna, "test-steps", "Slow", "{\"slow\":1}");
        var slowRun = Task.Run(() => runner.RunNextAsync());
        await Task.Delay(300);
        await tasks.CancelAsync(BtAna, slow.Id);
        steps.Gate.SetResult();
        await slowRun.WaitAsync(TimeSpan.FromSeconds(20));
        var stopped = await tasks.GetAsync(BtAna, slow.Id);
        Assert.True(stopped.Status == BackgroundTaskStatus.Cancelled && stopped.Error!.StartsWith("Cancelled at"), $"running task cancelled: {stopped.Status} {stopped.Error}");
        Assert.True(!await tasks.CancelAsync(BtAna, slow.Id), "cancelling a finished task does nothing");

        // Limit of active tasks per user, and run again.
        for (var i = 0; i < BackgroundTaskLimits.MaxActivePerUser; i++) await tasks.EnqueueAsync(BtBudi, "test-steps", $"B{i}", "{\"fail\":1}");
        await Assert.Throws<InvalidPayloadException>(() => tasks.EnqueueAsync(BtBudi, "test-steps", "one too many"), "active limit");
        while (await runner.RunNextAsync()) { }
        var notFinished = await tasks.EnqueueAsync(BtAna, "test-steps", "q");
        await Assert.Throws<InvalidStateException>(() => tasks.RunAgainAsync(BtAna, notFinished.Id), "only finished tasks run again");
        var again = await tasks.RunAgainAsync(BtAna, job.Id);
        Assert.True(again.Id != job.Id && again.Title == job.Title && again.Status == BackgroundTaskStatus.Queued, "queued again");
    }

    [Test]
    public async Task Background_tasks_of_a_stopped_worker_are_interrupted_not_rerun()
    {
        var clock = new TaskManualClock(DateTimeOffset.Parse("2026-11-21T08:00:00Z"));
        await using var sp = BuildTaskProvider(new StepHandler(), clock);
        var tasks = sp.GetRequiredService<BackgroundTaskService>();
        var store = sp.GetRequiredService<IBackgroundTaskStore>();
        var job = await tasks.EnqueueAsync(BtAna, "test-steps", "Long import");
        Assert.True(await store.ClaimAsync("dead-worker", clock.Now) is { Status: BackgroundTaskStatus.Running }, "claimed by a worker that then stops");
        await sp.GetRequiredService<BackgroundTaskRunner>().MaintainAsync();
        Assert.Equal(BackgroundTaskStatus.Running, (await tasks.GetAsync(BtAna, job.Id)).Status, "lease still valid");
        clock.Now += BackgroundTaskLimits.Lease + TimeSpan.FromSeconds(1);
        await sp.GetRequiredService<BackgroundTaskRunner>().MaintainAsync();
        Assert.Equal(BackgroundTaskStatus.Interrupted, (await tasks.GetAsync(BtAna, job.Id)).Status, "interrupted after the lease");
        Assert.True(sp.GetRequiredService<INotificationStore>().ForUser("acme", "ana").Any(n => n.Subject == "Long import was interrupted"), "user told");
        Assert.True(await store.ClaimAsync("other", clock.Now) is null, "not picked up again");
        clock.Now += BackgroundTaskLimits.Retention + TimeSpan.FromHours(1);
        await sp.GetRequiredService<BackgroundTaskRunner>().MaintainAsync();
        Assert.Equal(0, (await tasks.ListAsync(BtAna)).Count, "old finished tasks pruned");
    }

    [Test]
    public async Task Background_import_and_bulk_action_handlers()
    {
        await using var sp = BuildTaskProvider();
        var tasks = sp.GetRequiredService<BackgroundTaskService>();
        var runner = sp.GetRequiredService<BackgroundTaskRunner>();
        var importer = sp.GetRequiredService<DataImportService>();
        var docs = sp.GetRequiredService<DocumentService>();

        // Import: 3 good rows and 1 without the required subject; the mapping chosen in the wizard is kept.
        var csv = Encoding.UTF8.GetBytes("Title,Amount\nFirst,1\nSecond,2\n,3\nFourth,4\n");
        var session = importer.Open(BtAna, "BtNote", "notes.csv", csv);
        session.Main.Columns.Single(c => c.Header == "Title").Field = "subject";
        session.Main.Columns.Single(c => c.Header == "Amount").Field = "qty";
        var import = await DataImportTaskHandler.EnqueueAsync(tasks, importer, BtAna, session, csv, atomic: false);
        Assert.Equal("Import notes.csv into BtNote", import.Title, "title");
        await runner.RunNextAsync();
        var imported = await tasks.GetAsync(BtAna, import.Id);
        Assert.True(imported is { Status: BackgroundTaskStatus.Succeeded, OutputName: "import-failures-BtNote.xlsx" }
                    && imported.Result == "3 of 4 rows imported, 1 failed (see the failure report).", $"{imported.Status}: {imported.Result} {imported.Error}");
        var list = await docs.ListAsync(BtAna, "BtNote", new ListRequest(PageSize: 50));
        Assert.Equal(3, list.Total, "three notes created");
        Assert.True((await tasks.OutputAsync(BtAna, import.Id)).Content.Length > 100, "failure report produced");
        await Assert.Throws<PermissionDeniedException>(() => DataImportTaskHandler.EnqueueAsync(tasks, importer, BtAdmin with { Roles = new HashSet<string>() }, session, csv, false), "import permission checked when queuing");

        // Bulk submit: two drafts and one missing document.
        var names = list.Items.Select(i => (string)i["name"]!).Take(2).Append("BN-99999").ToList();
        var bulk = await BulkActionTaskHandler.EnqueueAsync(tasks, BtAna, new BulkActionTaskPayload("BtNote", "submit", names));
        await runner.RunNextAsync();
        var bulkDone = await tasks.GetAsync(BtAna, bulk.Id);
        Assert.True(bulkDone is { Status: BackgroundTaskStatus.Succeeded, OutputName: "submit-failures-BtNote.csv" }
                    && bulkDone.Result == "2 of 3 documents done, 1 failed (see the output file).", $"{bulkDone.Result} {bulkDone.Error}");
        Assert.True((await docs.GetAsync(BtAna, "BtNote", names[0])).DocStatus == DocStatus.Submitted, "submitted");
        Assert.True(Encoding.UTF8.GetString((await tasks.OutputAsync(BtAna, bulk.Id)).Content).StartsWith("Document,Error\nBN-99999,"), "failures listed");
        await Assert.Throws<InvalidPayloadException>(() => BulkActionTaskHandler.EnqueueAsync(tasks, BtAna, new BulkActionTaskPayload("BtNote", "archive", names)), "unknown action");
        await Assert.Throws<InvalidPayloadException>(() => BulkActionTaskHandler.EnqueueAsync(tasks, BtAna, new BulkActionTaskPayload("BtNote", "delete", [])), "no documents");
    }

    [Test]
    public async Task Background_tasks_postgres_store_claims_each_task_once()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        await using var sp = BuildTaskProvider(extra: s => s.AddFrogFrameworkPostgreSql(pg));
        await sp.MigrateFrogFrameworkDatabaseAsync();
        var store = sp.GetRequiredService<IBackgroundTaskStore>();
        Assert.True(store is PostgresBackgroundTaskStore, "PostgreSQL store registered");
        var tenant = "bt-" + Guid.NewGuid().ToString("N")[..8];
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ids = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            var t = new BackgroundTask { TenantId = tenant, UserId = "Ana", Kind = "k", Title = $"T{i}", Payload = "{\"i\":" + i + "}", Input = i == 0 ? [9, 8, 7] : null, CreatedAt = t0.AddSeconds(i) };
            await store.AddAsync(t); ids.Add(t.Id);
        }
        Assert.Equal(20, await store.CountActiveAsync(tenant, "ana"), "active count, user id case-insensitive");
        Assert.True((await store.GetWithInputAsync(ids[0]))!.Input!.SequenceEqual(new byte[] { 9, 8, 7 }) && (await store.GetAsync(tenant, ids[0]))!.Input is null, "input only when asked");

        // Concurrent claims never hand out the same task (other tenants' queued tasks may exist, so claim and filter).
        var claimed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var n = 0; n < 10; n++)
                if (await store.ClaimAsync($"w{w}", DateTimeOffset.UtcNow) is { } c && c.TenantId == tenant) claimed.Add(c.Id);
        })));
        Assert.True(claimed.Count == 20 && claimed.Distinct().Count() == 20, $"each task claimed once ({claimed.Count})");

        var first = ids[0];
        var owner = (await store.GetAsync(tenant, first))!.LockedBy!;
        Assert.True(!await store.ProgressAsync(first, owner, 40, "half", DateTimeOffset.UtcNow.AddMinutes(2)), "progress saved, no cancel");
        Assert.True(!await store.ProgressAsync(first, "someone-else", 99, null, DateTimeOffset.UtcNow), "another worker cannot report");
        Assert.True(await store.RequestCancelAsync(tenant, first, DateTimeOffset.UtcNow), "cancel requested");
        Assert.True(await store.ProgressAsync(first, owner, 50, null, DateTimeOffset.UtcNow.AddMinutes(2)), "worker learns about the cancel");
        Assert.True(await store.GetAsync(tenant, first) is { Status: BackgroundTaskStatus.Running, Progress: 50, CancelRequested: true }, "still running until it stops");
        await store.FinishAsync(first, BackgroundTaskStatus.Succeeded, "done", null, [1, 2], "out.csv", DateTimeOffset.UtcNow);
        Assert.True(await store.GetAsync(tenant, first) is { Status: BackgroundTaskStatus.Succeeded, Progress: 100, Result: "done", OutputName: "out.csv" }, "finished");
        Assert.Equal(2, (await store.OutputAsync(tenant, first))!.Value.Content.Length, "output stored");
        Assert.True(await store.OutputAsync("other-tenant", first) is null, "other tenant cannot read the output");

        var interrupted = await store.InterruptExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(19, interrupted.Count(t => t.TenantId == tenant), "the other running tasks expired");
        Assert.Equal(20, (await store.ListAsync(tenant, "ANA", 100)).Count, "list");
        Assert.True(await store.PruneAsync(DateTimeOffset.UtcNow.AddMinutes(10)) >= 20, "pruned");
        Assert.Equal(0, (await store.ListAsync(tenant, null, 100)).Count, "gone");
    }
}
