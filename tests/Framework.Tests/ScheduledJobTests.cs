using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;

sealed class StaleDocumentCleanupJob : IScheduledJob
{
    public string Name => "stale-cleanup";
    public string Schedule => "@hourly";
    public static readonly List<(string TenantId, string UserId, string DocName)> Executions = [];

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        // SystemUser should have admin permissions
        var list = await context.Docs.ListAsync(context.SystemUser, "PurchaseRequest", new ListRequest(PageSize: 10), ct);
        foreach (var doc in list.Items)
        {
            var name = doc.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
            Executions.Add((context.TenantId, context.SystemUser.Id, name));
        }
    }
}

sealed partial class Tests
{
    [Test]
    public Task CronSchedule_evaluasi_format_shorthand_dan_standar_5_field()
    {
        // 2026-11-01 15:30:00 UTC (Sunday: DayOfWeek = 0)
        var dtSunday = new DateTimeOffset(2026, 11, 1, 15, 30, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Sunday, dtSunday.DayOfWeek, "day of week Sunday");

        // Standard matches
        Assert.True(CronSchedule.IsDue("* * * * *", dtSunday), "* * * * * matches anytime");
        Assert.True(CronSchedule.IsDue("30 15 1 11 *", dtSunday), "exact date and time matches");
        Assert.True(CronSchedule.IsDue("*/15 * * * *", dtSunday), "minute step 15 matches 30");
        Assert.True(!CronSchedule.IsDue("*/20 * * * *", dtSunday), "minute step 20 does not match 30");
        Assert.True(CronSchedule.IsDue("0,15,30,45 * * * *", dtSunday), "minute list matches");
        Assert.True(CronSchedule.IsDue("* * * * 0", dtSunday), "Sunday as 0 matches");
        Assert.True(CronSchedule.IsDue("* * * * 7", dtSunday), "Sunday as 7 matches");

        // Shorthands
        Assert.True(CronSchedule.IsDue("@every_minute", dtSunday), "@every_minute matches");
        Assert.True(CronSchedule.IsDue("@every_15m", dtSunday), "@every_15m matches 30");
        Assert.True(CronSchedule.IsDue("@every_30m", dtSunday), "@every_30m matches 30");

        // Hourly: "0 * * * *" -> at minute 30 should NOT be due
        Assert.True(!CronSchedule.IsDue("@hourly", dtSunday), "@hourly not due at minute 30");
        var dtTopOfHour = new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);
        Assert.True(CronSchedule.IsDue("@hourly", dtTopOfHour), "@hourly due at minute 0");

        // Dedup within same minute
        var sameMinuteLater = dtSunday.AddSeconds(20);
        Assert.True(!CronSchedule.IsDue("* * * * *", sameMinuteLater, lastRun: dtSunday), "same minute is not due again");
        var nextMinute = dtSunday.AddMinutes(1);
        Assert.True(CronSchedule.IsDue("* * * * *", nextMinute, lastRun: dtSunday), "next minute is due");

