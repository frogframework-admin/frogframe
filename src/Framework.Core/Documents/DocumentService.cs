using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Expressions;
using Framework.Files;
using Framework.Hooks;
using Framework.Imports;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Observability;
using Framework.Persistence;
using Framework.Reports;
using Framework.Security;
using Framework.Validation;

namespace Framework.Documents;

public sealed record ActionView(string Action, string ToState, bool RequireComment, string Tone);

public sealed record BulkActionFailure(string Name, string Error);

public sealed record BulkActionResult(IReadOnlyList<string> Succeeded, IReadOnlyList<BulkActionFailure> Failed);

public sealed record ListRequest(
    IReadOnlyList<FilterClause>? Filters = null,
    string? Search = null,
    string? SortField = null,
    string? SortDirection = null,
    int Page = 1,
    int PageSize = 0);

public sealed record ListResponse(IReadOnlyList<Dictionary<string, object?>> Items, int Total, int Page, int PageSize, IReadOnlyList<string> Columns, bool TotalCapped = false);

public sealed record LinkOption(string Name, string? Title);

public sealed class WorkflowEngine(DocumentEvaluator evaluator)
{
    public static string? CurrentState(CompiledDocType meta, Document doc) =>
        doc.WorkflowState ?? (meta.Workflow is not null && doc.DocStatus == DocStatus.Draft ? meta.Workflow.Def.InitialState : null);

    public IReadOnlyList<CompiledTransition> Available(CompiledDocType meta, Document doc, UserContext user)
    {
        if (meta.Workflow is null) return [];
        // Dokumen draft yang dibuat sebelum workflow dipasang belum punya state → dianggap di initial state.
        var state = CurrentState(meta, doc);
        if (state is null) return [];
        var scope = DocumentService.ScopeData(doc);
        return meta.Workflow.Transitions
            .Where(t => t.Def.From == state)
            .Where(t => t.Def.AllowedRoles.Any(user.HasRole))
            .Where(t => evaluator.Bool(t.Condition, true, scope, user))
            .ToList();
    }
}

