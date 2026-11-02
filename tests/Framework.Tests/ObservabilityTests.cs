using System.Diagnostics;
using Framework.Documents;
using Framework.Jobs;
using Framework.Observability;
using Framework.Persistence.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

sealed class ObservabilityTestJob : IScheduledJob
{
    public string Name => "observability-test-job";
    public string Schedule => "@hourly";
    public static int ExecutionCount;

    public Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref ExecutionCount);
        return Task.CompletedTask;
    }
}

sealed partial class Tests
{
    [Test]
    public async Task ErrorLog_InMemoryStore_Lifecycle()
    {
        var store = new InMemoryErrorLogStore();
        var now = DateTimeOffset.UtcNow;

        var e1 = new ErrorLogEntry
        {
            TenantId = "acme",
            Timestamp = now.AddMinutes(-10),
            ErrorType = "NullReferenceException",
            Message = "Object reference not set",
            StackTrace = "at Foo.Bar()",
            Path = "/api/v1/resource",
            Method = "GET",
            UserId = "user-1"
        };

        var e2 = new ErrorLogEntry
        {
            TenantId = "acme",
            Timestamp = now,
            ErrorType = "ArgumentException",
            Message = "Invalid parameter",
            StackTrace = "at Foo.Baz()",
            Path = "/api/v1/update",
            Method = "POST",
            UserId = "user-2"
        };

        await store.LogAsync(e1);
        await store.LogAsync(e2);

        var recent = await store.GetRecentAsync("acme", limit: 10);
        Assert.Equal(2, recent.Count, "Error log should have 2 entries");
        Assert.Equal("ArgumentException", recent[0].ErrorType, "Most recent entry should come first");
        Assert.True(!recent[0].Seen, "New error should not be marked seen");

        // Limit works
        var one = await store.GetRecentAsync("acme", limit: 1);
        Assert.Equal(1, one.Count, "Limit parameter should be respected");

        // Mark seen with wrong tenant does not mark seen
        await store.MarkSeenAsync(recent[0].Id, "other_tenant");
        var unchanged = await store.GetRecentAsync("acme", limit: 10);
        Assert.True(!unchanged.First(x => x.Id == recent[0].Id).Seen, "Entry should not be marked seen for wrong tenant");

        // Mark seen with matching tenant
        await store.MarkSeenAsync(recent[0].Id, "acme");
        var afterSeen = await store.GetRecentAsync("acme", limit: 10);
        Assert.True(afterSeen.First(x => x.Id == recent[0].Id).Seen, "Entry should be marked as seen");

        // Clear
        await store.ClearAsync("acme");
        var afterClear = await store.GetRecentAsync("acme", limit: 10);
        Assert.Equal(0, afterClear.Count, "Error log should be cleared");
    }

    [Test, PgTest]
    public async Task Pg_ErrorLog_PostgresStore_Lifecycle()
    {
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var store = new PostgresErrorLogStore(db);
        var tenantId = "acme_obs_" + Guid.NewGuid().ToString("N")[..8];

        var entry = new ErrorLogEntry
        {
            TenantId = tenantId,
            Timestamp = DateTimeOffset.UtcNow,
            ErrorType = "InvalidOperationException",
            Message = "Something went wrong in postgres test",
            StackTrace = "at Service.Execute() line 42",
            Path = "/api/test",
            Method = "POST",
            UserId = "admin@example.com"
        };

        await store.LogAsync(entry);

        var list = await store.GetRecentAsync(tenantId, limit: 10);
        Assert.Equal(1, list.Count, "PostgreSQL error log should store and retrieve entry");
        var saved = list[0];
        Assert.Equal("InvalidOperationException", saved.ErrorType, "Error type matches");
        Assert.Equal("Something went wrong in postgres test", saved.Message, "Message matches");
        Assert.Equal("at Service.Execute() line 42", saved.StackTrace, "Stack trace matches");
        Assert.Equal("/api/test", saved.Path, "Path matches");
        Assert.Equal("POST", saved.Method, "HTTP method matches");
        Assert.Equal("admin@example.com", saved.UserId, "User matches");
        Assert.True(!saved.Seen, "Should not be seen initially");

        // Mark seen with wrong tenant does not update
        await store.MarkSeenAsync(saved.Id, "other_tenant");
        var unupdatedList = await store.GetRecentAsync(tenantId, limit: 10);
        Assert.True(!unupdatedList[0].Seen, "Should still not be seen when tenant does not match");

        // Mark seen with matching tenant
        await store.MarkSeenAsync(saved.Id, tenantId);
        var seenList = await store.GetRecentAsync(tenantId, limit: 10);
        Assert.True(seenList[0].Seen, "Should be marked seen");

        // Clear
        await store.ClearAsync(tenantId);
        var emptyList = await store.GetRecentAsync(tenantId, limit: 10);
        Assert.Equal(0, emptyList.Count, "Error log should be empty after clear");
    }

