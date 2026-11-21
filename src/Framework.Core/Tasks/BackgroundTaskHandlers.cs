using System.Globalization;
using System.Text;
using System.Text.Json;
using Framework.Documents;
using Framework.Imports;
using Framework.Security;

namespace Framework.Tasks;

/// <summary>Parameters of a background data import: the file is the task's input.</summary>
/// <param name="Mappings">Field per file column, per sheet: key "" = main sheet, otherwise the Table field name.</param>
public sealed record DataImportTaskPayload(string DocType, string FileName, bool Atomic, Dictionary<string, List<string?>> Mappings);

/// <summary>
/// Data import in the background (kind "data-import"): reads the uploaded file again, applies the column mapping chosen
/// in the import wizard, and imports it with the user's current permissions. Progress is the share of rows done.
/// The result says how many rows were created; when rows failed, the failure report is the task's output file.
/// </summary>
public sealed class DataImportTaskHandler(DataImportService importer) : IBackgroundTaskHandler
{
    public const string TaskKind = "data-import";
    public string Kind => TaskKind;

    /// <summary>Queues an import of the session's file with its current column mapping.</summary>
    public static Task<BackgroundTask> EnqueueAsync(BackgroundTaskService tasks, DataImportService importer, UserContext user,
        ImportSession session, byte[] file, bool atomic, CancellationToken ct = default)
    {
        if (!importer.CanImport(user, session.DocType)) throw new PermissionDeniedException($"You cannot import {session.DocType}.");
        var mappings = new Dictionary<string, List<string?>> { [""] = session.Main.Columns.OrderBy(c => c.Index).Select(c => c.Field).ToList() };
        foreach (var (table, sheet) in session.Children) mappings[table] = sheet.Columns.OrderBy(c => c.Index).Select(c => c.Field).ToList();
        var payload = JsonSerializer.Serialize(new DataImportTaskPayload(session.DocType, session.FileName, atomic, mappings));
        return tasks.EnqueueAsync(user, TaskKind, $"Import {session.FileName} into {session.DocType}", payload, file, ct);
    }

    public async Task<BackgroundTaskOutcome> RunAsync(BackgroundTaskContext context, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<DataImportTaskPayload>(context.Task.Payload) ?? throw new InvalidPayloadException("Invalid import parameters.");
        var file = context.Task.Input ?? throw new InvalidPayloadException("The import file is missing.");
        var session = importer.Open(context.User, p.DocType, p.FileName, file);
        Apply(session.Main, p.Mappings.GetValueOrDefault(""));
        foreach (var (table, sheet) in session.Children) Apply(sheet, p.Mappings.GetValueOrDefault(table));

        var total = Math.Max(1, session.RowCount);
        await context.ReportAsync(0, $"0 of {total:N0} rows", ct);
        // The importer reports synchronously; a cancel thrown from the report stops the import loop.
        var progress = new SyncProgress(i => context.ReportAsync(i * 100 / total, $"{i:N0} of {total:N0} rows", ct).GetAwaiter().GetResult());
        var run = await importer.ExecuteAsync(context.User, session, p.Atomic, progress, ct);
        var summary = $"{run.Log.Succeeded:N0} of {run.Log.Total:N0} rows imported" + (run.Log.Failed > 0 ? $", {run.Log.Failed:N0} failed (see the failure report)." : ".");
        return run.Log.Failed > 0
            ? new BackgroundTaskOutcome(summary, DataImportService.FailureReport(session, run.Rows), $"import-failures-{p.DocType}.xlsx")
            : new BackgroundTaskOutcome(summary);
    }

    private static void Apply(ImportSheet sheet, List<string?>? fields)
    {
        if (fields is null) return;
        foreach (var c in sheet.Columns) c.Field = c.Index < fields.Count ? fields[c.Index] : null;
    }

    /// <summary>Runs the callback on the caller's thread (Progress&lt;T&gt; would post it elsewhere and lose exceptions).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}

/// <summary>Parameters of a background bulk action on documents.</summary>
/// <param name="Action">"submit", "cancel" or "delete".</param>
public sealed record BulkActionTaskPayload(string DocType, string Action, List<string> Names, string? Comment = null);

/// <summary>
/// Submit, cancel or delete many documents in the background (kind "bulk-action"), one document at a time with the
/// same checks as from the list, so one failure does not stop the others. The output lists the failures (CSV).
/// </summary>
public sealed class BulkActionTaskHandler(DocumentService docs) : IBackgroundTaskHandler
{
    public const string TaskKind = "bulk-action";
    public const int MaxDocuments = 50_000;
    public string Kind => TaskKind;

    public static Task<BackgroundTask> EnqueueAsync(BackgroundTaskService tasks, UserContext user, BulkActionTaskPayload payload, CancellationToken ct = default)
    {
        if (payload.Action is not ("submit" or "cancel" or "delete")) throw new InvalidPayloadException($"Unknown bulk action '{payload.Action}'.");
        var names = payload.Names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) throw new InvalidPayloadException("Choose at least one document.");
        if (names.Count > MaxDocuments) throw new InvalidPayloadException($"At most {MaxDocuments:N0} documents per task.");
        var verb = payload.Action switch { "submit" => "Submit", "cancel" => "Cancel", _ => "Delete" };
        return tasks.EnqueueAsync(user, TaskKind, $"{verb} {names.Count:N0} {payload.DocType}", JsonSerializer.Serialize(payload with { Names = names }), null, ct);
    }

    public async Task<BackgroundTaskOutcome> RunAsync(BackgroundTaskContext context, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<BulkActionTaskPayload>(context.Task.Payload) ?? throw new InvalidPayloadException("Invalid parameters.");
        var failures = new List<BulkActionFailure>();
        var done = 0;
        foreach (var name in p.Names)
        {
            ct.ThrowIfCancellationRequested();
            BulkActionResult r = p.Action switch
            {
                "submit" => await docs.BulkSubmitAsync(context.User, p.DocType, [name], p.Comment, ct),
                "cancel" => await docs.BulkCancelAsync(context.User, p.DocType, [name], p.Comment, ct),
                "delete" => await docs.BulkDeleteAsync(context.User, p.DocType, [name], ct),
                _ => throw new InvalidPayloadException($"Unknown bulk action '{p.Action}'."),
            };
            failures.AddRange(r.Failed);
            done++;
            await context.ReportAsync(done * 100 / p.Names.Count, $"{done:N0} of {p.Names.Count:N0} documents", ct);
        }
        var ok = p.Names.Count - failures.Count;
        var summary = $"{ok:N0} of {p.Names.Count:N0} documents done" + (failures.Count > 0 ? $", {failures.Count:N0} failed (see the output file)." : ".");
        if (failures.Count == 0) return new BackgroundTaskOutcome(summary);
        var csv = new StringBuilder("Document,Error\n");
        foreach (var f in failures) csv.Append(Csv(f.Name)).Append(',').Append(Csv(f.Error)).Append('\n');
        return new BackgroundTaskOutcome(summary, Encoding.UTF8.GetBytes(csv.ToString()), $"{p.Action}-failures-{p.DocType}.csv");
    }

    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
