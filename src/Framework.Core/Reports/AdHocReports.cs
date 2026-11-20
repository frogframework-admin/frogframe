using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Framework.Documents;
using Framework.Expressions;
using Framework.Imports;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Framework.Reports;

/// <summary>Filter of an ad-hoc report. <see cref="Value"/> is text as typed; "in" takes a comma-separated list.</summary>
public sealed record AdHocFilter(string Field, string Op, string? Value);

/// <summary>Aggregate column: count (field optional), sum, avg, min or max of a field.</summary>
public sealed record AdHocAggregate(string Function, string? Field);

/// <summary>What an ad-hoc report shows. Plain data: saved as JSON and run with the viewer's own permissions.</summary>
public sealed record AdHocReportDef
{
    public string DocType { get; init; } = "";
    /// <summary>Detail columns (when <see cref="GroupBy"/> is empty).</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<AdHocFilter> Filters { get; init; } = [];
    /// <summary>Group-by fields; with them the report shows one row per group with <see cref="Aggregates"/>.</summary>
    public IReadOnlyList<string> GroupBy { get; init; } = [];
    public IReadOnlyList<AdHocAggregate> Aggregates { get; init; } = [];
    /// <summary>Output column key to sort by (a field, or an aggregate key such as "sum:amount").</summary>
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }
    public int Limit { get; init; } = 500;
}

public sealed record AdHocColumn(string Key, string Label, string Type);

/// <summary>Report output. <see cref="Truncated"/>: the source query hit the row cap, so totals may be incomplete.</summary>
public sealed record AdHocResult(
    IReadOnlyList<AdHocColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    int SourceRows,
    bool Truncated,
    bool LimitApplied,
    IReadOnlyList<string> SkippedFields);

/// <summary>A named, saved ad-hoc report. Shared with roles, it still runs with each viewer's own permissions.</summary>
public sealed record SavedAdHocReport
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Owner { get; init; } = "";
    public IReadOnlyList<string> SharedWithRoles { get; init; } = [];
    public AdHocReportDef Definition { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public int Version { get; init; } = 1;
}

/// <summary>Storage of saved ad-hoc reports (table <c>frogframe_adhoc_reports</c> in PostgreSQL).</summary>
public interface IAdHocReportStore
{
    Task<IReadOnlyList<SavedAdHocReport>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<SavedAdHocReport?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    /// <summary>Insert (version 1) or update with optimistic concurrency on <paramref name="expectedVersion"/>.</summary>
    Task SaveAsync(SavedAdHocReport report, int? expectedVersion, CancellationToken ct = default);
    Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default);
}

public sealed class InMemoryAdHocReportStore : IAdHocReportStore
{
    private readonly ConcurrentDictionary<(string, Guid), SavedAdHocReport> reports = new();
    private readonly object gate = new();

    public Task<IReadOnlyList<SavedAdHocReport>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SavedAdHocReport>>(reports.Values.Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList());

    public Task<SavedAdHocReport?> GetAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(reports.GetValueOrDefault((tenantId, id)));