    [Test, PgTest]
    public async Task Pg_ScheduledJob_RecentRunsAndRunNow()
    {
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var logStore = new PostgresScheduledJobLogStore(db);
        var tenantId = "acme";

        var testJob = new ObservabilityTestJob();
        var initialExecutions = ObservabilityTestJob.ExecutionCount;

        var runner = new ScheduledJobRunner(
            jobs: [testJob],
            tenantProvider: new PostgresTenantProvider(db),
            distributedLock: new PostgresDistributedLock(db),
            documentService: sp.GetRequiredService<DocumentService>(),
            serviceProvider: sp,
            logStore: logStore
        );

        // Run job on demand
        var runResult = await runner.RunJobNowAsync(testJob.Name, tenantId);
        Assert.Equal("Success", runResult.Status, "RunJobNowAsync should succeed with Status 'Success'");
        Assert.Equal(initialExecutions + 1, ObservabilityTestJob.ExecutionCount, "Job execution count should increment");

        // Check recent runs
        var recentRuns = await logStore.GetRecentRunsAsync(tenantId, limit: 5);
        Assert.True(recentRuns.Count > 0, "Recent job runs should have at least 1 entry");
        var lastRun = recentRuns.FirstOrDefault(r => r.JobName == testJob.Name);
        Assert.True(lastRun is not null, "Last run should contain the tested job");
        Assert.Equal("Success", lastRun!.Status, "Last run status should be Success");
    }

    [Test]
    public Task DocFrameTelemetry_MetersAndActivities_RecordWithoutExceptions()
    {
        Assert.Equal("DocFrame", DocFrameTelemetry.SourceName, "Source name should be DocFrame");
        Assert.Equal("1.0.0", DocFrameTelemetry.Version, "Version should be 1.0.0");

        // Record metrics
        DocFrameTelemetry.HttpErrors.Add(1, new KeyValuePair<string, object?>("error.type", "NullReferenceException"));
        DocFrameTelemetry.DocumentOperations.Add(1,
            new KeyValuePair<string, object?>("docframe.doctype", "User"),
            new KeyValuePair<string, object?>("docframe.action", "create"));
        DocFrameTelemetry.OutboxEventsProcessed.Add(1);
        DocFrameTelemetry.OutboxEventsFailed.Add(1);
        DocFrameTelemetry.ScheduledJobRuns.Add(1,
            new KeyValuePair<string, object?>("docframe.job_name", "observability-test-job"),
            new KeyValuePair<string, object?>("docframe.status", "Success"));

        // Activity source listener verification
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DocFrameTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => activities.Add(a)
        };
        ActivitySource.AddActivityListener(listener);

        using (var span = DocFrameTelemetry.ActivitySource.StartActivity("docframe.test.operation"))
        {
            span?.SetTag("docframe.tag", "sample");
        }

        Assert.True(activities.Any(a => a.OperationName == "docframe.test.operation"), "ActivitySource produced test activity");

        return Task.CompletedTask;
    }
}