        return Task.CompletedTask;
    }

    [Test]
    public async Task DistributedLock_mencegah_eksekusi_bersamaan_pada_key_yang_sama()
    {
        var distLock = new InMemoryDistributedLock();
        const string key = "job:sample:tenant-1";

        var lock1 = await distLock.TryAcquireAsync(key, TimeSpan.Zero);
        Assert.True(lock1 is not null, "lock 1 berhasil diakuisisi");

        var lock2 = await distLock.TryAcquireAsync(key, TimeSpan.Zero);
        Assert.True(lock2 is null, "lock 2 gagal diakuisisi selama lock 1 aktif");

        // Setelah lock 1 dilepas, lock berikutnya dapat diperoleh
        lock1?.Dispose();

        var lock3 = await distLock.TryAcquireAsync(key, TimeSpan.Zero);
        Assert.True(lock3 is not null, "lock 3 berhasil setelah lock 1 didispose");
        lock3?.Dispose();
    }

    [Test]
    public async Task ScheduledJobRunner_menjalankan_job_dengan_UserContext_System_dan_multi_tenant()
    {
        StaleDocumentCleanupJob.Executions.Clear();

        await UseHookProviderAsync(s =>
        {
            s.AddScheduledJob<StaleDocumentCleanupJob>();
            s.AddSingleton<ITenantProvider>(new DefaultTenantProvider(["acme", "subsidiary"]));
        });

        // Insert a test document in tenant acme
        var prDoc = await Docs.InsertAsync(U("andi"), PR, P(("items", Items(1, 1000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11")));

        var runner = sp.GetRequiredService<ScheduledJobRunner>();
        var topOfHour = new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero);

        var result = await runner.RunDueJobsAsync(topOfHour);

        // 1 job x 2 tenants = 2 evaluated, 2 executed
        Assert.Equal(2, result.Evaluated, "2 tenants evaluated");
        Assert.Equal(2, result.Executed, "2 tenants executed");
        Assert.Equal(0, result.SkippedLocked, "0 locked");
        Assert.Equal(0, result.Failed, "0 failed");

        // Verify execution audit & system user
        Assert.True(StaleDocumentCleanupJob.Executions.Any(e => e.TenantId == "acme" && e.UserId == "system" && e.DocName == prDoc.Name),
            "Job dieksekusi dengan akun system dan membaca dokumen tenant acme");

        // Running again within the same minute should skip
        var sameMinuteResult = await runner.RunDueJobsAsync(topOfHour.AddSeconds(15));
        Assert.Equal(0, sameMinuteResult.Executed, "tidak berjalan ganda di menit yang sama");

        // If lock is held externally, job is skipped
        var nextHour = topOfHour.AddHours(1);
        var distLock = sp.GetRequiredService<IDistributedLock>();
        using (await distLock.TryAcquireAsync("frogframe:scheduler:stale-cleanup:acme", TimeSpan.Zero))
        {
            var lockedResult = await runner.RunDueJobsAsync(nextHour);
            Assert.Equal(1, lockedResult.SkippedLocked, "acme dilewati karena lock sedang aktif");
            Assert.Equal(1, lockedResult.Executed, "subsidiary tetap dieksekusi");
        }
    }

    [Test, PgTest]
    public async Task Pg_PostgresDistributedLock_mencegah_eksekusi_bersamaan_lintas_koneksi()
    {
        var db = sp.GetRequiredService<Npgsql.NpgsqlDataSource>();
        var lock1 = new Framework.Persistence.PostgreSql.PostgresDistributedLock(db);
        var lock2 = new Framework.Persistence.PostgreSql.PostgresDistributedLock(db);
        const string key = "job:pg-lock-test";

        var handle1 = await lock1.TryAcquireAsync(key, TimeSpan.Zero);
        Assert.True(handle1 is not null, "lock 1 diakuisisi");

        var handle2 = await lock2.TryAcquireAsync(key, TimeSpan.Zero);
        Assert.True(handle2 is null, "lock 2 ditolak saat lock 1 aktif");

        handle1?.Dispose();

        var handle3 = await lock2.TryAcquireAsync(key, TimeSpan.Zero);
        Assert.True(handle3 is not null, "lock 2 dapat diakuisisi setelah lock 1 dilepas");
        handle3?.Dispose();
    }

    [Test, PgTest]
    public async Task Pg_PostgresTenantProvider_dan_ScheduledJobLogStore_persisten_di_database()
    {
        var db = sp.GetRequiredService<Npgsql.NpgsqlDataSource>();
        var provider = new Framework.Persistence.PostgreSql.PostgresTenantProvider(db, defaultTenant: "acme");
        var tenants = await provider.GetActiveTenantsAsync();
        Assert.True(tenants.Count > 0, "active tenants ditemukan");
        Assert.True(tenants.Contains("acme"), "tenant acme ditemukan");

        var store = new Framework.Persistence.PostgreSql.PostgresScheduledJobLogStore(db);
        var now = DateTimeOffset.UtcNow;
        var entry = new ScheduledJobRunEntry
        {
            JobName = "test-job-audit",
            TenantId = "acme",
            StartedAt = now,
            CompletedAt = now.AddSeconds(2),
            DurationMs = 2000,
            Status = "Success",
            Error = null
        };

        await store.RecordRunAsync(entry);

        var lastRun = await store.GetLastRunAsync("test-job-audit", "acme");
        Assert.True(lastRun.HasValue, "last run berhasil dicatat");
        Assert.True(Math.Abs((lastRun!.Value - now).TotalSeconds) < 2, "timestamp last run akurat");
    }
}