/// <summary>
/// Application service — satu-satunya jalur untuk membaca/mengubah dokumen (UI dan API sama-sama lewat sini).
/// Urutan save (RFC §6): metadata aktif → authorize → load &amp; concurrency → normalize → permitted fields →
/// hooks → fetch/compute → validate → persist + audit + outbox dalam satu commit.
/// </summary>
public sealed class DocumentService(
    IMetadataProvider metadata,
    IDocumentStore store,
    IPermissionService permissions,
    DocumentEvaluator evaluator,
    ValidationEngine validation,
    WorkflowEngine workflow,
    IDocumentHandlerRegistry handlers,
    TimeProvider clock,
    IFileStore files,
    HookRunner hooks)
{
    private sealed class TenantLookup(IDocumentStore store, string tenantId) : IDocumentLookup
    {
        public Task<Document?> GetAsync(string docType, string name, CancellationToken ct = default) =>
            store.GetAsync(tenantId, docType, name, ct);
    }

    private sealed class HookDocumentReader(DocumentService service, UserContext user) : IDocumentService
    {
        public Task<Document> GetAsync(string docType, string name, CancellationToken ct = default) => service.GetAsync(user, docType, name, ct);
        public Task<object?> GetFieldAsync(string docType, string name, string fieldName, CancellationToken ct = default) => service.GetFieldAsync(user, docType, name, fieldName, ct);
    }

    private sealed class HookLookup(IDocumentService docs) : IDocumentLookup
    {
        public async Task<Document?> GetAsync(string docType, string name, CancellationToken ct = default)
        {
            try { return await docs.GetAsync(docType, name, ct); }
            catch (DocumentNotFoundException) { return null; }
        }
    }

    private MetadataSnapshot Meta => metadata.Current;
    private CompiledDocType Resolve(string docType) => Meta.Get(docType);

    private CompiledDocType TopLevel(string docType)
    {
        if (!Meta.TryGet(docType, out var m) || m.Def.IsChild) throw new DocumentNotFoundException("DocType", docType);
        return m;
    }

    public static Dictionary<string, object?> ScopeData(Document doc)
    {
        var d = new Dictionary<string, object?>(doc.Data);
        foreach (var f in StandardFields.All) if (f is not ("idx" or "tenant_id")) d[f] = doc.Get(f);
        return d;
    }

    // ---------------- Read ----------------

    public Document New(UserContext user, string docType)
    {
        var meta = TopLevel(docType);
        if (meta.Def.IsSingle)
            permissions.Demand(user, meta, DocActions.Read);
        else
            permissions.Demand(user, meta, DocActions.Create);
        var data = new Dictionary<string, object?>();
        evaluator.ApplyDefaults(meta, data, user);
        evaluator.ApplyCompute(meta, data, user, Resolve);
        return new Document
        {
            DocType = docType, Name = meta.Def.IsSingle ? meta.Name : "", TenantId = user.TenantId, Owner = user.Id, Version = 0,
            SchemaVersion = meta.Version, WorkflowState = meta.Workflow?.Def.InitialState, Data = data
        };
    }

    public async Task<Document> GetAsync(UserContext user, string docType, string name, CancellationToken ct = default)
    {
        using var hookScope = hooks.EnterDocument(docType, name);
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        var doc = await LoadInScopeAsync(user, meta, name, ct);
        var view = RedactForRead(user, meta, doc);
        var hookContext = CreateHookContext(meta, user, view, null, []);
        RunVisualHooks(meta, "OnLoad", hookContext, null);
        await hooks.RunAsync(DocHookStage.OnLoad, hookContext, ct);
        return RedactForRead(user, meta, view);
    }

    public async Task<object?> GetFieldAsync(UserContext user, string docType, string name, string fieldName, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        var doc = await LoadInScopeAsync(user, meta, name, ct);
        if (meta.ByName.TryGetValue(fieldName, out var field))
        {
            if (!permissions.CanReadField(user, meta, field))
                return null;
            return doc.Data.TryGetValue(fieldName, out var v) ? v : null;
        }
        return doc.Get(fieldName);
    }

    /// <summary>
    /// Mengambil dokumen Single (singleton per tenant). Bila belum pernah disimpan,
    /// mengembalikan instans terinisialisasi dengan nilai default dari metadata.
    /// </summary>
    public Task<Document> GetSingleAsync(UserContext user, string docType, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        if (!meta.Def.IsSingle)
            throw new InvalidOperationException($"DocType '{docType}' is not a Single DocType.");
        return GetAsync(user, docType, meta.Name, ct);
    }

    /// <summary>
    /// Menyimpan atau memperbarui dokumen Single (upsert singleton per tenant).
    /// </summary>
    public async Task<Document> SetSingleAsync(UserContext user, string docType, IDictionary<string, object?> data, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        if (!meta.Def.IsSingle)
            throw new InvalidOperationException($"DocType '{docType}' is not a Single DocType.");
        var existing = await store.GetAsync(user.TenantId, meta.Name, meta.Name, ct);
        if (existing is null)
            return await InsertAsync(user, docType, data, ct: ct);
        return await UpdateAsync(user, docType, meta.Name, existing.Version, data, ct: ct);
    }

    /// <summary>Memuat dokumen dalam tenant user. Dokumen di luar scope dilaporkan "tidak ditemukan" agar tidak membocorkan keberadaannya (anti-IDOR).</summary>
    private async Task<Document> LoadInScopeAsync(UserContext user, CompiledDocType meta, string name, CancellationToken ct)
    {
        var doc = await store.GetAsync(user.TenantId, meta.Name, name, ct);
        if (doc is null)
        {
            if (meta.Def.IsSingle && (string.Equals(name, meta.Name, StringComparison.OrdinalIgnoreCase) || name is "(single)" or "new"))
            {
                var initial = New(user, meta.Name);
                return CloneWithName(initial, meta.Name);
            }
            throw new DocumentNotFoundException(meta.Title, name);
        }
        if (!permissions.InScope(user, meta, DocActions.Read, doc.Owner, doc.Data))
            throw new DocumentNotFoundException(meta.Title, name);
        return doc;
    }

    private Document RedactForRead(UserContext user, CompiledDocType meta, Document doc)
    {
        var copy = doc.Clone();
        foreach (var f in meta.Fields.Where(f => !permissions.CanReadField(user, meta, f) || f.IsLayout))
            copy.Data.Remove(f.Name);
        // Password fields: never sent to clients. A set value is shown as the mask so forms know one is stored.
        foreach (var f in meta.Fields.Where(f => f.Def.Type == "Password" && copy.Data.ContainsKey(f.Name)))
            copy.Data[f.Name] = Values.IsEmpty(copy.Data[f.Name]) ? null : PasswordFieldType.Mask;
        // Field yang sudah tidak ada di metadata aktif tidak dikirim ke client.
        foreach (var key in copy.Data.Keys.Where(k => !meta.ByName.ContainsKey(k)).ToList())
            copy.Data.Remove(key);
        return copy;
    }

    public async Task<ListResponse> ListAsync(UserContext user, string docType, ListRequest req, CancellationToken ct = default, bool allowAllReadableFields = false)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        var lv = meta.Def.ListView;

        bool Readable(string field) => StandardFields.Queryable.Contains(field) ||
            (meta.ByName.TryGetValue(field, out var f) && permissions.CanReadField(user, meta, f));
        var filterable = allowAllReadableFields
            ? new HashSet<string>(meta.Fields.Where(f => meta.IsQueryable(f.Name) && Readable(f.Name)).Select(f => f.Name).Concat(StandardFields.Queryable))
            : new HashSet<string>(lv.Filters.Concat(meta.ListColumns).Concat(StandardFields.Queryable));

        var filters = new List<FilterClause>();
        foreach (var f in req.Filters ?? [])
        {
            if (!filterable.Contains(f.Field) || !meta.IsQueryable(f.Field) || !Readable(f.Field))
                throw new InvalidPayloadException($"Filter on field '{f.Field}' is not allowed", [f.Field]);
            if (!FilterOps.All.Contains(f.Op))
                throw new InvalidPayloadException($"Filter operator '{f.Op}' is not allowed", [f.Field]);
            var value = f.Value;
            if (meta.ByName.TryGetValue(f.Field, out var cf) && f.Op is not ("like" or "in"))
            {
                var n = cf.Type.Normalize(value);
                if (!n.Ok) throw new InvalidPayloadException($"Filter value for {cf.Label} is invalid: {n.Error}", [f.Field]);
                value = n.Value;
            }
            else if (f.Field == StandardFields.DocStatus) value = Values.ToDecimal(value);
            filters.Add(f with { Value = value });
        }

        var sort = new List<SortDef>();
        if (req.SortField is not null)
        {
            if (!filterable.Contains(req.SortField) || !meta.IsQueryable(req.SortField) || !Readable(req.SortField))
                throw new InvalidPayloadException($"Sort on field '{req.SortField}' is not allowed", [req.SortField]);
            sort.Add(new SortDef { Field = req.SortField, Direction = req.SortDirection == "asc" ? "asc" : "desc" });
        }
        sort.AddRange(lv.DefaultSort);

        var pageSize = Math.Clamp(req.PageSize <= 0 ? lv.PageSize : req.PageSize, 1, 100);
        var page = Math.Max(1, req.Page);
        var scope = permissions.ScopeFor(user, meta, DocActions.Read);
        var result = await store.QueryAsync(new StoreQuery(user.TenantId, docType, scope, filters,
            string.IsNullOrWhiteSpace(req.Search) ? null : req.Search.Trim(),
            lv.SearchFields.Where(Readable).ToList(), sort, (page - 1) * pageSize, pageSize), ct);

        var columns = meta.ListColumns.Where(Readable).ToList();
        var items = result.Items.Select(d =>
        {
            var row = new Dictionary<string, object?>
            {
                [StandardFields.Name] = d.Name, [StandardFields.DocStatus] = d.DocStatus,
                [StandardFields.WorkflowState] = d.WorkflowState, [StandardFields.Modified] = d.Modified,
                [StandardFields.Owner] = d.Owner, ["version"] = d.Version,
            };
            foreach (var c in columns) row[c] = d.Get(c);
            foreach (var gf in meta.Fields.Where(f => f.Def.RowGrouping && Readable(f.Name))) row[gf.Name] = d.Get(gf.Name);
            if (lv.TitleField is { } tf && Readable(tf)) row["_title"] = d.Get(tf);
            return row;
        }).ToList();
        return new ListResponse(items, result.Total, page, pageSize, columns, result.TotalCapped);
    }

    /// <summary>Query untuk report/export: scope tenant + row-level + field redaction sama dengan list view, tanpa paging UI.</summary>
    public async Task<IReadOnlyList<Document>> QueryForReportAsync(UserContext user, string docType,
        IReadOnlyList<FilterClause> filters, string action = DocActions.Report, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, action);
        foreach (var f in filters)
            if (!meta.IsQueryable(f.Field) || !FilterOps.All.Contains(f.Op))
                throw new InvalidPayloadException($"Filter '{f.Field} {f.Op}' is not allowed", [f.Field]);
        var scope = permissions.ScopeFor(user, meta, DocActions.Read);
        var res = await store.QueryAsync(new StoreQuery(user.TenantId, docType, scope, filters, null, [], meta.Def.ListView.DefaultSort, 0, 10_000), ct);
        return res.Items.Select(d => RedactForRead(user, meta, d)).ToList();
    }

    /// <summary>
    /// Exports tabular list data to Excel (.xlsx) or CSV (.csv), enforcing read + export permissions,
    /// row-level scope, field-level redaction, active search/filter clauses, and CSV formula-injection guards.
    /// </summary>
    public async Task<ReportOutput> ExportListAsync(
        UserContext user,
        string docType,
        string format,
        ListRequest? req = null,
        IReadOnlyList<string>? selectedNames = null,
        IReadOnlyList<string>? visibleColumns = null,
        CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        permissions.Demand(user, meta, DocActions.Export);

        var normFormat = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (normFormat is not ("xlsx" or "csv"))
            throw new InvalidPayloadException($"Export format '{format}' is not supported. Use 'xlsx' or 'csv'.", ["format"]);

        var lv = meta.Def.ListView;
        bool Readable(string field) => StandardFields.Queryable.Contains(field) ||
            (meta.ByName.TryGetValue(field, out var f) && !f.IsLayout && f.Def.Type != "Password" && permissions.CanReadField(user, meta, f));

        var filterable = new HashSet<string>(meta.Fields.Where(f => meta.IsQueryable(f.Name) && Readable(f.Name)).Select(f => f.Name).Concat(StandardFields.Queryable));
        var filters = new List<FilterClause>();
        foreach (var f in req?.Filters ?? [])
        {
            if (!filterable.Contains(f.Field) || !meta.IsQueryable(f.Field) || !Readable(f.Field))
                throw new InvalidPayloadException($"Filter on field '{f.Field}' is not allowed", [f.Field]);
            if (!FilterOps.All.Contains(f.Op))
                throw new InvalidPayloadException($"Filter operator '{f.Op}' is not allowed", [f.Field]);
            var value = f.Value;
            if (meta.ByName.TryGetValue(f.Field, out var cf) && f.Op is not ("like" or "in"))
            {
                var n = cf.Type.Normalize(value);
                if (!n.Ok) throw new InvalidPayloadException($"Filter value for {cf.Label} is invalid: {n.Error}", [f.Field]);
                value = n.Value;
            }
            else if (f.Field == StandardFields.DocStatus) value = Values.ToDecimal(value);
            filters.Add(f with { Value = value });
        }

        if (selectedNames is { Count: > 0 })
        {
            var distinctNames = selectedNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).Cast<object?>().ToList();
            if (distinctNames.Count > 0)
                filters.Add(new FilterClause(StandardFields.Name, "in", distinctNames));
        }

        var sort = new List<SortDef>();
        if (req?.SortField is not null)
        {
            if (!filterable.Contains(req.SortField) || !meta.IsQueryable(req.SortField) || !Readable(req.SortField))
                throw new InvalidPayloadException($"Sort on field '{req.SortField}' is not allowed", [req.SortField]);
            sort.Add(new SortDef { Field = req.SortField, Direction = req.SortDirection == "asc" ? "asc" : "desc" });
        }
        sort.AddRange(lv.DefaultSort);

        var scope = permissions.ScopeFor(user, meta, DocActions.Read);
        var result = await store.QueryAsync(new StoreQuery(
            user.TenantId,
            docType,
            scope,
            filters,
            string.IsNullOrWhiteSpace(req?.Search) ? null : req.Search.Trim(),
            lv.SearchFields.Where(Readable).ToList(),
            sort,
            0,
            10_000), ct);

        var dataCols = visibleColumns is { Count: > 0 }
            ? visibleColumns.Where(c => meta.ByName.ContainsKey(c) && Readable(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : meta.ListColumns.Where(Readable).ToList();

        var includeStatus = meta.Workflow is not null || meta.Def.Document.AllowSubmit;
        var headers = new List<string> { "Number" };
        headers.AddRange(dataCols.Select(c => meta.ByName.TryGetValue(c, out var cf) ? cf.Label : c));
        if (includeStatus) headers.Add("Status");
        headers.Add("Modified");

        var dateStamp = clock.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (normFormat == "xlsx")
        {
            var sheetRows = new List<object?[]>(result.Items.Count);
            foreach (var doc in result.Items)
            {
                var redacted = RedactForRead(user, meta, doc);
                var row = new List<object?>(headers.Count) { redacted.Name };
                foreach (var col in dataCols)
                {
                    var cf = meta.ByName[col];
                    row.Add(FormatXlsxCell(cf, redacted.Get(col)));
                }
                if (includeStatus)
                    row.Add(redacted.WorkflowState ?? DocStatus.Label(redacted.DocStatus));
                row.Add(redacted.Modified.UtcDateTime);
                sheetRows.Add([.. row]);
            }

            var sheetName = Regex.Replace(meta.Title, @"[\[\]\*\?/\\:]", " ").Trim();
            if (sheetName.Length > 31) sheetName = sheetName[..31];
            if (sheetName.Length == 0) sheetName = docType;

            var bytes = XlsxWriter.Build([
                new XlsxSheet
                {
                    Name = sheetName,
                    Headers = headers,
                    Rows = sheetRows
                }
            ]);
            return new ReportOutput(bytes, $"{docType}-{dateStamp}.xlsx", ReportFormats.ContentType(ReportFormats.Xlsx), sheetRows.Count);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(EscapeCsvCell)));
            foreach (var doc in result.Items)
            {
                var redacted = RedactForRead(user, meta, doc);
                var row = new List<string>(headers.Count) { EscapeCsvCell(redacted.Name) };
                foreach (var col in dataCols)
                {
                    var cf = meta.ByName[col];
                    row.Add(EscapeCsvCell(FormatCsvCell(cf, redacted.Get(col))));
                }
                if (includeStatus)
                    row.Add(EscapeCsvCell(redacted.WorkflowState ?? DocStatus.Label(redacted.DocStatus)));
                row.Add(EscapeCsvCell(redacted.Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                sb.AppendLine(string.Join(",", row));
            }

            var csvBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return new ReportOutput(csvBytes, $"{docType}-{dateStamp}.csv", "text/csv; charset=utf-8", result.Items.Count);
        }
    }

    private static object? FormatXlsxCell(CompiledField cf, object? val)
    {
        if (val is null || (val is string empty && empty.Length == 0)) return null;
        return cf.Def.Type switch
        {
            "Int" => Values.ToDecimal(val) is { } i ? (long)i : val,
            "Float" or "Currency" or "Percent" or "Rating" => Values.ToDecimal(val) is { } d ? d : val,
            "Date" when val is string ds && DateTime.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) => dt,
            "Datetime" when DatetimeFieldType.ToZone(val) is { } dto => dto.DateTime,
            "Check" => val is true ? "Yes" : "No",
            "TableMultiSelect" => string.Join(", ", TableMultiSelectFieldType.Items(val)),
            _ => SanitizeSpreadsheetText(Convert.ToString(val, CultureInfo.InvariantCulture) ?? "")
        };
    }

    private static object? FormatCsvCell(CompiledField cf, object? val)
    {
        if (val is null || (val is string empty && empty.Length == 0)) return "";
        return cf.Def.Type switch
        {
            "Int" => Values.ToDecimal(val) is { } i ? ((long)i).ToString(CultureInfo.InvariantCulture) : val,
            "Float" or "Currency" or "Percent" or "Rating" => Values.ToDecimal(val) is { } d ? d.ToString("0.######", CultureInfo.InvariantCulture) : val,
            "Check" => val is true ? "true" : "false",
            "TableMultiSelect" => string.Join(", ", TableMultiSelectFieldType.Items(val)),
            _ => Convert.ToString(val, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private static string SanitizeSpreadsheetText(string s) =>
        s.Length > 0 && "=+-@".Contains(s[0]) ? "'" + s : s;

    private static string EscapeCsvCell(object? v)
    {
        var s = v switch
        {
            null => "",
            decimal d => d.ToString("0.######", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };
        if (s.Length > 0 && v is string && "=+-@".Contains(s[0]))
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Dokumen yang menunggu tindakan workflow dari user ini (untuk beranda/to-do).</summary>
    public async Task<IReadOnlyList<(CompiledDocType Meta, Document Doc, IReadOnlyList<string> Actions)>> PendingActionsAsync(
        UserContext user, CancellationToken ct = default)
    {
        var result = new List<(CompiledDocType, Document, IReadOnlyList<string>)>();
        foreach (var meta in Meta.DocTypes.Values.Where(m => m.Workflow is not null && permissions.Can(user, m, DocActions.Read)))
        {
            var scope = permissions.ScopeFor(user, meta, DocActions.Read);
            var open = await store.QueryAsync(new StoreQuery(user.TenantId, meta.Name, scope,
                [new FilterClause(StandardFields.DocStatus, "in", new List<object?> { 0m, 1m })], null, [], meta.Def.ListView.DefaultSort, 0, 200), ct);
            foreach (var d in open.Items)
            {
                var actions = workflow.Available(meta, d, user).Select(t => t.Def.Action).ToList();
                if (actions.Count > 0) result.Add((meta, RedactForRead(user, meta, d), actions));
            }
        }
        return result;
    }

    /// <summary>Recent documents visible to the user (prioritizing workflow/submittable documents) along with available workflow actions.</summary>
    public async Task<IReadOnlyList<(CompiledDocType Meta, Document Doc, IReadOnlyList<string> Actions)>> RecentDocumentsAsync(
        UserContext user, int limit = 8, CancellationToken ct = default)
    {
        var candidates = new List<(CompiledDocType Meta, Document Doc, IReadOnlyList<string> Actions)>();
        var docTypes = Meta.DocTypes.Values
            .Where(m => !m.Def.IsChild && permissions.Can(user, m, DocActions.Read))
            .OrderByDescending(m => m.Workflow is not null || m.Def.Document.AllowSubmit)
            .ThenBy(m => m.Name)
            .ToList();

        var transactional = docTypes.Where(m => m.Workflow is not null || m.Def.Document.AllowSubmit).ToList();
        var targets = transactional.Count > 0 ? transactional : docTypes;

        foreach (var meta in targets)
        {
            var scope = permissions.ScopeFor(user, meta, DocActions.Read);
            var sort = new List<SortDef> { new() { Field = StandardFields.Modified, Direction = "desc" } };
            var res = await store.QueryAsync(new StoreQuery(user.TenantId, meta.Name, scope, [], null, [], sort, 0, limit), ct);
            foreach (var d in res.Items)
            {
                var actions = meta.Workflow is not null
                    ? workflow.Available(meta, d, user).Select(t => t.Def.Action).ToList()
                    : [];
                candidates.Add((meta, RedactForRead(user, meta, d), actions));
            }
        }

        return candidates
            .OrderByDescending(x => x.Doc.Modified)
            .ThenByDescending(x => x.Doc.Name)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<LinkOption>> SearchLinkAsync(UserContext user, string target, string? q, CancellationToken ct = default,
        string? sourceDocType = null, string? sourceField = null, IReadOnlyList<FilterClause>? filters = null)
    {
        var meta = TopLevel(target);
        permissions.Demand(user, meta, DocActions.Read);
        // Filter hanya mempersempit hasil; server tetap memeriksa filter field saat dokumen disimpan.
        var all = (LinkFiltersFor(target, sourceDocType, sourceField) ?? []).Concat(filters ?? []).ToList();
        var res = await ListAsync(user, target, new ListRequest(Filters: all.Count > 0 ? all : null, Search: q, PageSize: 10, SortField: null), ct, allowAllReadableFields: true);
        return res.Items.Select(i => new LinkOption((string)i[StandardFields.Name]!, i.GetValueOrDefault("_title")?.ToString())).ToList();
    }

    /// <summary>Filter Link statis dari field asal (bila diberikan dan memang menunjuk ke target ini).</summary>
    public IReadOnlyList<FilterClause>? LinkFiltersFor(string target, string? sourceDocType, string? sourceField)
    {
        if (sourceDocType is null || sourceField is null || !Meta.DocTypes.TryGetValue(sourceDocType, out var src)) return null;
        return src.ByName.TryGetValue(sourceField, out var f) && f.Def.LinkTo == target && f.Def.LinkFilters.Count > 0
            ? LinkFilters.ToClauses(f.Def.LinkFilters) : null;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(UserContext user, string docType, string name, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        await LoadInScopeAsync(user, meta, name, ct);
        var hiddenLevels = meta.Fields.Where(f => !permissions.CanReadField(user, meta, f)).Select(f => f.Def.PermLevel).ToHashSet();
        var entries = await store.GetAuditAsync(user.TenantId, docType, name, ct);
        return entries.Select(e => e with { Changes = e.Changes.Where(c => !hiddenLevels.Contains(c.PermLevel)).ToList() }).ToList();
    }

    public async Task<IReadOnlyList<ActionView>> AvailableActionsAsync(UserContext user, Document doc, CancellationToken ct = default)
    {
        var meta = TopLevel(doc.DocType);
        if (string.IsNullOrEmpty(doc.Name)) return [];
        var stored = await LoadInScopeAsync(user, meta, doc.Name, ct);
        var actions = new List<ActionView>();
        if (meta.Workflow is not null)
        {
            actions.AddRange(workflow.Available(meta, stored, user).Select(t =>
                new ActionView(t.Def.Action, t.Def.To, t.Def.RequireComment, meta.Workflow.States[t.Def.To].Tone)));
        }
        else
        {
            if (meta.Def.Document.AllowSubmit && stored.DocStatus == DocStatus.Draft && permissions.Can(user, meta, DocActions.Submit))
                actions.Add(new ActionView(DocActions.Submit, "Submitted", false, "positive"));
            if (meta.Def.Document.AllowCancel && stored.DocStatus == DocStatus.Submitted && permissions.Can(user, meta, DocActions.Cancel))
                actions.Add(new ActionView(DocActions.Cancel, "Cancelled", true, "negative"));
        }
        if (meta.Def.Document.AllowAmend && stored.DocStatus == DocStatus.Cancelled && permissions.Can(user, meta, DocActions.Create))
            actions.Add(new ActionView(DocActions.Amend, "Draft", false, "neutral"));
        return actions;
    }

    /// <summary>Mencatat pencetakan dokumen di riwayat audit (siapa, kapan, format apa).</summary>
    public async Task LogPrintAsync(UserContext user, string docType, string name, string format, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        var doc = await LoadInScopeAsync(user, meta, name, ct);
        var uow = new UnitOfWork();
        uow.Audits.Add(Audit(doc, user, "Dicetak", []) with { Comment = $"Format {format}" });
        await store.CommitAsync(uow, ct);
    }

    // ---------------- Write ----------------

    public Task<Document> InsertAsync(UserContext user, string docType, IDictionary<string, object?> payload, CancellationToken ct = default) =>
        InsertAsync(user, docType, payload, "Dibuat", ct);

    /// <param name="auditAction">Teks aksi di audit, mis. "Dibuat lewat impor #imp_x".</param>
    public async Task<Document> InsertAsync(UserContext user, string docType, IDictionary<string, object?> payload, string auditAction, CancellationToken ct = default)
    {
        using var activity = FrogFrameTelemetry.ActivitySource.StartActivity("frogframe.document.insert");
        activity?.SetTag("frogframe.doctype", docType);
        activity?.SetTag("frogframe.tenant", user.TenantId);
        activity?.SetTag("frogframe.user", user.Id);

        using var hookScope = hooks.EnterDocument(docType, $"(insert:{Guid.NewGuid():N})");
        var p = await PrepareInsertAsync(user, docType, payload, allocateName: true, ct);
        activity?.SetTag("frogframe.name", p.Doc.Name);
        hookScope.Dispose();
        using var finalNameScope = hooks.EnterDocument(docType, p.Doc.Name);
        var uow = new UnitOfWork();
        uow.Upserts.Add((p.Doc, null));
        p.Ctx.Events.Add(Event(p.Doc, EventTypes.DocumentInserted, user));
        await FinishAsync(p.Ctx, p.Doc, uow, Audit(p.Doc, user, auditAction, Diff(p.Meta, null, p.Doc)), ct);
        return RedactForRead(user, p.Meta, p.Doc);
    }

    private sealed record PreparedInsert(CompiledDocType Meta, Document Doc, DocumentContext Ctx);

    /// <summary>Tahap persiapan insert yang sama untuk form, API, simulasi, dan impor: payload → pipeline validasi & hook →
    /// cakupan baris → nomor dokumen. Tanpa <paramref name="allocateName"/> nomor urut tidak dipakai (simulasi).</summary>
    private async Task<PreparedInsert> PrepareInsertAsync(UserContext user, string docType, IDictionary<string, object?> payload, bool allocateName, CancellationToken ct)
    {
        var meta = TopLevel(docType);
        if (meta.Def.IsSingle)
        {
            if (!permissions.Can(user, meta, DocActions.Write) && !permissions.Can(user, meta, DocActions.Create))
                throw new PermissionDeniedException($"You do not have 'write' permission for {meta.Title}.");
        }
        else
        {
            permissions.Demand(user, meta, DocActions.Create);
        }
        var lookup = new TenantLookup(store, user.TenantId);
        var now = clock.GetUtcNow();

        var data = new Dictionary<string, object?>();
        evaluator.ApplyDefaults(meta, data, user);
        var draft = new Document
        {
            DocType = docType, Name = "(baru)", TenantId = user.TenantId, Owner = user.Id, Created = now, Modified = now,
            ModifiedBy = user.Id, Version = 1, SchemaVersion = meta.Version, DocStatus = DocStatus.Draft,
            WorkflowState = meta.Workflow?.Def.InitialState, WorkflowVersion = meta.Workflow?.Def.Version, Data = data
        };
        ApplyPayload(user, meta, draft, payload, isNew: true);

        var ctx = new DocumentContext { Meta = meta, Doc = draft, User = user, Operation = DocumentOperation.Insert, Lookup = lookup };
        RunVisualHooks(meta, "BeforeValidate", CreateHookContext(ctx), null);
        await hooks.RunAsync(DocHookStage.BeforeValidate, CreateHookContext(ctx), ct);
        await RunPipelineAsync(ctx, ct);
        RunVisualHooks(meta, "BeforeInsert", CreateHookContext(ctx), null);
        await hooks.RunAsync(DocHookStage.BeforeInsert, CreateHookContext(ctx), ct);

        if (!permissions.InScope(user, meta, meta.Def.IsSingle ? DocActions.Write : DocActions.Create, draft.Owner, draft.Data))
            throw new PermissionDeniedException(meta.Def.IsSingle
                ? $"Document is outside the scope of data you are allowed to modify."
                : "Document is outside the scope of data you are allowed to create.");

        if (meta.Def.IsSingle && await store.GetAsync(user.TenantId, docType, meta.Name, ct) is not null)
            throw new ValidationFailedException([new FieldError(StandardFields.Name, $"{meta.Title} hanya boleh satu per tenant dan sudah ada. Buka dan ubah dokumen yang ada.")]);
        var newName = meta.Def.IsSingle ? meta.Name
            : NamingField(meta) is { } nf
            ? await NameFromFieldAsync(user, meta, nf, draft, ct)
            : allocateName ? await store.NextNameAsync(user.TenantId, docType, meta.Def.Naming.Pattern, now, ct) : "(baru)";
        var doc = CloneWithName(draft, newName);
        return new PreparedInsert(meta, doc, ctx);
    }

    /// <summary>Simulasi insert (impor dry-run): seluruh aturan dijalankan, tidak ada yang disimpan dan nomor urut tidak dipakai.</summary>
    public async Task<Document> ValidateInsertAsync(UserContext user, string docType, IDictionary<string, object?> payload, CancellationToken ct = default)
    {
        using var hookScope = hooks.EnterDocument(docType, $"(simulation:{Guid.NewGuid():N})");
        var p = await PrepareInsertAsync(user, docType, payload, allocateName: false, ct);
        return RedactForRead(user, p.Meta, p.Doc);
    }

    /// <summary>
    /// Insert banyak dokumen dalam SATU commit (semua atau tidak sama sekali). Gagal pada baris ke-i →
    /// <see cref="BatchRowException"/> dengan indeks baris; tidak ada yang tersimpan.
    /// </summary>
    public async Task<IReadOnlyList<Document>> InsertBatchAsync(UserContext user, string docType, IReadOnlyList<IDictionary<string, object?>> payloads,
        string auditAction, CancellationToken ct = default)
    {
        var uow = new UnitOfWork();
        var prepared = new List<PreparedInsert>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < payloads.Count; i++)
        {
            try
            {
                using var hookScope = hooks.EnterDocument(docType, $"(batch-row:{i}:{Guid.NewGuid():N})");
                var p = await PrepareInsertAsync(user, docType, payloads[i], allocateName: true, ct);
                if (!names.Add(p.Doc.Name))
                    throw new ValidationFailedException([new FieldError(NamingField(p.Meta)?.Name ?? StandardFields.Name, $"Identifier '{p.Doc.Name}' appears more than once in this import")]);
                hookScope.Dispose();
                using var finalNameScope = hooks.EnterDocument(docType, p.Doc.Name);
                foreach (var h in handlers.For<IAfterSave>(p.Meta.Name)) await h.AfterSaveAsync(p.Ctx, ct);
                RunVisualHooks(p.Meta, "AfterSave", CreateHookContext(p.Ctx), p.Ctx.Previous);
                await hooks.RunAsync(DocHookStage.AfterSave,
                    CreateHookContext(p.Meta, user, p.Doc, p.Ctx.Previous, p.Ctx.Events, messages: p.Ctx.Messages, extensions: p.Ctx.Extensions), ct);
                ThrowIfErrors(p.Ctx);
                uow.Upserts.Add((p.Doc, null));
                if (p.Meta.Def.Document.TrackChanges) uow.Audits.Add(Audit(p.Doc, user, auditAction, Diff(p.Meta, null, p.Doc)));
                p.Ctx.Events.Add(Event(p.Doc, EventTypes.DocumentInserted, user));
                uow.Outbox.AddRange(p.Ctx.Events);
                uow.Extensions.AddRange(p.Ctx.Extensions.Select(e => new UnitOfWorkExtension(p.Doc, e)));
                prepared.Add(p);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BatchRowException(i, ex);
            }
        }
        await store.CommitAsync(uow, ct);
        foreach (var p in prepared) p.Doc.TransientMessages.AddRange(p.Ctx.Messages);
        return prepared.Select(p => RedactForRead(user, p.Meta, p.Doc)).ToList();
    }

    public async Task<Document> UpdateAsync(UserContext user, string docType, string name, int expectedVersion,
        IDictionary<string, object?> patch, CancellationToken ct = default)
    {
        using var activity = FrogFrameTelemetry.ActivitySource.StartActivity("frogframe.document.update");
        activity?.SetTag("frogframe.doctype", docType);
        activity?.SetTag("frogframe.name", name);
        activity?.SetTag("frogframe.tenant", user.TenantId);
        activity?.SetTag("frogframe.user", user.Id);

        using var hookScope = hooks.EnterDocument(docType, name);
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Write);
        var current = await LoadInScopeAsync(user, meta, name, ct);
        if (!permissions.InScope(user, meta, DocActions.Write, current.Owner, current.Data))
            throw new PermissionDeniedException($"You are not allowed to modify {meta.Title} {name}.");
        if (current.Version != expectedVersion) throw new ConcurrencyException(name, expectedVersion, current.Version);
        if (current.DocStatus != DocStatus.Draft)
            throw new InvalidStateException($"{meta.Title} {name} is in status {current.WorkflowState ?? DocStatus.Label(current.DocStatus)} and cannot be modified.");

        var doc = current.Clone();
        ApplyPayload(user, meta, doc, patch, isNew: false);
        doc.Modified = clock.GetUtcNow(); doc.ModifiedBy = user.Id; doc.Version = current.Version + 1;
        doc.SchemaVersion = meta.Version;

        var ctx = new DocumentContext { Meta = meta, Doc = doc, Previous = current, User = user, Operation = DocumentOperation.Update, Lookup = new TenantLookup(store, user.TenantId) };
        RunVisualHooks(meta, "BeforeValidate", CreateHookContext(ctx), null);
        await hooks.RunAsync(DocHookStage.BeforeValidate, CreateHookContext(ctx), ct);
        await RunPipelineAsync(ctx, ct);
        RunVisualHooks(meta, "BeforeUpdate", CreateHookContext(ctx), current);
        await hooks.RunAsync(DocHookStage.BeforeUpdate, CreateHookContext(ctx), ct, Diff(meta, current, doc));
        if (!permissions.InScope(user, meta, DocActions.Write, doc.Owner, doc.Data))
            throw new PermissionDeniedException("This modification moves the document outside your data scope.");

        var changes = Diff(meta, current, doc);
        var uow = new UnitOfWork();
        uow.Upserts.Add((doc, expectedVersion));
        if (changes.Count > 0) ctx.Events.Add(Event(doc, EventTypes.DocumentUpdated, user));
        await FinishAsync(ctx, doc, uow, changes.Count > 0 ? Audit(doc, user, "Diubah", changes) : null, ct);
        return RedactForRead(user, meta, doc);
    }

    public async Task DeleteAsync(UserContext user, string docType, string name, int expectedVersion, CancellationToken ct = default)
    {
        if (Meta.DocTypes.TryGetValue(docType, out var singleMeta) && singleMeta.Def.IsSingle)
            throw new InvalidStateException($"{singleMeta.Title} adalah dokumen tunggal dan tidak bisa dihapus.");
        using var activity = FrogFrameTelemetry.ActivitySource.StartActivity("frogframe.document.delete");
        activity?.SetTag("frogframe.doctype", docType);
        activity?.SetTag("frogframe.name", name);
        activity?.SetTag("frogframe.tenant", user.TenantId);
        activity?.SetTag("frogframe.user", user.Id);

        using var hookScope = hooks.EnterDocument(docType, name);
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Delete);
        var current = await LoadInScopeAsync(user, meta, name, ct);
        if (!permissions.InScope(user, meta, DocActions.Delete, current.Owner, current.Data))
            throw new PermissionDeniedException($"You are not allowed to delete {name}.");
        if (!meta.Def.Document.AllowDelete || current.DocStatus != DocStatus.Draft)
            throw new InvalidStateException("Only draft documents can be deleted.");

        if (Meta.IncomingLinks.TryGetValue(docType, out var linkDefs) && linkDefs.Count > 0)
        {
            var link = await store.FindFirstLinkAsync(user.TenantId, docType, name, linkDefs, ct);
            if (link is not null)
                throw new LinkedDocumentException("delete", meta.Title, name, link.LinkingDocType, link.LinkingDocName, link.FieldLabel);
        }

        var deleteEvents = new List<OutboxMessage>();
        var deleteContext = CreateHookContext(meta, user, current, null, deleteEvents);
        await hooks.RunAsync(DocHookStage.BeforeDelete, deleteContext, ct);

        var uow = new UnitOfWork();
        uow.Deletes.Add((user.TenantId, docType, name, expectedVersion));
        uow.Audits.Add(Audit(current, user, "Dihapus", []));
        uow.Outbox.Add(Event(current, EventTypes.DocumentDeleted, user));
        uow.Outbox.AddRange(deleteEvents);
        await store.CommitAsync(uow, ct);
        FrogFrameTelemetry.DocumentOperations.Add(1, new KeyValuePair<string, object?>("doctype", docType), new KeyValuePair<string, object?>("operation", "Delete"));

        await hooks.RunAsync(DocHookStage.AfterDelete, deleteContext, ct);
    }

    /// <summary>Menjalankan aksi workflow, atau submit/cancel/amend untuk DocType tanpa workflow.</summary>
    public async Task<Document> RunActionAsync(UserContext user, string docType, string name, string action, int expectedVersion,
        string? comment, CancellationToken ct = default)
    {
        using var activity = FrogFrameTelemetry.ActivitySource.StartActivity("frogframe.document.action");
        activity?.SetTag("frogframe.doctype", docType);
        activity?.SetTag("frogframe.name", name);
        activity?.SetTag("frogframe.action", action);
        activity?.SetTag("frogframe.tenant", user.TenantId);
        activity?.SetTag("frogframe.user", user.Id);

        using var hookScope = hooks.EnterDocument(docType, name);
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        var current = await LoadInScopeAsync(user, meta, name, ct);
        if (current.Version != expectedVersion) throw new ConcurrencyException(name, expectedVersion, current.Version);

        if (action == DocActions.Amend) return await AmendAsync(user, meta, current, ct);

        var doc = current.Clone();
        doc.Modified = clock.GetUtcNow(); doc.ModifiedBy = user.Id; doc.Version = current.Version + 1;
        doc.SchemaVersion = meta.Version;
        string? fromState = WorkflowEngine.CurrentState(meta, current), toState;
        DocumentOperation op;
        var transitionEvents = new List<OutboxMessage>();

        if (meta.Workflow is not null)
        {
            var transition = workflow.Available(meta, current, user).FirstOrDefault(t => t.Def.Action == action)
                ?? throw new PermissionDeniedException($"Action '{action}' is not available for you in state '{current.WorkflowState}'.");
            if (transition.Def.RequireComment && string.IsNullOrWhiteSpace(comment))
                throw new ValidationFailedException([new FieldError("_comment", $"Action '{action}' requires a comment.")]);
            toState = transition.Def.To;
            var transitionContext = CreateHookContext(meta, user, doc, current, transitionEvents, action, fromState, toState);
            await hooks.RunAsync(DocHookStage.BeforeTransition, transitionContext, ct);
            doc.WorkflowState = toState;
            doc.DocStatus = meta.Workflow.States[toState].DocStatus;
            doc.WorkflowVersion = meta.Workflow.Def.Version;
            op = DocumentOperation.Transition;
        }
        else if (action == DocActions.Submit && meta.Def.Document.AllowSubmit)
        {
            permissions.Demand(user, meta, DocActions.Submit);
            if (current.DocStatus != DocStatus.Draft) throw new InvalidStateException("Only draft documents can be submitted.");
            doc.DocStatus = DocStatus.Submitted; toState = "Submitted"; op = DocumentOperation.Submit;
        }
        else if (action == DocActions.Cancel && meta.Def.Document.AllowCancel)
        {
            permissions.Demand(user, meta, DocActions.Cancel);
            if (current.DocStatus != DocStatus.Submitted) throw new InvalidStateException("Only submitted documents can be cancelled.");
            doc.DocStatus = DocStatus.Cancelled; toState = "Cancelled"; op = DocumentOperation.Cancel;
        }
        else throw new InvalidPayloadException($"Action '{action}' is not recognized for {meta.Title}.");

        var ctx = new DocumentContext { Meta = meta, Doc = doc, Previous = current, User = user, Operation = op, Lookup = new TenantLookup(store, user.TenantId), Comment = comment };
        ctx.Events.AddRange(transitionEvents);

        // Masuk ke status submitted → dokumen harus valid penuh dan hook submit dijalankan.
        if (current.DocStatus == DocStatus.Draft && doc.DocStatus == DocStatus.Submitted)
        {
            await RunPipelineAsync(ctx, ct);
            foreach (var h in handlers.For<IBeforeSubmit>(docType)) await h.BeforeSubmitAsync(ctx, ct);
        }
        if (doc.DocStatus == DocStatus.Cancelled && current.DocStatus != DocStatus.Cancelled)
        {
            if (Meta.IncomingLinks.TryGetValue(docType, out var linkDefs) && linkDefs.Count > 0)
            {
                var link = await store.FindFirstLinkAsync(user.TenantId, docType, name, linkDefs, ct);
                if (link is not null)
                    throw new LinkedDocumentException("cancel", meta.Title, name, link.LinkingDocType, link.LinkingDocName, link.FieldLabel);
            }
            foreach (var h in handlers.For<IBeforeCancel>(docType)) await h.BeforeCancelAsync(ctx, ct);
            var cancelContext = CreateHookContext(meta, user, doc, current, ctx.Events, action, fromState, toState, ctx.Messages, ctx.Extensions);
            await hooks.RunAsync(DocHookStage.OnCancel, cancelContext, ct);
        }
        ThrowIfErrors(ctx);

        var uow = new UnitOfWork();
        uow.Upserts.Add((doc, expectedVersion));
        var evt = Event(doc, meta.Workflow is not null ? EventTypes.WorkflowTransitioned : EventTypes.DocumentAction(action), user);
        evt.Payload["from"] = fromState; evt.Payload["to"] = toState; evt.Payload["action"] = action; evt.Payload["comment"] = comment;
        ctx.Events.Add(evt);
        var audit = Audit(doc, user, action, Diff(meta, current, doc)) with
        {
            Comment = comment, FromState = fromState ?? DocStatus.Label(current.DocStatus), ToState = toState, WorkflowVersion = doc.WorkflowVersion
        };
        await FinishAsync(ctx, doc, uow, audit, ct);
        return RedactForRead(user, meta, doc);
    }

    // ---------------- Bulk Actions ----------------

    /// <summary>Checks whether the user can perform Bulk Submit on this DocType (either via standard submit or workflow transition).</summary>
    public bool CanBulkSubmit(UserContext user, CompiledDocType meta)
    {
        if (meta.Def.IsSingle || meta.Def.IsChild) return false;
        if (meta.Workflow is null)
            return meta.Def.Document.AllowSubmit && permissions.Can(user, meta, DocActions.Submit);
        return permissions.Can(user, meta, DocActions.Read) &&
               meta.Workflow.Transitions.Any(t =>
                   t.Def.AllowedRoles.Any(user.HasRole) &&
                   meta.Workflow.States.TryGetValue(t.Def.From, out var fromState) && fromState.DocStatus == DocStatus.Draft &&
                   meta.Workflow.States.TryGetValue(t.Def.To, out var toState) &&
                   (toState.DocStatus == DocStatus.Submitted || toState.Tone != "negative"));
    }

    /// <summary>Checks whether the user can perform Bulk Cancel on this DocType (either via standard cancel or workflow transition).</summary>
    public bool CanBulkCancel(UserContext user, CompiledDocType meta)
    {
        if (meta.Def.IsSingle || meta.Def.IsChild) return false;
        if (meta.Workflow is null)
            return meta.Def.Document.AllowCancel && permissions.Can(user, meta, DocActions.Cancel);
        return permissions.Can(user, meta, DocActions.Read) &&
               meta.Workflow.Transitions.Any(t =>
                   t.Def.AllowedRoles.Any(user.HasRole) &&
                   IsCancelTransition(meta.Workflow, t));
    }

    private static bool IsCancelTransition(CompiledWorkflow wf, CompiledTransition t) =>
        string.Equals(t.Def.Action, "Cancel", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Def.Action, "Withdraw", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Def.Action, "Batalkan", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Def.To, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Def.To, "Dibatalkan", StringComparison.OrdinalIgnoreCase) ||
        (wf.States.TryGetValue(t.Def.To, out var s) &&
         s.DocStatus == DocStatus.Cancelled &&
         !string.Equals(t.Def.Action, "Reject", StringComparison.OrdinalIgnoreCase) &&
         !string.Equals(t.Def.Action, "Tolak", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the top-level fields on <paramref name="meta"/> that <paramref name="user"/> is allowed to modify via Bulk Edit.</summary>
    public IReadOnlyList<CompiledField> BulkEditableFields(UserContext user, CompiledDocType meta)
    {
        if (meta.Def.IsSingle || meta.Def.IsChild || !permissions.Can(user, meta, DocActions.Write))
            return [];
        var namingField = NamingField(meta)?.Name;
        return meta.Fields
            .Where(f => !StandardFields.All.Contains(f.Name)
                        && !f.IsLayout
                        && !f.IsDerived
                        && f.Name != namingField
                        && f.Def.Type is not ("Table" or "Password" or "Signature" or "Attach" or "Image")
                        && !string.Equals(f.Def.ReadOnlyWhen?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                        && permissions.CanWriteField(user, meta, f))
            .ToList();
    }

    /// <summary>Submits multiple draft documents, executing validation, workflow rules, hooks, audit, and outbox per document.</summary>
    public async Task<BulkActionResult> BulkSubmitAsync(
        UserContext user,
        string docType,
        IReadOnlyList<string> names,
        string? comment = null,
        CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        if (!CanBulkSubmit(user, meta))
            throw new PermissionDeniedException($"You do not have permission to submit {meta.Title}.");

        var succeeded = new List<string>();
        var failed = new List<BulkActionFailure>();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var current = await LoadInScopeAsync(user, meta, name, ct);
                if (meta.Workflow is null)
                {
                    await RunActionAsync(user, docType, name, DocActions.Submit, current.Version, comment, ct);
                }
                else
                {
                    if (current.DocStatus != DocStatus.Draft)
                        throw new InvalidStateException("Only draft documents can be submitted.");
                    var available = workflow.Available(meta, current, user);
                    var transition = available.FirstOrDefault(t =>
                                         meta.Workflow.States.TryGetValue(t.Def.To, out var s) && s.DocStatus == DocStatus.Submitted)
                                     ?? available.FirstOrDefault(t =>
                                         meta.Workflow.States.TryGetValue(t.Def.To, out var s) && s.Tone != "negative")
                                     ?? throw new InvalidStateException($"Action 'Submit' is not available for {name} in status '{current.WorkflowState ?? DocStatus.Label(current.DocStatus)}'.");
                    await RunActionAsync(user, docType, name, transition.Def.Action, current.Version, comment, ct);
                }
                succeeded.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new BulkActionFailure(name, FormatBulkError(ex)));
            }
        }
        return new BulkActionResult(succeeded, failed);
    }

    /// <summary>Cancels multiple submitted documents, checking incoming document links, hooks, workflow transitions, and audit logs.</summary>
    public async Task<BulkActionResult> BulkCancelAsync(
        UserContext user,
        string docType,
        IReadOnlyList<string> names,
        string? comment = null,
        CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        if (!CanBulkCancel(user, meta))
            throw new PermissionDeniedException($"You do not have permission to cancel {meta.Title}.");

        var succeeded = new List<string>();
        var failed = new List<BulkActionFailure>();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var current = await LoadInScopeAsync(user, meta, name, ct);
                if (meta.Workflow is null)
                {
                    await RunActionAsync(user, docType, name, DocActions.Cancel, current.Version, comment, ct);
                }
                else
                {
                    if (current.DocStatus != DocStatus.Submitted)
                        throw new InvalidStateException("Only submitted documents can be cancelled.");
                    var available = workflow.Available(meta, current, user);
                    var transition = available.FirstOrDefault(t => IsCancelTransition(meta.Workflow, t))
                                     ?? throw new InvalidStateException($"Action 'Cancel' is not available for {name} in status '{current.WorkflowState ?? DocStatus.Label(current.DocStatus)}'.");
                    await RunActionAsync(user, docType, name, transition.Def.Action, current.Version, comment, ct);
                }
                succeeded.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new BulkActionFailure(name, FormatBulkError(ex)));
            }
        }
        return new BulkActionResult(succeeded, failed);
    }

    /// <summary>Updates a single permitted field across multiple draft documents, enforcing full validation and hooks per document.</summary>
    public async Task<BulkActionResult> BulkEditAsync(
        UserContext user,
        string docType,
        IReadOnlyList<string> names,
        string fieldName,
        object? value,
        CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Write);
        var editable = BulkEditableFields(user, meta).FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal))
            ?? throw new InvalidPayloadException($"Field '{fieldName}' cannot be bulk-edited on {meta.Title}.", [fieldName]);

        var succeeded = new List<string>();
        var failed = new List<BulkActionFailure>();
        var patch = new Dictionary<string, object?> { [editable.Name] = value };
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var current = await LoadInScopeAsync(user, meta, name, ct);
                await UpdateAsync(user, docType, name, current.Version, patch, ct);
                succeeded.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new BulkActionFailure(name, FormatBulkError(ex)));
            }
        }
        return new BulkActionResult(succeeded, failed);
    }

    /// <summary>Deletes multiple draft documents, enforcing delete permissions, row scope, and link integrity per document.</summary>
    public async Task<BulkActionResult> BulkDeleteAsync(
        UserContext user,
        string docType,
        IReadOnlyList<string> names,
        CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Delete);

        var succeeded = new List<string>();
        var failed = new List<BulkActionFailure>();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var current = await LoadInScopeAsync(user, meta, name, ct);
                await DeleteAsync(user, docType, name, current.Version, ct);
                succeeded.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new BulkActionFailure(name, FormatBulkError(ex)));
            }
        }
        return new BulkActionResult(succeeded, failed);
    }

    private static string FormatBulkError(Exception ex) => ex switch
    {
        ValidationFailedException v => string.Join(", ", v.Errors.Select(e => e.Message)),
        _ => ex.Message
    };

    private async Task<Document> AmendAsync(UserContext user, CompiledDocType meta, Document source, CancellationToken ct)
    {
        if (!meta.Def.Document.AllowAmend) throw new InvalidStateException($"{meta.Title} does not support amend.");
        if (source.DocStatus != DocStatus.Cancelled) throw new InvalidStateException("Only cancelled documents can be amended.");
        permissions.Demand(user, meta, DocActions.Create);

        var baseName = source.AmendedFrom is null ? source.Name : source.Name[..source.Name.LastIndexOf('-')];
        var n = 1;
        while (await store.GetAsync(user.TenantId, meta.Name, $"{baseName}-{n}", ct) is not null) n++;
        var now = clock.GetUtcNow();
        var doc = new Document
        {
            DocType = meta.Name, Name = $"{baseName}-{n}", TenantId = user.TenantId, Owner = user.Id, Created = now, Modified = now,
            ModifiedBy = user.Id, Version = 1, SchemaVersion = meta.Version, DocStatus = DocStatus.Draft,
            WorkflowState = meta.Workflow?.Def.InitialState, WorkflowVersion = meta.Workflow?.Def.Version,
            AmendedFrom = source.Name, Data = Document.DeepCopy(source.Data)
        };
        var ctx = new DocumentContext { Meta = meta, Doc = doc, Previous = source, User = user, Operation = DocumentOperation.Amend, Lookup = new TenantLookup(store, user.TenantId) };
        var amendContext = CreateHookContext(meta, user, source, null, ctx.Events, messages: ctx.Messages, extensions: ctx.Extensions);
        await hooks.RunAsync(DocHookStage.OnAmend, amendContext, ct, amendedDraft: doc);
        var uow = new UnitOfWork();
        uow.Upserts.Add((doc, null));
        ctx.Events.Add(Event(doc, EventTypes.DocumentAmended, user));
        await FinishAsync(ctx, doc, uow, Audit(doc, user, "Amend", []) with { Comment = $"Amend dari {source.Name}" }, ct);
        return RedactForRead(user, meta, doc);
    }

    // ---------------- Pipeline ----------------

    private void ApplyPayload(UserContext user, CompiledDocType meta, Document doc, IDictionary<string, object?> payload, bool isNew)
    {
        var unknown = new List<string>();
        var forbidden = new List<string>();
        var errors = new List<FieldError>();
        var scope = ScopeData(doc);

        foreach (var (key, raw) in payload)
        {
            if (StandardFields.All.Contains(key)) { forbidden.Add(key); continue; }
            if (!meta.ByName.TryGetValue(key, out var f)) { unknown.Add(key); continue; }
            if (f.IsDerived) continue; // dihitung ulang server; nilai client tidak dipercaya
            if (f.IsLayout) continue;  // HTML, Button, Heading: tidak menyimpan nilai
            // Password: kosong atau mask = pertahankan nilai tersimpan (UI tidak pernah menerima nilai aslinya).
            if (f.Def.Type == "Password" && KeepsSecret(raw)) continue;

            var n = f.Def.Type == "Table" ? NormalizeTable(user, f, raw, errors) : f.Type.Normalize(raw);
            if (!n.Ok) { if (n.Error is not null) errors.Add(new(key, $"{f.Label} {n.Error}")); continue; }

            var changed = !SameValue(doc.Data.GetValueOrDefault(key), n.Value);
            if (!changed) continue;
            if (!isNew && NamingField(meta)?.Name == key)
            { errors.Add(new(key, $"{f.Label} dipakai sebagai nomor dokumen dan tidak bisa diubah setelah disimpan")); continue; }
            if (!permissions.CanWriteField(user, meta, f) || (!isNew && evaluator.StateOf(f, scope, user).ReadOnly))
            { forbidden.Add(key); continue; }
            if (f.Def.Type == "Link" && n.Value is not null && !permissions.Can(user, Resolve(f.Def.LinkTo!), DocActions.Read))
            { forbidden.Add(key); continue; }
            if (f.Def.Type == "TableMultiSelect" && n.Value is List<string> { Count: > 0 } && !permissions.Can(user, Resolve(f.Def.LinkTo!), DocActions.Read))
            { forbidden.Add(key); continue; }
            doc.Data[key] = n.Value;
        }

        // DynamicLink: the target DocType comes from another field, possibly set in the same payload.
        foreach (var f in meta.Fields.Where(f => f.Def.Type == "DynamicLink" && f.Def.LinkToField is not null
                     && (payload.ContainsKey(f.Name) || payload.ContainsKey(f.Def.LinkToField!))))
        {
            if (doc.Data.GetValueOrDefault(f.Name) is not string || doc.Data.GetValueOrDefault(f.Def.LinkToField!) is not string target) continue;
            if (Meta.TryGet(target, out var targetMeta) && !targetMeta.Def.IsChild && !permissions.Can(user, targetMeta, DocActions.Read))
                forbidden.Add(f.Name);
        }

        // Mass-assignment ditolak eksplisit, bukan diabaikan diam-diam.
        if (unknown.Count > 0) throw new InvalidPayloadException($"Unknown field(s): {string.Join(", ", unknown)}", unknown);
        if (forbidden.Count > 0) throw new PermissionDeniedException($"You are not allowed to modify field(s): {string.Join(", ", forbidden)}");
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

    /// <summary>A Password value that means "keep what is stored": empty, null, or the read mask.</summary>
    private static bool KeepsSecret(object? raw) => raw switch
    {
        null => true,
        string s => s.Length == 0 || s == PasswordFieldType.Mask,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() is null or "" or PasswordFieldType.Mask,
        _ => false,
    };

    private NormalizeResult NormalizeTable(UserContext user, CompiledField table, object? raw, List<FieldError> errors)
    {
        var res = table.Type.Normalize(raw);
        if (!res.Ok) return res;
        var childMeta = Resolve(table.Def.ChildDocType!);
        var rows = new List<Dictionary<string, object?>>();
        var i = 0;
        foreach (var inRow in (List<Dictionary<string, object?>>)res.Value!)
        {
            var row = new Dictionary<string, object?> { ["idx"] = (long)(i + 1) };
            foreach (var (key, val) in inRow)
            {
                if (key is "idx" or "name") continue;
                if (!childMeta.ByName.TryGetValue(key, out var cf)) throw new InvalidPayloadException($"Unknown field: {table.Name}[{i}].{key}", [key]);
                if (cf.IsDerived) continue;
                var n = cf.Type.Normalize(val);
                if (!n.Ok) errors.Add(new($"{table.Name}[{i}].{key}", $"{cf.Label} {n.Error}"));
                else row[key] = n.Value;
            }
            foreach (var cf in childMeta.Fields.Where(cf => !row.ContainsKey(cf.Name)))
                row[cf.Name] = evaluator.DefaultFor(cf, user);
            rows.Add(row); i++;
        }
        return errors.Count > 0 ? NormalizeResult.Fail(null!) : NormalizeResult.Success(rows);
    }

    private async Task RunPipelineAsync(DocumentContext ctx, CancellationToken ct)
    {
        var dt = ctx.Meta.Name;
        foreach (var h in handlers.For<IBeforeValidate>(dt)) await h.BeforeValidateAsync(ctx, ct);
        await ApplyFetchFromAsync(ctx, ct);
        ctx.Errors.AddRange(evaluator.ApplyCompute(ctx.Meta, ctx.Doc.Data, ctx.User, Resolve));
        ctx.Errors.AddRange(await validation.ValidateAsync(ctx.Meta, ctx.Doc.Data, ctx.User, Resolve, ctx.Lookup, ct));
        await CheckSelfLinkCyclesAsync(ctx, ct);
        await ValidateImagesAsync(ctx, ct);
        foreach (var h in handlers.For<IValidateDocument>(dt)) await h.ValidateAsync(ctx, ct);
        ThrowIfErrors(ctx);
        foreach (var h in handlers.For<IBeforeSave>(dt)) await h.BeforeSaveAsync(ctx, ct);
        ThrowIfErrors(ctx);
    }

    // ---------------- Penamaan dari field, siklus, gambar ----------------

    private static CompiledField? NamingField(CompiledDocType meta) =>
        meta.Def.Naming.Pattern.StartsWith("field:") && meta.ByName.TryGetValue(meta.Def.Naming.Pattern["field:".Length..], out var f) ? f : null;

    private static readonly System.Text.RegularExpressions.Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$");

    private async Task<string> NameFromFieldAsync(UserContext user, CompiledDocType meta, CompiledField f, Document draft, CancellationToken ct)
    {
        var value = (draft.Data.GetValueOrDefault(f.Name) as string)?.Trim() ?? "";
        if (!SafeName.IsMatch(value))
            throw new ValidationFailedException([new FieldError(f.Name, $"{f.Label} may only contain letters, numbers, spaces, dots, underscores, or hyphens (max 64).")]);
        if (await store.GetAsync(user.TenantId, meta.Name, value, ct) is not null)
            throw new ValidationFailedException([new FieldError(f.Name, $"{f.Label} '{value}' is already in use.")]);
        return value;
    }

    /// <summary>Link ke DocType yang sama (pohon: induk departemen, atasan) tidak boleh menunjuk diri sendiri atau membentuk siklus.</summary>
    private static async Task CheckSelfLinkCyclesAsync(DocumentContext ctx, CancellationToken ct)
    {
        foreach (var f in ctx.Meta.Fields.Where(f => f.Def.Type == "Link" && f.Def.LinkTo == ctx.Meta.Name))
        {
            if (ctx.Doc.Data.GetValueOrDefault(f.Name) is not string start) continue;
            if (start == ctx.Doc.Name) { ctx.AddError(f.Name, $"{f.Label} tidak boleh menunjuk dokumen ini sendiri"); continue; }
            var chain = new List<string> { ctx.Doc.Name, start };
            var current = start;
            for (var depth = 0; depth < 100; depth++)
            {
                var parent = await ctx.Lookup.GetAsync(ctx.Meta.Name, current, ct);
                if (parent?.Data.GetValueOrDefault(f.Name) is not string next) break;
                chain.Add(next);
                if (next == ctx.Doc.Name) { ctx.AddError(f.Name, $"{f.Label} membentuk siklus: {string.Join(" → ", chain)}"); break; }
                if (chain.Count(c => c == next) > 1) break; // siklus lama di atas, bukan melibatkan dokumen ini
                current = next;
            }
        }
    }

    /// <summary>Id gambar harus file di tenant ini yang diunggah pengguna sendiri, atau sudah milik dokumen ini.</summary>
    private async Task ValidateImagesAsync(DocumentContext ctx, CancellationToken ct)
    {
        foreach (var f in ctx.Meta.Fields.Where(f => FieldKinds.IsFileField(f.Def.Type)))
        {
            if (ctx.Doc.Data.GetValueOrDefault(f.Name) is not string id || Equals(ctx.Previous?.Data.GetValueOrDefault(f.Name), id)) continue;
            var info = await files.GetInfoAsync(ctx.User.TenantId, id, ct);
            var ok = info is not null && (
                (info.DocName is null && info.UploadedBy == ctx.User.Id) ||
                (info.DocType == ctx.Meta.Name && (info.DocName == ctx.Doc.Name || info.DocName == ctx.Doc.AmendedFrom)));
            if (!ok) ctx.AddError(f.Name, $"{f.Label}: {(f.Def.Type == "Image" ? "gambar" : "file")} tidak ditemukan atau bukan milik Anda. Unggah ulang.");
        }
    }

    private static void ThrowIfErrors(DocumentContext ctx)
    {
        if (ctx.Errors.Count > 0) throw new ValidationFailedException(ctx.Errors.DistinctBy(e => (e.Path, e.Message)).ToList());
    }

    private async Task ApplyFetchFromAsync(DocumentContext ctx, CancellationToken ct)
    {
        foreach (var f in ctx.Meta.Fields.Where(f => f.FetchFrom is not null))
        {
            var (linkField, target) = f.FetchFrom!.Value;
            var linkValue = ctx.Doc.Data.GetValueOrDefault(linkField) as string;
            var linkMeta = ctx.Meta.ByName[linkField];
            var linked = linkValue is null ? null : await ctx.Lookup.GetAsync(linkMeta.Def.LinkTo!, linkValue, ct);
            ctx.Doc.Data[f.Name] = linked?.Data.GetValueOrDefault(target);
        }
    }

    private async Task FinishAsync(DocumentContext ctx, Document doc, UnitOfWork uow, AuditEntry? audit, CancellationToken ct)
    {
        // Gambar baru/berganti ditautkan ke dokumen (nama final sudah diketahui); sejak itu izin lihat = izin baca dokumen.
        foreach (var f in ctx.Meta.Fields.Where(f => FieldKinds.IsFileField(f.Def.Type)))
            if (doc.Data.GetValueOrDefault(f.Name) is string id && !Equals(ctx.Previous?.Data.GetValueOrDefault(f.Name), id)
                && ctx.Operation is DocumentOperation.Insert or DocumentOperation.Update)
                await files.AttachAsync(doc.TenantId, id, doc.DocType, doc.Name, f.Name, ct);
        foreach (var h in handlers.For<IAfterSave>(ctx.Meta.Name)) await h.AfterSaveAsync(ctx, ct);
        RunVisualHooks(ctx.Meta, "AfterSave", CreateHookContext(ctx), ctx.Previous);
        await hooks.RunAsync(DocHookStage.AfterSave,
            CreateHookContext(ctx.Meta, ctx.User, doc, ctx.Previous, ctx.Events, messages: ctx.Messages, extensions: ctx.Extensions), ct);
        ThrowIfErrors(ctx);
        if (audit is not null && ctx.Meta.Def.Document.TrackChanges) uow.Audits.Add(audit);
        uow.Outbox.AddRange(ctx.Events);
        uow.Extensions.AddRange(ctx.Extensions.Select(e => new UnitOfWorkExtension(doc, e)));
        try { await store.CommitAsync(uow, ct); } // event eksternal baru diproses setelah commit melalui outbox
        catch (UnitOfWorkValidationException ex) { throw new ValidationFailedException(ex.Errors); }
        doc.TransientMessages.AddRange(ctx.Messages);
        FrogFrameTelemetry.DocumentOperations.Add(1, new KeyValuePair<string, object?>("doctype", doc.DocType), new KeyValuePair<string, object?>("operation", ctx.Operation.ToString()));
    }

    private HookContext CreateHookContext(DocumentContext ctx) =>
        CreateHookContext(ctx.Meta, ctx.User, ctx.Doc, ctx.Previous, ctx.Events, messages: ctx.Messages, extensions: ctx.Extensions);

    private HookContext CreateHookContext(CompiledDocType meta, UserContext user, Document doc, Document? previous,
        List<OutboxMessage> events, string? action = null, string? fromState = null, string? toState = null, List<string>? messages = null,
        List<IUnitOfWorkExtension>? extensions = null)
    {
        IDocumentService docs = new HookDocumentReader(this, user);
        return new HookContext
        {
            User = user, Meta = meta, Doc = doc, Previous = previous,
            Lookup = new HookLookup(docs), Docs = docs, Events = events,
            Messages = messages ?? [],
            Extensions = extensions ?? [],
            Action = action, FromState = fromState, ToState = toState,
        };
    }

    // ---------------- Helpers ----------------

    private static Document CloneWithName(Document d, string name)
    {
        var c = d.Clone();
        return new Document
        {
            DocType = c.DocType, Name = name, TenantId = c.TenantId, Owner = c.Owner, DocStatus = c.DocStatus,
            WorkflowState = c.WorkflowState, Created = c.Created, Modified = c.Modified, ModifiedBy = c.ModifiedBy,
            Version = c.Version, SchemaVersion = c.SchemaVersion, WorkflowVersion = c.WorkflowVersion, Data = c.Data
        };
    }

    /// <summary>Versi bentuk muatan event (naikkan bila kunci berubah tidak kompatibel).</summary>
    public const string EventPayloadVersion = "2";

    /// <summary>
    /// Event outbox, ditulis dalam transaksi yang sama dengan dokumen. Selain identitas, membawa versi, status, dan
    /// SNAPSHOT DATA SAAT KEJADIAN (kunci "data", JSON) — proyeksi analitik (Dunia 2) tidak perlu membaca ulang dokumen
    /// yang mungkin sudah berubah lagi, sehingga riwayat yang direkam tepat sesuai keadaan pada versi itu.
    /// </summary>
    private OutboxMessage Event(Document doc, string type, UserContext user)
    {
        var now = clock.GetUtcNow();
        return new OutboxMessage
        {
            TenantId = doc.TenantId, Type = type, CreatedAt = now,
            IdempotencyKey = $"{doc.TenantId}:{doc.DocType}:{doc.Name}:v{doc.Version}:{type}",
            Payload = new()
            {
                ["payloadVersion"] = EventPayloadVersion,
                ["docType"] = doc.DocType, ["name"] = doc.Name, ["owner"] = doc.Owner,
                ["actor"] = user.Id, ["actorName"] = user.FullName,
                ["occurredAt"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["version"] = doc.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["schemaVersion"] = doc.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["docStatus"] = ((int)doc.DocStatus).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["workflowState"] = doc.WorkflowState,
                ["workflowVersion"] = doc.WorkflowVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["amendedFrom"] = doc.AmendedFrom,
                ["data"] = SerializeEventData(doc),
            }
        };
    }

    /// <summary>Document data for events and webhooks: Password fields are masked, never sent (even encrypted).</summary>
    private string SerializeEventData(Document doc)
    {
        if (!Meta.TryGet(doc.DocType, out var meta) || meta.Fields.All(f => f.Def.Type != "Password"))
            return System.Text.Json.JsonSerializer.Serialize(doc.Data);
        var copy = new Dictionary<string, object?>(doc.Data);
        foreach (var f in meta.Fields.Where(f => f.Def.Type == "Password" && copy.ContainsKey(f.Name)))
            copy[f.Name] = Values.IsEmpty(copy[f.Name]) ? null : PasswordFieldType.Mask;
        return System.Text.Json.JsonSerializer.Serialize(copy);
    }

    private AuditEntry Audit(Document doc, UserContext user, string action, IReadOnlyList<FieldChange> changes) => new()
    {
        TenantId = doc.TenantId, DocType = doc.DocType, DocName = doc.Name, Action = action, Actor = user.FullName,
        At = clock.GetUtcNow(), Changes = changes, SchemaVersion = doc.SchemaVersion
    };

    private static List<FieldChange> Diff(CompiledDocType meta, Document? before, Document after)
    {
        var list = new List<FieldChange>();
        foreach (var f in meta.Fields)
        {
            var o = before?.Data.GetValueOrDefault(f.Name);
            var n = after.Data.GetValueOrDefault(f.Name);
            if (SameValue(o, n)) continue;
            if (f.IsLayout) continue;
            if (f.Def.Type == "Password")
            {
                // The audit trail records that a secret changed, never its (encrypted) value.
                list.Add(new FieldChange(f.Label, Values.IsEmpty(o) ? null : PasswordFieldType.Mask, Values.IsEmpty(n) ? null : PasswordFieldType.Mask, f.Def.PermLevel));
                continue;
            }
            list.Add(new FieldChange(f.Label, Display(o), Display(n), f.Def.PermLevel));
        }
        return list;
    }

    private static string? Display(object? v) => v switch
    {
        null => null,
        List<Dictionary<string, object?>> rows => $"{rows.Count} baris",
        bool b => b ? "Ya" : "Tidak",
        decimal d => d.ToString("#,0.##", CultureInfo.GetCultureInfo("id-ID")),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)
    };

    private static bool SameValue(object? a, object? b)
    {
        if (a is List<Dictionary<string, object?>> ra && b is List<Dictionary<string, object?>> rb)
            return ra.Count == rb.Count && ra.Zip(rb).All(p =>
                p.First.Keys.Union(p.Second.Keys).Where(k => k != "idx").All(k => SameValue(p.First.GetValueOrDefault(k), p.Second.GetValueOrDefault(k))));
        if (a is null || b is null) return Values.IsEmpty(a) && Values.IsEmpty(b);
        if (a is List<string> la && b is List<string> lb) return la.SequenceEqual(lb, StringComparer.Ordinal);
        return Values.AreEqual(a, b);
    }

    private void RunVisualHooks(CompiledDocType meta, string stage, Framework.Hooks.HookContext context, Document? previous)
    {
        foreach (var hook in meta.Hooks.Where(h => h.Def.Enabled && h.Def.Event == stage))
        {
            if (hook.Def.When is { } condition)
            {
                var current = context.Doc.Data.GetValueOrDefault(condition.Field);
                var matches = condition.Operator switch
                {
                    "Equals" => SameValue(current, hook.ConditionValue),
                    "NotEquals" => !SameValue(current, hook.ConditionValue),
                    "ChangedTo" => previous is not null &&
                        !SameValue(previous.Data.GetValueOrDefault(condition.Field), current) &&
                        SameValue(current, hook.ConditionValue),
                    _ => false
                };
                if (!matches) continue;
            }

            foreach (var action in hook.Def.Actions)
            {
                if (action.Type == "Reject")
                    throw new ValidationFailedException([new FieldError(action.Field ?? "", action.Message!)]);
                if (action.Type == "EmitEvent")
                {
                    var now = clock.GetUtcNow();
                    context.Events.Add(new OutboxMessage
                    {
                        TenantId = context.Doc.TenantId,
                        Type = action.EventType!,
                        IdempotencyKey = $"{context.Doc.TenantId}:{context.Doc.DocType}:{context.Doc.Name}:v{context.Doc.Version}:{hook.Def.Name}:{action.EventType}",
                        Payload = new()
                        {
                            ["docType"] = context.Doc.DocType,
                            ["name"] = context.Doc.Name,
                            ["version"] = context.Doc.Version.ToString(CultureInfo.InvariantCulture),
                            ["actor"] = context.User.Id,
                            ["data"] = SerializeEventData(context.Doc)
                        },
                        CreatedAt = now
                    });
                }
            }
        }
    }
}
