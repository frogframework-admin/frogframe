using Framework.Documents;
using Framework.Metadata;
using Framework.Observability;
using Framework.Persistence;
using Framework.Security;

namespace Framework.Jobs;

public interface IEventHandler
{
    bool CanHandle(string eventType);
    /// <summary>Harus idempoten: pesan yang sama dapat dikirim ulang setelah kegagalan.</summary>
    Task HandleAsync(OutboxMessage message, CancellationToken ct);
}

public sealed record OutboxRunResult(int Processed, int Failed, int DeadLettered);

/// <summary>
/// Memproses outbox setelah commit: retry dengan backoff eksponensial, dead-letter setelah MaxAttempts.
/// Dijalankan oleh hosted service pada aplikasi web; dapat diganti worker terpisah/broker.
/// </summary>
public sealed class OutboxProcessor(IOutboxStore outbox, IEnumerable<IEventHandler> handlers, TimeProvider clock)
{
    /// <summary>Retensi: hapus pesan yang sudah diproses lebih lama dari <paramref name="retention"/>, per batch.</summary>
    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow() - retention;
        var total = 0;
        int n;
        do { n = await outbox.PruneAsync(cutoff, 5_000, ct); total += n; } while (n == 5_000 && !ct.IsCancellationRequested);
        return total;
    }

    public const int MaxAttempts = 5;

    public async Task<OutboxRunResult> ProcessDueAsync(CancellationToken ct = default)
    {
        int ok = 0, failed = 0, dead = 0;
        foreach (var msg in await outbox.FetchDueAsync(clock.GetUtcNow(), 50, ct))
        {
            using var activity = DocFrameTelemetry.ActivitySource.StartActivity("docframe.outbox.process");
            activity?.SetTag("docframe.event_type", msg.Type);
            activity?.SetTag("docframe.event_id", msg.Id);
            activity?.SetTag("docframe.tenant", msg.TenantId);

            try
            {
                foreach (var h in handlers.Where(h => h.CanHandle(msg.Type)))
                    await h.HandleAsync(msg, ct);
                msg.ProcessedAt = clock.GetUtcNow();
                ok++;
                DocFrameTelemetry.OutboxEventsProcessed.Add(1, new KeyValuePair<string, object?>("type", msg.Type));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                msg.Attempts++;
                msg.LastError = ex.Message;
                DocFrameTelemetry.OutboxEventsFailed.Add(1, new KeyValuePair<string, object?>("type", msg.Type));
                if (msg.Attempts >= MaxAttempts) { msg.DeadLettered = true; dead++; }
                else { msg.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Pow(2, msg.Attempts)); failed++; }
            }
            await outbox.UpdateAsync(msg, ct);
        }
        return new OutboxRunResult(ok, failed, dead);
    }
}

// ---------- Reporting (RFC §13: provider C# terdaftar, bukan SQL bebas dari metadata) ----------

public sealed record ReportColumn(string Key, string Label, string Type = "Data");
public sealed record ReportFilterDef(string Key, string Label, string Type, IReadOnlyList<string>? Options = null);
public sealed record ReportResult(IReadOnlyList<ReportColumn> Columns, IReadOnlyList<Dictionary<string, object?>> Rows);

public interface IReportProvider
{
    string Name { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>DocType sumber; permission 'report'/'export' dan row scope diambil dari sini.</summary>
    string DocType { get; }
    IReadOnlyList<ReportFilterDef> Filters { get; }
    Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> filters, DocumentService documents, CancellationToken ct);
}

public sealed class ReportService(IEnumerable<IReportProvider> providers, DocumentService documents, IMetadataProvider metadata, IPermissionService permissions)
{
    public IEnumerable<IReportProvider> AvailableFor(UserContext user) =>
        providers.Where(p => metadata.Current.TryGet(p.DocType, out var m) && permissions.Can(user, m, DocActions.Report));

    public IReportProvider Get(string name) =>
        providers.FirstOrDefault(p => p.Name == name) ?? throw new DocumentNotFoundException("Report", name);

    public Task<ReportResult> RunAsync(UserContext user, string name, IReadOnlyDictionary<string, string?> filters, CancellationToken ct = default)
    {
        var p = Get(name);
        permissions.Demand(user, metadata.Current.Get(p.DocType), DocActions.Report);
        return p.RunAsync(user, filters, documents, ct);
    }

    public async Task<string> ExportCsvAsync(UserContext user, string name, IReadOnlyDictionary<string, string?> filters, CancellationToken ct = default)
    {
        var p = Get(name);
        permissions.Demand(user, metadata.Current.Get(p.DocType), DocActions.Export);
        var r = await RunAsync(user, name, filters, ct);
        static string Esc(object? v)
        {
            var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; // cegah CSV/formula injection
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(',', r.Columns.Select(c => Esc(c.Label))));
        foreach (var row in r.Rows) sb.AppendLine(string.Join(',', r.Columns.Select(c => Esc(row.GetValueOrDefault(c.Key)))));
        return sb.ToString();
    }
}
