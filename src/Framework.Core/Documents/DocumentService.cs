using System.Globalization;
using Framework.Expressions;
using Framework.Files;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Observability;
using Framework.Persistence;
using Framework.Security;
using Framework.Validation;

namespace Framework.Documents;

public sealed record ActionView(string Action, string ToState, bool RequireComment, string Tone);

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
/// Urutan save (RFC §6): metadata aktif → authorize → load & concurrency → normalize → permitted fields →
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
        permissions.Demand(user, meta, DocActions.Create);
        var data = new Dictionary<string, object?>();
        evaluator.ApplyDefaults(meta, data, user);
        evaluator.ApplyCompute(meta, data, user, Resolve);
        return new Document
        {
            DocType = docType, Name = "", TenantId = user.TenantId, Owner = user.Id, Version = 0,
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

    /// <summary>Memuat dokumen dalam tenant user. Dokumen di luar scope dilaporkan "tidak ditemukan" agar tidak membocorkan keberadaannya (anti-IDOR).</summary>
    private async Task<Document> LoadInScopeAsync(UserContext user, CompiledDocType meta, string name, CancellationToken ct)
    {
        var doc = await store.GetAsync(user.TenantId, meta.Name, name, ct);
        if (doc is null || !permissions.InScope(user, meta, DocActions.Read, doc.Owner, doc.Data))
            throw new DocumentNotFoundException(meta.Title, name);
        return doc;
    }

    private Document RedactForRead(UserContext user, CompiledDocType meta, Document doc)
    {
        var copy = doc.Clone();
        foreach (var f in meta.Fields.Where(f => !permissions.CanReadField(user, meta, f)))
            copy.Data.Remove(f.Name);
        // Field yang sudah tidak ada di metadata aktif tidak dikirim ke client.
        foreach (var key in copy.Data.Keys.Where(k => !meta.ByName.ContainsKey(k)).ToList())
            copy.Data.Remove(key);
        return copy;
    }

    public async Task<ListResponse> ListAsync(UserContext user, string docType, ListRequest req, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Read);
        var lv = meta.Def.ListView;

        bool Readable(string field) => StandardFields.Queryable.Contains(field) ||
            (meta.ByName.TryGetValue(field, out var f) && permissions.CanReadField(user, meta, f));
        var filterable = new HashSet<string>(lv.Filters.Concat(meta.ListColumns).Concat(StandardFields.Queryable));

        var filters = new List<FilterClause>();
        foreach (var f in req.Filters ?? [])
        {
            if (!filterable.Contains(f.Field) || !meta.IsQueryable(f.Field) || !Readable(f.Field))
                throw new InvalidPayloadException($"Filter pada field '{f.Field}' tidak diizinkan", [f.Field]);
            if (!FilterOps.All.Contains(f.Op))
                throw new InvalidPayloadException($"Operator filter '{f.Op}' tidak diizinkan", [f.Field]);
            var value = f.Value;
            if (meta.ByName.TryGetValue(f.Field, out var cf) && f.Op is not ("like" or "in"))
            {
                var n = cf.Type.Normalize(value);
                if (!n.Ok) throw new InvalidPayloadException($"Nilai filter {cf.Label} {n.Error}", [f.Field]);
                value = n.Value;
            }
            else if (f.Field == StandardFields.DocStatus) value = Values.ToDecimal(value);
            filters.Add(f with { Value = value });
        }

        var sort = new List<SortDef>();
        if (req.SortField is not null)
        {
            if (!filterable.Contains(req.SortField) || !meta.IsQueryable(req.SortField) || !Readable(req.SortField))
                throw new InvalidPayloadException($"Sort pada field '{req.SortField}' tidak diizinkan", [req.SortField]);
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
                throw new InvalidPayloadException($"Filter '{f.Field} {f.Op}' tidak diizinkan", [f.Field]);
        var scope = permissions.ScopeFor(user, meta, DocActions.Read);
        var res = await store.QueryAsync(new StoreQuery(user.TenantId, docType, scope, filters, null, [], meta.Def.ListView.DefaultSort, 0, 10_000), ct);
        return res.Items.Select(d => RedactForRead(user, meta, d)).ToList();
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

    public async Task<IReadOnlyList<LinkOption>> SearchLinkAsync(UserContext user, string target, string? q, CancellationToken ct = default)
    {
        var meta = TopLevel(target);
        permissions.Demand(user, meta, DocActions.Read);
        var res = await ListAsync(user, target, new ListRequest(Search: q, PageSize: 10, SortField: null), ct);
        return res.Items.Select(i => new LinkOption((string)i[StandardFields.Name]!, i.GetValueOrDefault("_title")?.ToString())).ToList();
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
        using var hookScope = hooks.EnterDocument(docType, $"(insert:{Guid.NewGuid():N})");
        var p = await PrepareInsertAsync(user, docType, payload, allocateName: true, ct);
        hookScope.Dispose();
        using var finalNameScope = hooks.EnterDocument(docType, p.Doc.Name);
        var uow = new UnitOfWork();
        uow.Upserts.Add((p.Doc, null));
        p.Ctx.Events.Add(Event(p.Doc, "document.inserted", user));
        await FinishAsync(p.Ctx, p.Doc, uow, Audit(p.Doc, user, auditAction, Diff(p.Meta, null, p.Doc)), ct);
        return RedactForRead(user, p.Meta, p.Doc);
    }

    private sealed record PreparedInsert(CompiledDocType Meta, Document Doc, DocumentContext Ctx);

    /// <summary>Tahap persiapan insert yang sama untuk form, API, simulasi, dan impor: payload → pipeline validasi & hook →
    /// cakupan baris → nomor dokumen. Tanpa <paramref name="allocateName"/> nomor urut tidak dipakai (simulasi).</summary>
    private async Task<PreparedInsert> PrepareInsertAsync(UserContext user, string docType, IDictionary<string, object?> payload, bool allocateName, CancellationToken ct)
    {
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Create);
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

        if (!permissions.InScope(user, meta, DocActions.Create, draft.Owner, draft.Data))
            throw new PermissionDeniedException("Dokumen berada di luar cakupan data yang boleh Anda buat");

        var newName = NamingField(meta) is { } nf
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
                    throw new ValidationFailedException([new FieldError(NamingField(p.Meta)?.Name ?? StandardFields.Name, $"Nomor '{p.Doc.Name}' muncul lebih dari sekali dalam impor ini")]);
                hookScope.Dispose();
                using var finalNameScope = hooks.EnterDocument(docType, p.Doc.Name);
                foreach (var h in handlers.For<IAfterSave>(p.Meta.Name)) await h.AfterSaveAsync(p.Ctx, ct);
                RunVisualHooks(p.Meta, "AfterSave", CreateHookContext(p.Ctx), p.Ctx.Previous);
                await hooks.RunAsync(DocHookStage.AfterSave,
                    CreateHookContext(p.Meta, user, p.Doc, p.Ctx.Previous, p.Ctx.Events), ct);
                ThrowIfErrors(p.Ctx);
                uow.Upserts.Add((p.Doc, null));
                if (p.Meta.Def.Document.TrackChanges) uow.Audits.Add(Audit(p.Doc, user, auditAction, Diff(p.Meta, null, p.Doc)));
                p.Ctx.Events.Add(Event(p.Doc, "document.inserted", user));
                uow.Outbox.AddRange(p.Ctx.Events);
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
        using var hookScope = hooks.EnterDocument(docType, name);
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Write);
        var current = await LoadInScopeAsync(user, meta, name, ct);
        if (!permissions.InScope(user, meta, DocActions.Write, current.Owner, current.Data))
            throw new PermissionDeniedException($"Anda tidak boleh mengubah {meta.Title} {name}");
        if (current.Version != expectedVersion) throw new ConcurrencyException(name, expectedVersion, current.Version);
        if (current.DocStatus != DocStatus.Draft)
            throw new InvalidStateException($"{meta.Title} {name} berstatus {current.WorkflowState ?? DocStatus.Label(current.DocStatus)} dan tidak dapat diubah");

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
            throw new PermissionDeniedException("Perubahan ini memindahkan dokumen keluar dari cakupan data Anda");

        var changes = Diff(meta, current, doc);
        var uow = new UnitOfWork();
        uow.Upserts.Add((doc, expectedVersion));
        if (changes.Count > 0) ctx.Events.Add(Event(doc, "document.updated", user));
        await FinishAsync(ctx, doc, uow, changes.Count > 0 ? Audit(doc, user, "Diubah", changes) : null, ct);
        return RedactForRead(user, meta, doc);
    }

    public async Task DeleteAsync(UserContext user, string docType, string name, int expectedVersion, CancellationToken ct = default)
    {
        using var hookScope = hooks.EnterDocument(docType, name);
        var meta = TopLevel(docType);
        permissions.Demand(user, meta, DocActions.Delete);
        var current = await LoadInScopeAsync(user, meta, name, ct);
        if (!permissions.InScope(user, meta, DocActions.Delete, current.Owner, current.Data))
            throw new PermissionDeniedException($"Anda tidak boleh menghapus {name}");
        if (!meta.Def.Document.AllowDelete || current.DocStatus != DocStatus.Draft)
            throw new InvalidStateException("Hanya dokumen draft yang dapat dihapus");

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
        uow.Outbox.Add(Event(current, "document.deleted", user));
        uow.Outbox.AddRange(deleteEvents);
        await store.CommitAsync(uow, ct);
        DocFrameTelemetry.DocumentOperations.Add(1, new KeyValuePair<string, object?>("doctype", docType), new KeyValuePair<string, object?>("operation", "Delete"));

        await hooks.RunAsync(DocHookStage.AfterDelete, deleteContext, ct);
    }

    /// <summary>Menjalankan aksi workflow, atau submit/cancel/amend untuk DocType tanpa workflow.</summary>
    public async Task<Document> RunActionAsync(UserContext user, string docType, string name, string action, int expectedVersion,
        string? comment, CancellationToken ct = default)
    {
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
                ?? throw new PermissionDeniedException($"Aksi '{action}' tidak tersedia untuk Anda pada state '{current.WorkflowState}'");
            if (transition.Def.RequireComment && string.IsNullOrWhiteSpace(comment))
                throw new ValidationFailedException([new FieldError("_comment", $"Aksi '{action}' wajib disertai komentar")]);
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
            if (current.DocStatus != DocStatus.Draft) throw new InvalidStateException("Hanya draft yang dapat di-submit");
            doc.DocStatus = DocStatus.Submitted; toState = "Submitted"; op = DocumentOperation.Submit;
        }
        else if (action == DocActions.Cancel && meta.Def.Document.AllowCancel)
        {
            permissions.Demand(user, meta, DocActions.Cancel);
            if (current.DocStatus != DocStatus.Submitted) throw new InvalidStateException("Hanya dokumen submitted yang dapat dibatalkan");
            doc.DocStatus = DocStatus.Cancelled; toState = "Cancelled"; op = DocumentOperation.Cancel;
        }
        else throw new InvalidPayloadException($"Aksi '{action}' tidak dikenal untuk {meta.Title}");

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
            var cancelContext = CreateHookContext(meta, user, doc, current, ctx.Events, action, fromState, toState, ctx.Messages);
            await hooks.RunAsync(DocHookStage.OnCancel, cancelContext, ct);
        }
        ThrowIfErrors(ctx);

        var uow = new UnitOfWork();
        uow.Upserts.Add((doc, expectedVersion));
        var evt = Event(doc, meta.Workflow is not null ? "workflow.transitioned" : $"document.{action}", user);
        evt.Payload["from"] = fromState; evt.Payload["to"] = toState; evt.Payload["action"] = action; evt.Payload["comment"] = comment;
        ctx.Events.Add(evt);
        var audit = Audit(doc, user, action, Diff(meta, current, doc)) with
        {
            Comment = comment, FromState = fromState ?? DocStatus.Label(current.DocStatus), ToState = toState, WorkflowVersion = doc.WorkflowVersion
        };
        await FinishAsync(ctx, doc, uow, audit, ct);
        return RedactForRead(user, meta, doc);
    }

    private async Task<Document> AmendAsync(UserContext user, CompiledDocType meta, Document source, CancellationToken ct)
    {
        if (!meta.Def.Document.AllowAmend) throw new InvalidStateException($"{meta.Title} tidak mendukung amend");
        if (source.DocStatus != DocStatus.Cancelled) throw new InvalidStateException("Hanya dokumen yang dibatalkan/ditolak yang dapat di-amend");
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
        var amendContext = CreateHookContext(meta, user, source, null, ctx.Events, messages: ctx.Messages);
        await hooks.RunAsync(DocHookStage.OnAmend, amendContext, ct, amendedDraft: doc);
        var uow = new UnitOfWork();
        uow.Upserts.Add((doc, null));
        ctx.Events.Add(Event(doc, "document.amended", user));
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
            doc.Data[key] = n.Value;
        }

        // Mass-assignment ditolak eksplisit, bukan diabaikan diam-diam.
        if (unknown.Count > 0) throw new InvalidPayloadException($"Field tidak dikenal: {string.Join(", ", unknown)}", unknown);
        if (forbidden.Count > 0) throw new PermissionDeniedException($"Anda tidak boleh mengubah field: {string.Join(", ", forbidden)}");
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

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
                if (!childMeta.ByName.TryGetValue(key, out var cf)) throw new InvalidPayloadException($"Field tidak dikenal: {table.Name}[{i}].{key}", [key]);
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
            throw new ValidationFailedException([new FieldError(f.Name, $"{f.Label} hanya boleh huruf, angka, spasi, titik, garis bawah, atau tanda hubung (maks. 64)")]);
        if (await store.GetAsync(user.TenantId, meta.Name, value, ct) is not null)
            throw new ValidationFailedException([new FieldError(f.Name, $"{f.Label} '{value}' sudah dipakai")]);
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
        foreach (var f in ctx.Meta.Fields.Where(f => f.Def.Type == "Image"))
        {
            if (ctx.Doc.Data.GetValueOrDefault(f.Name) is not string id || Equals(ctx.Previous?.Data.GetValueOrDefault(f.Name), id)) continue;
            var info = await files.GetInfoAsync(ctx.User.TenantId, id, ct);
            var ok = info is not null && (
                (info.DocName is null && info.UploadedBy == ctx.User.Id) ||
                (info.DocType == ctx.Meta.Name && (info.DocName == ctx.Doc.Name || info.DocName == ctx.Doc.AmendedFrom)));
            if (!ok) ctx.AddError(f.Name, $"{f.Label}: gambar tidak ditemukan atau bukan milik Anda. Unggah ulang.");
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
        foreach (var f in ctx.Meta.Fields.Where(f => f.Def.Type == "Image"))
            if (doc.Data.GetValueOrDefault(f.Name) is string id && !Equals(ctx.Previous?.Data.GetValueOrDefault(f.Name), id)
                && ctx.Operation is DocumentOperation.Insert or DocumentOperation.Update)
                await files.AttachAsync(doc.TenantId, id, doc.DocType, doc.Name, f.Name, ct);
        foreach (var h in handlers.For<IAfterSave>(ctx.Meta.Name)) await h.AfterSaveAsync(ctx, ct);
        RunVisualHooks(ctx.Meta, "AfterSave", CreateHookContext(ctx), ctx.Previous);
        await hooks.RunAsync(DocHookStage.AfterSave,
            CreateHookContext(ctx.Meta, ctx.User, doc, ctx.Previous, ctx.Events), ct);
        ThrowIfErrors(ctx);
        if (audit is not null && ctx.Meta.Def.Document.TrackChanges) uow.Audits.Add(audit);
        uow.Outbox.AddRange(ctx.Events);
        await store.CommitAsync(uow, ct); // event eksternal baru diproses setelah commit melalui outbox
        doc.TransientMessages.AddRange(ctx.Messages);
        DocFrameTelemetry.DocumentOperations.Add(1, new KeyValuePair<string, object?>("doctype", doc.DocType), new KeyValuePair<string, object?>("operation", ctx.Operation.ToString()));
    }

    private HookContext CreateHookContext(DocumentContext ctx) =>
        CreateHookContext(ctx.Meta, ctx.User, ctx.Doc, ctx.Previous, ctx.Events, messages: ctx.Messages);

    private HookContext CreateHookContext(CompiledDocType meta, UserContext user, Document doc, Document? previous,
        List<OutboxMessage> events, string? action = null, string? fromState = null, string? toState = null, List<string>? messages = null)
    {
        IDocumentService docs = new HookDocumentReader(this, user);
        return new HookContext
        {
            User = user, Meta = meta, Doc = doc, Previous = previous,
            Lookup = new HookLookup(docs), Docs = docs, Events = events,
            Messages = messages ?? [],
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
                ["data"] = System.Text.Json.JsonSerializer.Serialize(doc.Data),
            }
        };
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
                            ["data"] = System.Text.Json.JsonSerializer.Serialize(context.Doc.Data)
                        },
                        CreatedAt = now
                    });
                }
            }
        }
    }
}