    public Task SaveAsync(SavedAdHocReport report, int? expectedVersion, CancellationToken ct = default)
    {
        lock (gate)
        {
            var current = reports.GetValueOrDefault((report.TenantId, report.Id));
            if (expectedVersion is null ? current is not null : current?.Version != expectedVersion)
                throw new ConcurrencyException(report.Name, expectedVersion ?? 0, current?.Version ?? 0);
            reports[(report.TenantId, report.Id)] = report;
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(reports.TryRemove((tenantId, id), out _));
}

public static class AdHocLimits
{
    public const int MaxRows = 10_000;
    public const int MaxColumns = 30;
    public const int MaxFilters = 20;
    public const int MaxGroupBy = 3;
    public const int MaxAggregates = 10;
    public const int MaxNameLength = 120;
    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.Ordinal) { "count", "sum", "avg", "min", "max" };
    public static readonly IReadOnlySet<string> NumericTypes = new HashSet<string>(StringComparer.Ordinal) { "Int", "Float", "Currency", "Percent" };
}

/// <summary>
/// Self-service reports: pick a DocType, columns, filters, group-by and aggregates, then run, export or save.
/// Data comes from <see cref="DocumentService.QueryForReportAsync"/> with the report permission, so tenant, row
/// scope (including row sharing) and the source query cap are the same as code reports. Fields the user cannot
/// read cannot be shown, filtered, grouped or aggregated. A saved report shared with roles is a definition only:
/// every viewer runs it with their own permissions, and columns they cannot read are left out.
/// </summary>
public sealed class AdHocReportService(
    DocumentService documents,
    IMetadataProvider metadata,
    IPermissionService permissions,
    IAdHocReportStore store,
    TimeProvider clock)
{
    private static readonly string[] StandardColumns =
        [StandardFields.Name, StandardFields.Owner, StandardFields.DocStatus, StandardFields.WorkflowState, StandardFields.Created, StandardFields.Modified];

    /// <summary>DocTypes the user may report on.</summary>
    public IReadOnlyList<CompiledDocType> DocTypes(UserContext user) =>
        metadata.Current.DocTypes.Values
            .Where(m => !m.Def.IsChild && !m.Def.IsSingle && permissions.Can(user, m, DocActions.Report))
            .OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Columns the user may use for this DocType: standard fields, then readable value fields in form order.</summary>
    public IReadOnlyList<AdHocColumn> Fields(UserContext user, string docType)
    {
        var meta = Meta(user, docType);
        var list = StandardColumns.Select(f => new AdHocColumn(f, StandardLabel(f), f switch { StandardFields.Created or StandardFields.Modified => "Datetime", StandardFields.DocStatus => "Int", _ => "Data" })).ToList();
        list.AddRange(meta.Fields
            .Where(f => !f.IsLayout && FieldKinds.IsReported(f.Def.Type) && FieldKinds.IsQueryable(f.Def.Type) && permissions.CanReadField(user, meta, f))
            .Select(f => new AdHocColumn(f.Name, f.Label, f.Def.Type)));
        return list;
    }

    public async Task<AdHocResult> RunAsync(UserContext user, AdHocReportDef def, CancellationToken ct = default)
    {
        var plan = Plan(user, def, strict: true);
        return await ExecuteAsync(user, plan, ct);
    }

    /// <summary>Runs a saved report as <paramref name="user"/>; columns they cannot read are skipped, not failed.</summary>
    public async Task<(SavedAdHocReport Report, AdHocResult Result)> RunSavedAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        var saved = await GetAsync(user, id, ct);
        var plan = Plan(user, saved.Definition, strict: false);
        return (saved, await ExecuteAsync(user, plan, ct));
    }

