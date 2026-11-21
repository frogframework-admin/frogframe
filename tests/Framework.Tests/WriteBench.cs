using System.Collections.Concurrent;
using System.Diagnostics;
using Apps.Erp;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Web;
using Framework.Web.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// Write benchmark on PostgreSQL: many concurrent users create Purchase Requests (with an item row) through the real
/// pipeline: <c>DocumentService</c>, ERP metadata, hooks (approver resolution), naming series, audit and outbox. The
/// outbox processor runs at the same time, as in the application. Reports latency percentiles and throughput.
/// HTTP, authentication and JSON are not included; compare with the k6 test (tests/load) for end-to-end numbers.
///   FROGFRAME_TEST_PG=... dotnet run --project tests/Framework.Tests -- --bench-write [workers=100] [perWorker=20]
/// The database in FROGFRAME_TEST_PG is used to create a fresh database for the run, which is dropped afterwards.
/// </summary>
public static class WriteBench
{
    public static async Task<int> RunAsync(string[] args)
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) { Console.WriteLine("The write benchmark needs FROGFRAME_TEST_PG."); return 2; }
        var workers = args.Length > 1 && int.TryParse(args[1], out var w) ? w : 100;
        var perWorker = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 20;
        var result = await Tests.RunWriteBenchAsync(pg, workers, perWorker, Console.Out);
        return result.Errors == 0 ? 0 : 1;
    }
}

public sealed record WriteBenchResult(int Inserts, int Errors, double P50, double P95, double P99, double Max, double PerSecond);

sealed partial class Tests
{
    internal static async Task<WriteBenchResult> RunWriteBenchAsync(string pg, int workers, int perWorker, TextWriter output)
    {
        var dbName = "ff_wbench_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlConnection(pg))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {dbName}", admin).ExecuteNonQueryAsync();
        }
        try
        {
            var conn = new NpgsqlConnectionStringBuilder(pg) { Database = dbName }.ConnectionString;
            var erpMetadataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Apps.Erp", "Metadata"));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
            services.AddFrogFramework(o => o.LoadFrom(erpMetadataPath));
            services.AddFrogFrameworkIdentity();
            services.AddErpHooks();
            services.AddFrogFrameworkPostgreSql(conn);
            await using var provider = services.BuildServiceProvider();
            await provider.MigrateFrogFrameworkDatabaseAsync();
            var (docs, _, _) = await MatrixSetupAsync(provider);
            var staff = MatrixUser("STAFF-A");
            Dictionary<string, object?> Payload(int i) => new()
            {
                ["requested_by"] = "STAFF-A", ["required_by"] = D.Iso(MatrixToday.AddDays(30)), ["company"] = "ACME",
                ["purpose"] = $"Load test request {i}",
                ["items"] = new List<Dictionary<string, object?>> { new() { ["item_name"] = "Standard Developer Laptop", ["qty"] = 1m, ["estimated_rate"] = 15_000_000m } },
            };
            for (var i = 0; i < 20; i++) await docs.InsertAsync(staff, "PurchaseRequest", Payload(-i)); // warm-up (JIT, pool, plans)

            // The outbox processor drains events while users write, woken by each commit, as the hosted service does.
            var processor = new OutboxProcessor(provider.GetRequiredService<IOutboxStore>(), provider.GetServices<IEventHandler>(), TimeProvider.System);
            using var stop = new CancellationTokenSource();
            var outboxLoop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try { if ((await processor.ProcessDueAsync(stop.Token)).Processed == 0) await Task.Delay(50, stop.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });

            var latencies = new ConcurrentBag<double>();
            var errors = new ConcurrentBag<string>();
            var gate = new TaskCompletionSource();
            var total = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, workers).Select(wi => Task.Run(async () =>
            {
                await gate.Task;
                for (var j = 0; j < perWorker; j++)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await docs.InsertAsync(staff, "PurchaseRequest", Payload(wi * perWorker + j));
                        latencies.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); }
                }
            })).ToList();
            total.Restart();
            gate.SetResult(); // all workers start at the same moment: a burst, like the k6 spike
            await Task.WhenAll(tasks);
            var elapsed = total.Elapsed.TotalSeconds;
            stop.Cancel();
            await outboxLoop;

            var sorted = latencies.Order().ToArray();
            double Pct(double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
            var result = new WriteBenchResult(sorted.Length, errors.Count, Pct(0.50), Pct(0.95), Pct(0.99), sorted.LastOrDefault(), sorted.Length / elapsed);
            output.WriteLine($"Write benchmark: {workers} concurrent users x {perWorker} Purchase Requests = {sorted.Length} inserts, {errors.Count} errors, {elapsed:N1} s");
            output.WriteLine($"  p50 {result.P50:N1} ms · p95 {result.P95:N1} ms · p99 {result.P99:N1} ms · max {result.Max:N1} ms · {result.PerSecond:N0} inserts/s");
            foreach (var e in errors.GroupBy(e => e).Take(5)) output.WriteLine($"  error x{e.Count()}: {e.Key}");
            return result;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(pg);
            await admin.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", admin).ExecuteNonQueryAsync();
        }
    }
}