    public async Task<ReportOutput> ExportAsync(UserContext user, AdHocReportDef def, string format, string title, CancellationToken ct = default)
    {
        var meta = Meta(user, def.DocType);
        permissions.Demand(user, meta, DocActions.Export);
        var result = await RunAsync(user, def, ct);
        var safeTitle = new string((string.IsNullOrWhiteSpace(title) ? meta.Name : title).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (safeTitle.Length == 0) safeTitle = "report";
        var headers = result.Columns.Select(c => c.Label).ToList();
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Csv)));
            foreach (var r in result.Rows) sb.AppendLine(string.Join(",", r.Select(v => Csv(Text(v)))));
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return new ReportOutput(bytes, safeTitle + ".csv", "text/csv", result.Rows.Count);
        }
        if (!string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidPayloadException($"Export format '{format}' is not supported. Use 'xlsx' or 'csv'.", ["format"]);
        var sheet = new XlsxSheet
        {
            Name = safeTitle.Length > 31 ? safeTitle[..31] : safeTitle,
            Headers = headers,
            Rows = result.Rows.Select(r => r.Select(XlsxValue).ToArray()).ToList(),
        };
        return new ReportOutput(XlsxWriter.Build([sheet]), safeTitle + ".xlsx", ReportFormats.ContentType(ReportFormats.Xlsx), result.Rows.Count);
    }

    // ---------------- saved reports ----------------

    /// <summary>Reports the user owns, plus reports shared with one of their roles (only for DocTypes they may report on).</summary>
    public async Task<IReadOnlyList<SavedAdHocReport>> ListAsync(UserContext user, CancellationToken ct = default) =>
        (await store.ListAsync(user.TenantId, ct)).Where(r => CanView(user, r)).ToList();

    public async Task<SavedAdHocReport> GetAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        var r = await store.GetAsync(user.TenantId, id, ct);
        if (r is null || !CanView(user, r)) throw new DocumentNotFoundException("Report", id.ToString());
        return r;
    }

    public static bool CanEdit(UserContext user, SavedAdHocReport r) =>
        user.IsAdmin || string.Equals(r.Owner, user.Id, StringComparison.OrdinalIgnoreCase);

    private bool CanView(UserContext user, SavedAdHocReport r) =>
        metadata.Current.TryGet(r.Definition.DocType, out var meta) && permissions.Can(user, meta, DocActions.Report)
        && (CanEdit(user, r) || r.SharedWithRoles.Any(user.Roles.Contains));

    /// <summary>
    /// Saves a new report (<paramref name="id"/> null) or updates one the user owns. The definition is validated
    /// against the saving user's permissions.
    /// </summary>
    public async Task<SavedAdHocReport> SaveAsync(UserContext user, Guid? id, string name, string? description,
        IReadOnlyList<string> sharedWithRoles, AdHocReportDef def, int? expectedVersion, CancellationToken ct = default)
    {
        SavedAdHocReport? current = null;
        if (id is not null)
        {
            current = await GetAsync(user, id.Value, ct);
            if (!CanEdit(user, current)) throw new PermissionDeniedException($"Only the owner can change the report '{current.Name}'. Save a copy instead.");
        }
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ValidationFailedException([new FieldError("name", "Report name is required.")]);
        if (name.Length > AdHocLimits.MaxNameLength) throw new ValidationFailedException([new FieldError("name", $"Report name is limited to {AdHocLimits.MaxNameLength} characters.")]);
        Plan(user, def, strict: true);
        var roles = sharedWithRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (roles.Count > 0 && !user.IsAdmin && !roles.All(user.Roles.Contains))
            throw new PermissionDeniedException("You can only share a report with roles you have.");
        var now = clock.GetUtcNow();
        if (current is null)
        {
            var created = new SavedAdHocReport
            {
                Id = Guid.NewGuid(), TenantId = user.TenantId, Name = name, Description = Blank(description), Owner = user.Id,
                SharedWithRoles = roles, Definition = def, CreatedAt = now, ModifiedAt = now, Version = 1,
            };
            await store.SaveAsync(created, null, ct);
            return created;
        }
        var updated = current with
        {
            Name = name, Description = Blank(description), SharedWithRoles = roles, Definition = def, ModifiedAt = now, Version = current.Version + 1,
        };
        await store.SaveAsync(updated, expectedVersion ?? current.Version, ct);
        return updated;
    }

    public async Task DeleteAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        var current = await GetAsync(user, id, ct);
        if (!CanEdit(user, current)) throw new PermissionDeniedException($"Only the owner can delete the report '{current.Name}'.");
        await store.DeleteAsync(user.TenantId, id, ct);
    }

    // ---------------- planning & execution ----------------

    private sealed record ExecPlan(CompiledDocType Meta, AdHocReportDef Def, List<FilterClause> Filters, List<string> Columns,
        List<string> GroupBy, List<AdHocAggregate> Aggregates, List<string> Skipped);

    private CompiledDocType Meta(UserContext user, string docType)
    {
        if (!metadata.Current.TryGet(docType ?? "", out var meta) || meta.Def.IsChild || meta.Def.IsSingle)
            throw new InvalidPayloadException($"DocType '{docType}' cannot be reported on.", ["docType"]);
        permissions.Demand(user, meta, DocActions.Report);
        return meta;
    }

    private ExecPlan Plan(UserContext user, AdHocReportDef def, bool strict)
    {
        var meta = Meta(user, def.DocType);
        var allowed = Fields(user, def.DocType).ToDictionary(c => c.Key, StringComparer.Ordinal);
        var errors = new List<FieldError>();
        var skipped = new List<string>();
        bool Usable(string field, string path)
        {
            if (allowed.ContainsKey(field)) return true;
            if (strict) errors.Add(new FieldError(path, $"Field '{field}' is not available for reporting."));
            else if (!skipped.Contains(field)) skipped.Add(field);
            return false;
        }

        if (def.Columns.Count > AdHocLimits.MaxColumns) errors.Add(new("columns", $"At most {AdHocLimits.MaxColumns} columns."));
        if (def.Filters.Count > AdHocLimits.MaxFilters) errors.Add(new("filters", $"At most {AdHocLimits.MaxFilters} filters."));
        if (def.GroupBy.Count > AdHocLimits.MaxGroupBy) errors.Add(new("groupBy", $"At most {AdHocLimits.MaxGroupBy} group-by fields."));
        if (def.Aggregates.Count > AdHocLimits.MaxAggregates) errors.Add(new("aggregates", $"At most {AdHocLimits.MaxAggregates} aggregates."));
        if (def.Limit is < 1 or > AdHocLimits.MaxRows) errors.Add(new("limit", $"Row limit must be between 1 and {AdHocLimits.MaxRows:N0}."));

        var columns = def.Columns.Distinct(StringComparer.Ordinal).Where(c => Usable(c, "columns")).ToList();
        var groupBy = def.GroupBy.Distinct(StringComparer.Ordinal).Where(c => Usable(c, "groupBy")).ToList();
        var aggregates = new List<AdHocAggregate>();
        foreach (var a in def.Aggregates)
        {
            if (!AdHocLimits.Functions.Contains(a.Function)) { errors.Add(new("aggregates", $"Unknown aggregate '{a.Function}'.")); continue; }
            if (a.Function == "count" && string.IsNullOrEmpty(a.Field)) { aggregates.Add(a with { Field = null }); continue; }
            if (string.IsNullOrEmpty(a.Field)) { errors.Add(new("aggregates", $"Aggregate '{a.Function}' needs a field.")); continue; }
            if (!Usable(a.Field, "aggregates")) continue;
            if (a.Function is "sum" or "avg" && !AdHocLimits.NumericTypes.Contains(allowed[a.Field].Type))
            { errors.Add(new("aggregates", $"'{a.Function}' needs a number field; '{allowed[a.Field].Label}' is not.")); continue; }
            aggregates.Add(a);
        }
        if (groupBy.Count == 0 && aggregates.Count == 0 && columns.Count == 0)
            errors.Add(new("columns", "Choose at least one column, or group the report."));
        if (groupBy.Count > 0 && aggregates.Count == 0) aggregates.Add(new AdHocAggregate("count", null));

        var filters = new List<FilterClause>();
        foreach (var f in def.Filters)
        {
            if (!FilterOps.All.Contains(f.Op)) { errors.Add(new("filters", $"Operator '{f.Op}' is not supported.")); continue; }
            // A filter on a field the viewer cannot read is never dropped silently: it would widen the result.
            if (!allowed.TryGetValue(f.Field, out var col))
            {
                if (strict) errors.Add(new("filters", $"Field '{f.Field}' is not available for reporting."));
                else throw new PermissionDeniedException("This report filters on a field you are not allowed to read.");
                continue;
            }
            try { filters.Add(new FilterClause(f.Field, f.Op, FilterValue(col.Type, f.Op, f.Value))); }
            catch (FormatException ex) { errors.Add(new("filters", $"{col.Label}: {ex.Message}")); }
        }
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        return new ExecPlan(meta, def, filters, columns, groupBy, aggregates, skipped);
    }

    private async Task<AdHocResult> ExecuteAsync(UserContext user, ExecPlan plan, CancellationToken ct)
    {
        var docs = await documents.QueryForReportAsync(user, plan.Meta.Name, plan.Filters, DocActions.Report, ct);
        var truncated = docs.Count >= AdHocLimits.MaxRows;
        var fieldCols = Fields(user, plan.Meta.Name).ToDictionary(c => c.Key, StringComparer.Ordinal);
        List<AdHocColumn> columns;
        List<object?[]> rows;
        if (plan.GroupBy.Count == 0 && plan.Aggregates.Count == 0)
        {
            columns = plan.Columns.Select(c => fieldCols[c]).ToList();
            rows = docs.Select(d => plan.Columns.Select(c => Value(d, c)).ToArray()).ToList();
        }
        else
        {
            columns = plan.GroupBy.Select(c => fieldCols[c]).ToList();
            columns.AddRange(plan.Aggregates.Select(a => AggregateColumn(a, a.Field is null ? null : fieldCols[a.Field])));
            rows = docs
                .GroupBy(d => string.Join("\u001f", plan.GroupBy.Select(g => Text(Value(d, g)))))
                .Select(g =>
                {
                    var first = g.First();
                    var row = plan.GroupBy.Select(c => Value(first, c)).ToList();
                    row.AddRange(plan.Aggregates.Select(a => Aggregate(a, g)));
                    return row.ToArray();
                }).ToList();
        }

        var sortIndex = plan.Def.SortBy is { Length: > 0 } sb ? columns.FindIndex(c => c.Key == sb) : -1;
        if (sortIndex >= 0)
        {
            rows = (plan.Def.SortDescending
                ? rows.OrderByDescending(r => r[sortIndex], ValueComparer.Instance)
                : rows.OrderBy(r => r[sortIndex], ValueComparer.Instance)).ToList();
        }
        else if (plan.GroupBy.Count > 0)
        {
            rows = rows.OrderBy(r => r[0], ValueComparer.Instance).ToList();
        }
        var limited = rows.Count > plan.Def.Limit;
        if (limited) rows = rows.Take(plan.Def.Limit).ToList();
        return new AdHocResult(columns, rows, docs.Count, truncated, limited, plan.Skipped);
    }

    public static string AggregateKey(AdHocAggregate a) => a.Field is null ? a.Function : $"{a.Function}:{a.Field}";

    private static AdHocColumn AggregateColumn(AdHocAggregate a, AdHocColumn? field)
    {
        var label = a.Function switch
        {
            "count" => field is null ? "Count" : $"Count of {field.Label}",
            "sum" => $"Sum of {field!.Label}",
            "avg" => $"Average {field!.Label}",
            "min" => $"Min {field!.Label}",
            _ => $"Max {field!.Label}",
        };
        var type = a.Function == "count" ? "Int" : a.Function == "avg" ? (field!.Type == "Currency" ? "Currency" : "Float") : field!.Type;
        return new AdHocColumn(AggregateKey(a), label, type);
    }

    private static object? Aggregate(AdHocAggregate a, IEnumerable<Document> group)
    {
        if (a.Function == "count")
            return (decimal)(a.Field is null ? group.Count() : group.Count(d => !Values.IsEmpty(Value(d, a.Field))));
        var values = group.Select(d => Value(d, a.Field!)).Where(v => !Values.IsEmpty(v)).ToList();
        if (values.Count == 0) return null;
        if (a.Function is "sum" or "avg")
        {
            var nums = values.Select(Values.ToDecimal).OfType<decimal>().ToList();
            if (nums.Count == 0) return null;
            return a.Function == "sum" ? nums.Sum() : Math.Round(nums.Average(), 6);
        }
        var ordered = values.OrderBy(v => v, ValueComparer.Instance);
        return a.Function == "min" ? ordered.First() : ordered.Last();
    }

    private static object? Value(Document d, string field) => d.Get(field) switch
    {
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        var v => v,
    };

    private static object? FilterValue(string type, string op, string? raw)
    {
        if (op == "in")
            return (raw ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(v => FilterValue(type, "=", v)).ToList();
        if (raw is null || raw.Length == 0) return null;
        if (op == "like") return raw;
        if (AdHocLimits.NumericTypes.Contains(type))
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException($"'{raw}' is not a number.");
        if (type == "Check")
            return raw.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "ya" => true,
                "0" or "false" or "no" or "tidak" => false,
                _ => throw new FormatException($"'{raw}' is not yes/no."),
            };
        if (type == "Date" && !DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new FormatException($"'{raw}' is not a date (yyyy-MM-dd).");
        return raw;
    }

    private static string StandardLabel(string f) => f switch
    {
        StandardFields.Name => "Number",
        StandardFields.Owner => "Created by",
        StandardFields.DocStatus => "Document status",
        StandardFields.WorkflowState => "Workflow state",
        StandardFields.Created => "Created",
        StandardFields.Modified => "Modified",
        _ => f,
    };

    private static string Text(object? v) => v switch
    {
        null => "",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "Yes" : "No",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static object? XlsxValue(object? v) => v switch
    {
        bool b => b ? "Yes" : "No",
        _ => v,
    };

    private static string Csv(string v) =>
        v.IndexOfAny([',', '"', '\n', '\r']) >= 0 || (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
            ? "\"" + (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' ? "'" : "") + v.Replace("\"", "\"\"") + "\""
            : v;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();
        public int Compare(object? x, object? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;
            if (Values.IsNumber(x) && Values.IsNumber(y)) return Values.ToDecimal(x)!.Value.CompareTo(Values.ToDecimal(y)!.Value);
            return string.Compare(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture), StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
