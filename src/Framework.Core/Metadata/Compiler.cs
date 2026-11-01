using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Expressions;

namespace Framework.Metadata;

public static class StandardFields
{
    public const string Name = "name", Owner = "owner", Created = "created", Modified = "modified",
        ModifiedBy = "modified_by", DocStatus = "docstatus", WorkflowState = "workflow_state", Version = "version",
        AmendedFrom = "amended_from";

    public static readonly ImmutableHashSet<string> All =
        [Name, Owner, Created, Modified, ModifiedBy, DocStatus, WorkflowState, Version, AmendedFrom, "idx", "tenant_id"];

    /// <summary>Field standar yang boleh dipakai di list view, sort, dan filter.</summary>
    public static readonly ImmutableHashSet<string> Queryable = [Name, Owner, Created, Modified, DocStatus, WorkflowState];
}

public static class DocActions
{
    public const string Read = "read", Create = "create", Write = "write", Delete = "delete",
        Submit = "submit", Cancel = "cancel", Amend = "amend", Export = "export", Report = "report";
    public static readonly ImmutableHashSet<string> All = [Read, Create, Write, Delete, Submit, Cancel, Amend, Export, Report];
}

public sealed record MetadataError(string Source, string Path, string Message)
{
    public override string ToString() => $"{Source} › {Path}: {Message}";
}

public sealed class MetadataCompilationException(IReadOnlyList<MetadataError> errors)
    : Exception("Metadata tidak valid:\n" + string.Join("\n", errors))
{
    public IReadOnlyList<MetadataError> Errors { get; } = errors;
}

// ---------- Model terkompilasi (immutable, di-cache per DocType + Version) ----------

public sealed class CompiledField
{
    public required FieldDef Def { get; init; }
    public required IFieldType Type { get; init; }
    public string Name => Def.Name;
    public string Label => string.IsNullOrEmpty(Def.Label) ? Def.Name : Def.Label;
    public ParsedExpression? RequiredWhen { get; init; }
    public ParsedExpression? VisibleWhen { get; init; }
    public ParsedExpression? ReadOnlyWhen { get; init; }
    public ParsedExpression? Compute { get; init; }
    public (string LinkField, string TargetField)? FetchFrom { get; init; }
    public Regex? Pattern { get; init; }
    public object? StaticDefault { get; init; }
    public string? DynamicDefault { get; init; }
    public bool IsDerived => Compute is not null || FetchFrom is not null;
}

public sealed record LayoutColumn(IReadOnlyList<CompiledField> Fields);
public sealed record LayoutSection(string? Title, IReadOnlyList<LayoutColumn> Columns);
public sealed record LayoutTab(string Title, IReadOnlyList<LayoutSection> Sections);

public sealed class CompiledRule
{
    public required RuleDef Def { get; init; }
    public required ParsedExpression Expr { get; init; }
}

public sealed class CompiledTransition
{
    public required WorkflowTransitionDef Def { get; init; }
    public ParsedExpression? Condition { get; init; }
}

public sealed class CompiledWorkflow
{
    public required WorkflowDef Def { get; init; }
    public required IReadOnlyDictionary<string, WorkflowStateDef> States { get; init; }
    public required IReadOnlyList<CompiledTransition> Transitions { get; init; }
}

public sealed class CompiledDocType
{
    public required DocTypeDef Def { get; init; }
    public string Name => Def.Name;
    public int Version => Def.Version;
    public string Title => string.IsNullOrEmpty(Def.Title) ? Def.Name : Def.Title;
    public required IReadOnlyList<CompiledField> Fields { get; init; }
    public required IReadOnlyDictionary<string, CompiledField> ByName { get; init; }
    /// <summary>Field compute dalam urutan topologis (dependensi lebih dulu).</summary>
    public required IReadOnlyList<CompiledField> ComputeOrder { get; init; }
    public required IReadOnlyList<CompiledRule> Rules { get; init; }
    public required IReadOnlyList<LayoutTab> Layout { get; init; }
    public CompiledWorkflow? Workflow { get; init; }
    public required IReadOnlyList<string> ListColumns { get; init; }

    public IEnumerable<CompiledField> TableFields => Fields.Where(f => f.Def.Type == "Table");
    public bool IsQueryable(string field) => StandardFields.Queryable.Contains(field) ||
        (ByName.TryGetValue(field, out var f) && f.Def.Type != "Table");
}

/// <summary>Snapshot metadata aktif. Immutable; diganti secara atomik saat publish.</summary>
public sealed class MetadataSnapshot
{
    public required ImmutableDictionary<string, CompiledDocType> DocTypes { get; init; }
    public required ImmutableDictionary<string, CompiledWorkflow> Workflows { get; init; }

    public CompiledDocType Get(string docType) =>
        DocTypes.TryGetValue(docType, out var d) ? d : throw new KeyNotFoundException($"DocType '{docType}' tidak ditemukan");
    public bool TryGet(string docType, out CompiledDocType meta) => DocTypes.TryGetValue(docType, out meta!);
}

// ---------- Compiler ----------

public sealed partial class MetadataCompiler(IFieldTypeRegistry fieldTypes, FunctionRegistry functions)
{
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]{0,63}$")] private static partial Regex DocTypeNameRx();
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}$")] private static partial Regex FieldNameRx();
    [GeneratedRegex(@"^match:([a-z][a-z0-9_]*)=([a-z][a-z0-9_]*)$")] private static partial Regex MatchScopeRx();
    private static readonly string[] DynamicDefaults = ["$today", "$user", "$employee"];

    public MetadataSnapshot Compile(IReadOnlyCollection<DocTypeDef> docTypes, IReadOnlyCollection<WorkflowDef> workflows)
    {
        var errors = new List<MetadataError>();
        var defs = new Dictionary<string, DocTypeDef>();
        foreach (var d in docTypes)
        {
            if (!DocTypeNameRx().IsMatch(d.Name)) errors.Add(new(d.Name, "name", "Nama DocType harus PascalCase alfanumerik"));
            if (!defs.TryAdd(d.Name, d)) errors.Add(new(d.Name, "name", "DocType duplikat"));
        }

        var wfDefs = workflows.ToDictionary(w => w.Name);
        var compiledWorkflows = new Dictionary<string, CompiledWorkflow>();
        foreach (var wf in workflows)
        {
            var cw = CompileWorkflow(wf, defs, errors);
            if (cw is not null) compiledWorkflows[wf.Name] = cw;
        }

        var compiled = new Dictionary<string, CompiledDocType>();
        foreach (var d in defs.Values)
        {
            var c = CompileDocType(d, defs, compiledWorkflows, errors);
            if (c is not null) compiled[d.Name] = c;
        }

        if (errors.Count > 0) throw new MetadataCompilationException(errors);
        return new MetadataSnapshot
        {
            DocTypes = compiled.ToImmutableDictionary(),
            Workflows = compiledWorkflows.ToImmutableDictionary()
        };
    }

    private ParsedExpression? ParseExpr(string? src, string source, string path, List<MetadataError> errors,
        ISet<string> allowedRoots, bool validateRoots = true)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        try
        {
            var parsed = new ExpressionParser(functions).Parse(src);
            foreach (var root in parsed.RootNames.Where(r => validateRoots && !allowedRoots.Contains(r)))
                errors.Add(new(source, path, $"Ekspresi merujuk nama tidak dikenal '{root}'"));
            return parsed;
        }
        catch (ExpressionException ex)
        {
            errors.Add(new(source, path, $"Ekspresi tidak valid: {ex.Message}"));
            return null;
        }
    }

    private CompiledDocType? CompileDocType(DocTypeDef d, Dictionary<string, DocTypeDef> defs,
        Dictionary<string, CompiledWorkflow> workflows, List<MetadataError> errors)
    {
        var src = $"{d.Name}@v{d.Version}";
        var startErrors = errors.Count;
        if (d.Version < 1) errors.Add(new(src, "version", "Versi harus >= 1"));
        if (d.Fields.Count == 0) errors.Add(new(src, "fields", "DocType minimal memiliki satu field"));

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in d.Fields)
        {
            if (!FieldNameRx().IsMatch(f.Name)) errors.Add(new(src, $"fields.{f.Name}", "Nama field harus snake_case"));
            else if (StandardFields.All.Contains(f.Name)) errors.Add(new(src, $"fields.{f.Name}", "Nama field dicadangkan oleh framework"));
            if (!fieldNames.Add(f.Name)) errors.Add(new(src, $"fields.{f.Name}", "Field duplikat"));
        }

        // Nama yang boleh dipakai dalam ekspresi: field sendiri, field standar, user, dan parent (untuk child).
        var roots = new HashSet<string>(fieldNames) { "user" };
        roots.UnionWith(StandardFields.All);
        if (d.IsChild) roots.Add("parent");

        var compiledFields = new List<CompiledField>();
        foreach (var f in d.Fields)
        {
            var path = $"fields.{f.Name}";
            if (!fieldTypes.TryGet(f.Type, out var type))
            {
                errors.Add(new(src, path, $"Tipe field '{f.Type}' tidak terdaftar"));
                continue;
            }

            switch (f.Type)
            {
                case "Link":
                    if (string.IsNullOrEmpty(f.LinkTo) || !defs.TryGetValue(f.LinkTo, out var target))
                        errors.Add(new(src, path, $"linkTo '{f.LinkTo}' tidak ditemukan"));
                    else if (target.IsChild)
                        errors.Add(new(src, path, "Link tidak boleh menunjuk child DocType"));
                    break;
                case "Table":
                    if (d.IsChild) errors.Add(new(src, path, "Child table bertingkat tidak didukung"));
                    if (string.IsNullOrEmpty(f.ChildDocType) || !defs.TryGetValue(f.ChildDocType, out var child))
                        errors.Add(new(src, path, $"childDocType '{f.ChildDocType}' tidak ditemukan"));
                    else if (!child.IsChild)
                        errors.Add(new(src, path, $"'{f.ChildDocType}' harus ditandai isChild"));
                    else if (f.AddFromLookup is { } add)
                    {
                        var link = child.Fields.FirstOrDefault(x => x.Name == add.LinkField);
                        if (link is not { Type: "Link" } || link.LinkTo is null)
                            errors.Add(new(src, $"{path}.addFromLookup", $"linkField '{add.LinkField}' harus field Link di {child.Name}"));
                        else if (defs.TryGetValue(link.LinkTo, out var source))
                            foreach (var (childField, sourceField) in add.Map)
                            {
                                if (child.Fields.All(x => x.Name != childField))
                                    errors.Add(new(src, $"{path}.addFromLookup.map", $"Field '{childField}' tidak ada di {child.Name}"));
                                if (source.Fields.All(x => x.Name != sourceField))
                                    errors.Add(new(src, $"{path}.addFromLookup.map", $"Field '{sourceField}' tidak ada di {source.Name}"));
                            }
                    }
                    break;
                case "Select":
                    if (f.Options is not { Count: > 0 }) errors.Add(new(src, path, "Field Select membutuhkan options"));
                    break;
            }

            Regex? rx = null;
            if (!string.IsNullOrEmpty(f.Pattern))
            {
                try { rx = new Regex(f.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                catch (ArgumentException) { errors.Add(new(src, path, "Pattern regex tidak valid")); }
            }

            (string, string)? fetch = null;
            if (!string.IsNullOrEmpty(f.FetchFrom))
            {
                var parts = f.FetchFrom.Split('.');
                var linkField = parts.Length == 2 ? d.Fields.FirstOrDefault(x => x.Name == parts[0]) : null;
                if (linkField is not { Type: "Link" })
                    errors.Add(new(src, path, "fetchFrom harus berformat 'fieldLink.fieldTarget'"));
                else if (linkField.LinkTo is not null && defs.TryGetValue(linkField.LinkTo, out var t) &&
                         t.Fields.All(x => x.Name != parts[1]))
                    errors.Add(new(src, path, $"Field '{parts[1]}' tidak ada di {linkField.LinkTo}"));
                else fetch = (parts[0], parts[1]);
            }

            object? staticDefault = null; string? dynamicDefault = null;
            if (f.Default is { } def && def.ValueKind != JsonValueKind.Null)
            {
                if (def.ValueKind == JsonValueKind.String && def.GetString()!.StartsWith('$'))
                {
                    dynamicDefault = def.GetString();
                    if (!DynamicDefaults.Contains(dynamicDefault))
                        errors.Add(new(src, path, $"Default dinamis '{dynamicDefault}' tidak dikenal"));
                }
                else
                {
                    var n = type.Normalize(def);
                    if (!n.Ok) errors.Add(new(src, path, $"Default tidak sesuai tipe: {n.Error}"));
                    staticDefault = n.Value;
                }
            }

            if (f.LookupDialog && f.Type != "Link") errors.Add(new(src, path, "lookupDialog hanya untuk field Link"));
            if (f.AddFromLookup is not null && f.Type != "Table") errors.Add(new(src, path, "addFromLookup hanya untuk field Table"));
            if (f.Type == "Table" && f.Compute is not null) errors.Add(new(src, path, "Child table tidak dapat di-compute"));

            compiledFields.Add(new CompiledField
            {
                Def = f, Type = type, Pattern = rx, FetchFrom = fetch,
                StaticDefault = staticDefault, DynamicDefault = dynamicDefault,
                RequiredWhen = ParseExpr(f.RequiredWhen, src, $"{path}.requiredWhen", errors, roots),
                VisibleWhen = ParseExpr(f.VisibleWhen, src, $"{path}.visibleWhen", errors, roots),
                ReadOnlyWhen = ParseExpr(f.ReadOnlyWhen, src, $"{path}.readOnlyWhen", errors, roots),
                Compute = ParseExpr(f.Compute, src, $"{path}.compute", errors, roots),
            });
        }

        var byName = compiledFields.ToDictionary(f => f.Name);
        var computeOrder = TopologicalCompute(src, compiledFields, byName, errors);

        var rules = new List<CompiledRule>();
        foreach (var r in d.Rules)
        {
            var e = ParseExpr(r.Expr, src, $"rules.{r.Name}", errors, roots);
            if (r.Field is not null && !fieldNames.Contains(r.Field)) errors.Add(new(src, $"rules.{r.Name}", $"Field '{r.Field}' tidak ada"));
            if (e is not null) rules.Add(new CompiledRule { Def = r, Expr = e });
        }

        foreach (var p in d.Permissions)
        {
            if (string.IsNullOrWhiteSpace(p.Role)) errors.Add(new(src, "permissions", "Role wajib diisi"));
            foreach (var a in p.Actions.Where(a => !DocActions.All.Contains(a)))
                errors.Add(new(src, $"permissions.{p.Role}", $"Aksi '{a}' tidak dikenal"));
            if (p.Scope is not ("all" or "owner"))
            {
                var m = MatchScopeRx().Match(p.Scope);
                if (!m.Success) errors.Add(new(src, $"permissions.{p.Role}", $"Scope '{p.Scope}' tidak valid"));
                else if (!fieldNames.Contains(m.Groups[1].Value))
                    errors.Add(new(src, $"permissions.{p.Role}", $"Field scope '{m.Groups[1].Value}' tidak ada"));
            }
        }

        var lv = d.ListView;
        var columns = lv.Columns ?? d.Fields.Where(f => f.InListView).Select(f => f.Name).ToList();
        foreach (var c in columns.Concat(lv.Filters).Concat(lv.SearchFields).Concat(lv.DefaultSort.Select(s => s.Field)))
            if (!StandardFields.Queryable.Contains(c) && !(byName.TryGetValue(c, out var cf) && cf.Def.Type != "Table"))
                errors.Add(new(src, "listView", $"Field '{c}' tidak dapat dipakai di list view"));
        foreach (var s in lv.SearchFields)
            if (byName.TryGetValue(s, out var sf) && !sf.Type.Searchable)
                errors.Add(new(src, "listView.searchFields", $"Field '{s}' bukan tipe teks"));

        foreach (var op in d.Api.Operations.Where(o => o is not ("list" or "read" or "create" or "update" or "delete" or "action")))
            errors.Add(new(src, "api.operations", $"Operasi '{op}' tidak dikenal"));

        CompiledWorkflow? workflow = null;
        if (d.Workflow is not null)
        {
            if (!workflows.TryGetValue(d.Workflow, out workflow))
                errors.Add(new(src, "workflow", $"Workflow '{d.Workflow}' tidak ditemukan"));
            else if (workflow.Def.DocType != d.Name)
                errors.Add(new(src, "workflow", $"Workflow '{d.Workflow}' milik DocType lain"));
        }

        if (d.PrintFormat is { } pf)
        {
            bool Known(string f) => fieldNames.Contains(f) || StandardFields.All.Contains(f);
            foreach (var sec in pf.Sections ?? [])
                foreach (var f in sec.Fields.Where(f => !Known(f)))
                    errors.Add(new(src, "printFormat.sections", $"Field '{f}' tidak ada"));
            foreach (var (table, cols) in pf.TableColumns ?? [])
            {
                var tf = d.Fields.FirstOrDefault(x => x.Name == table);
                if (tf is not { Type: "Table" }) { errors.Add(new(src, "printFormat.tableColumns", $"'{table}' bukan field Table")); continue; }
                if (tf.ChildDocType is not null && defs.TryGetValue(tf.ChildDocType, out var ch))
                    foreach (var c in cols.Where(c => ch.Fields.All(x => x.Name != c)))
                        errors.Add(new(src, "printFormat.tableColumns", $"Field '{c}' tidak ada di {ch.Name}"));
            }
            foreach (var f in pf.TotalFields.Where(f => !fieldNames.Contains(f)))
                errors.Add(new(src, "printFormat.totalFields", $"Field '{f}' tidak ada"));
            if (pf.AmountInWordsField is { } aw && d.Fields.FirstOrDefault(x => x.Name == aw) is not { Type: "Currency" or "Float" or "Int" })
                errors.Add(new(src, "printFormat.amountInWordsField", $"'{aw}' harus field angka"));
            if (pf.Signatures.Count > 0)
            {
                var states = d.Workflow is not null && workflows.TryGetValue(d.Workflow, out var w) ? w.States.Keys.ToHashSet() : null;
                if (states is null) errors.Add(new(src, "printFormat.signatures", "Tanda tangan elektronik membutuhkan workflow"));
                else
                    foreach (var sg in pf.Signatures)
                        foreach (var st in sg.ToStates.Append(sg.FromState).OfType<string>().Where(st => !states.Contains(st)))
                            errors.Add(new(src, $"printFormat.signatures.{sg.Label}", $"State '{st}' tidak ada di workflow"));
            }
        }

        if (!d.IsChild && d.Naming.Pattern.StartsWith("field:"))
        {
            // Nama dokumen = nilai field (mis. kode departemen). Field harus Data dan wajib.
            var nf = d.Fields.FirstOrDefault(x => x.Name == d.Naming.Pattern["field:".Length..]);
            if (nf is not { Type: "Data" } || nf.RequiredWhen != "true")
                errors.Add(new(src, "naming.pattern", "Penamaan field:<nama> butuh field Data yang selalu wajib"));
        }
        else if (!d.IsChild && !d.Naming.Pattern.Contains("{#"))
            errors.Add(new(src, "naming.pattern", "Pola nama harus memuat nomor seri, mis. {#####}, atau field:<nama_field>"));

        if (errors.Count > startErrors) return null;

        return new CompiledDocType
        {
            Def = d, Fields = compiledFields, ByName = byName, ComputeOrder = computeOrder, Rules = rules,
            Layout = BuildLayout(compiledFields), Workflow = workflow, ListColumns = columns,
        };
    }

    private static List<CompiledField> TopologicalCompute(string src, List<CompiledField> fields,
        Dictionary<string, CompiledField> byName, List<MetadataError> errors)
    {
        var order = new List<CompiledField>();
        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done
        bool Visit(CompiledField f, Stack<string> trail)
        {
            if (state.TryGetValue(f.Name, out var s)) {
                if (s == 2) return true;
                errors.Add(new(src, $"fields.{f.Name}.compute", $"Dependensi melingkar: {string.Join(" → ", trail.Reverse().Append(f.Name))}"));
                return false;
            }
            state[f.Name] = 1; trail.Push(f.Name);
            foreach (var dep in f.Compute!.RootNames)
                if (byName.TryGetValue(dep, out var df) && df.Compute is not null && !Visit(df, trail)) return false;
            trail.Pop(); state[f.Name] = 2; order.Add(f);
            return true;
        }
        foreach (var f in fields.Where(f => f.Compute is not null))
            if (!Visit(f, new Stack<string>())) break;
        return order;
    }

    private static List<LayoutTab> BuildLayout(List<CompiledField> fields)
    {
        // Urutan kemunculan field menentukan urutan tab/section; kolom 1..3 per section.
        var tabs = new List<LayoutTab>();
        foreach (var tabGroup in fields.GroupBy(f => f.Def.Tab ?? "Detail"))
        {
            var sections = tabGroup.GroupBy(f => f.Def.Section)
                .Select(sg => new LayoutSection(sg.Key,
                    sg.GroupBy(f => Math.Clamp(f.Def.Column, 1, 3)).OrderBy(g => g.Key)
                      .Select(cg => new LayoutColumn(cg.ToList())).ToList()))
                .ToList();
            tabs.Add(new LayoutTab(tabGroup.Key, sections));
        }
        return tabs;
    }

    private CompiledWorkflow? CompileWorkflow(WorkflowDef wf, Dictionary<string, DocTypeDef> defs, List<MetadataError> errors)
    {
        var src = $"Workflow {wf.Name}@v{wf.Version}";
        var start = errors.Count;
        DocTypeDef? docType = null;
        if (!string.IsNullOrWhiteSpace(wf.DocType) && !defs.TryGetValue(wf.DocType, out docType))
        {
            errors.Add(new(src, "docType", $"DocType '{wf.DocType}' tidak ditemukan"));
            return null;
        }
        if (docType?.IsChild == true)
            errors.Add(new(src, "docType", "Workflow hanya dapat dikaitkan ke DocType utama"));
        if (!System.Text.RegularExpressions.Regex.IsMatch(wf.Name, "^[A-Za-z][A-Za-z0-9_]{1,63}$"))
            errors.Add(new(src, "name", "Nama workflow harus huruf/angka tanpa spasi, mis. PengajuanApproval"));
        foreach (var dup in wf.Transitions.GroupBy(t => (t.From, t.Action)).Where(g => g.Count() > 1))
            errors.Add(new(src, $"transitions.{dup.Key.From}->{dup.Key.Action}", $"Aksi \"{dup.Key.Action}\" dari state \"{dup.Key.From}\" didefinisikan lebih dari sekali"));
        if (wf.States.Any(x => string.IsNullOrWhiteSpace(x.Name))) errors.Add(new(src, "states", "Nama state wajib diisi"));
        var states = new Dictionary<string, WorkflowStateDef>();
        foreach (var s in wf.States)
        {
            if (!states.TryAdd(s.Name, s)) errors.Add(new(src, $"states.{s.Name}", "State duplikat"));
            if (s.DocStatus is < 0 or > 2) errors.Add(new(src, $"states.{s.Name}", "docStatus harus 0, 1, atau 2"));
        }
        if (!states.TryGetValue(wf.InitialState, out var init) || init.DocStatus != 0)
            errors.Add(new(src, "initialState", "Initial state harus ada dan ber-docStatus 0"));

        var roots = new HashSet<string>(docType?.Fields.Select(f => f.Name) ?? []) { "user" };
        roots.UnionWith(StandardFields.All);
        var transitions = new List<CompiledTransition>();
        foreach (var t in wf.Transitions)
        {
            var path = $"transitions.{t.From}->{t.To}";
            if (!states.ContainsKey(t.From)) errors.Add(new(src, path, $"State asal '{t.From}' tidak ada"));
            if (!states.ContainsKey(t.To)) errors.Add(new(src, path, $"State tujuan '{t.To}' tidak ada"));
            if (string.IsNullOrWhiteSpace(t.Action)) errors.Add(new(src, path, "Action wajib diisi"));
            if (t.AllowedRoles.Count == 0) errors.Add(new(src, path, "Minimal satu role diizinkan"));
            if (states.TryGetValue(t.From, out var fs) && states.TryGetValue(t.To, out var ts) && ts.DocStatus < fs.DocStatus)
                errors.Add(new(src, path, "Transisi tidak boleh menurunkan docStatus; gunakan amend"));
            transitions.Add(new CompiledTransition { Def = t, Condition = ParseExpr(t.Condition, src, $"{path}.condition", errors, roots, validateRoots: docType is not null) });
        }
        if (errors.Count > start) return null;
        return new CompiledWorkflow { Def = wf, States = states, Transitions = transitions };
    }
}

// ---------- Diff & klasifikasi perubahan (RFC §15) ----------

public enum ChangeRisk { Safe = 0, NeedsTransform = 1, Risky = 2 }

public sealed record MetadataChange(ChangeRisk Risk, string Path, string Description);

public static class MetadataDiff
{
    public static IReadOnlyList<MetadataChange> Compare(DocTypeDef? oldDef, DocTypeDef newDef)
    {
        var changes = new List<MetadataChange>();
        if (oldDef is null) { changes.Add(new(ChangeRisk.Safe, newDef.Name, "DocType baru")); return changes; }

        var oldFields = oldDef.Fields.ToDictionary(f => f.Name);
        var newFields = newDef.Fields.ToDictionary(f => f.Name);

        foreach (var (name, of) in oldFields)
        {
            if (!newFields.TryGetValue(name, out var nf))
            {
                changes.Add(new(ChangeRisk.Risky, $"fields.{name}", "Field dihapus — data lama tidak lagi terbaca"));
                continue;
            }
            if (of.Type != nf.Type) changes.Add(new(ChangeRisk.NeedsTransform, $"fields.{name}.type", $"Tipe berubah {of.Type} → {nf.Type}"));
            if (of.LinkTo != nf.LinkTo) changes.Add(new(ChangeRisk.Risky, $"fields.{name}.linkTo", $"Relasi berubah {of.LinkTo} → {nf.LinkTo}"));
            if (of.ChildDocType != nf.ChildDocType) changes.Add(new(ChangeRisk.Risky, $"fields.{name}.childDocType", "Child table berubah"));
            var removedOptions = (of.Options ?? []).Except(nf.Options ?? []).ToList();
            if (removedOptions.Count > 0) changes.Add(new(ChangeRisk.NeedsTransform, $"fields.{name}.options", $"Opsi dihapus: {string.Join(", ", removedOptions)}"));
            if (of.RequiredWhen != nf.RequiredWhen || of.Min != nf.Min || of.Max != nf.Max || of.Pattern != nf.Pattern)
                changes.Add(new(ChangeRisk.NeedsTransform, $"fields.{name}", "Aturan validasi berubah — dokumen lama mungkin tidak lolos saat disimpan ulang"));
            if (of.Compute != nf.Compute) changes.Add(new(ChangeRisk.NeedsTransform, $"fields.{name}.compute", "Rumus compute berubah"));
            if (JsonSerializer.Serialize(of.Default) != JsonSerializer.Serialize(nf.Default))
                changes.Add(new(ChangeRisk.NeedsTransform, $"fields.{name}.default", "Nilai default berubah"));
            if (of.PermLevel != nf.PermLevel) changes.Add(new(ChangeRisk.Risky, $"fields.{name}.permLevel", "Level permission field berubah"));
            if (of.Label != nf.Label || of.Description != nf.Description || of.Tab != nf.Tab || of.Section != nf.Section || of.Column != nf.Column || of.InListView != nf.InListView || of.VisibleWhen != nf.VisibleWhen)
                changes.Add(new(ChangeRisk.Safe, $"fields.{name}", "Tampilan/label berubah"));
        }
        foreach (var (name, nf) in newFields.Where(kv => !oldFields.ContainsKey(kv.Key)))
            changes.Add(new(string.IsNullOrEmpty(nf.RequiredWhen) || nf.Default is not null ? ChangeRisk.Safe : ChangeRisk.NeedsTransform,
                $"fields.{name}", nf.RequiredWhen is null ? "Field baru" : "Field wajib baru — dokumen lama belum memiliki nilai"));

        if (oldDef.Fields.Select(f => f.Name).Intersect(newFields.Keys).SequenceEqual(newDef.Fields.Select(f => f.Name).Intersect(oldFields.Keys)) is false)
            changes.Add(new(ChangeRisk.Safe, "fields", "Urutan field berubah"));
        if (JsonSerializer.Serialize(oldDef.Permissions) != JsonSerializer.Serialize(newDef.Permissions))
            changes.Add(new(ChangeRisk.Risky, "permissions", "Permission berubah — perlu review keamanan"));
        if (JsonSerializer.Serialize(oldDef.Rules) != JsonSerializer.Serialize(newDef.Rules))
            changes.Add(new(ChangeRisk.NeedsTransform, "rules", "Aturan lintas-field berubah"));
        if (oldDef.Workflow != newDef.Workflow) changes.Add(new(ChangeRisk.Risky, "workflow", "Workflow diganti"));
        if (JsonSerializer.Serialize(oldDef.ListView) != JsonSerializer.Serialize(newDef.ListView))
            changes.Add(new(ChangeRisk.Safe, "listView", "Konfigurasi list view berubah"));
        if (oldDef.Title != newDef.Title) changes.Add(new(ChangeRisk.Safe, "title", "Judul berubah"));
        if (JsonSerializer.Serialize(oldDef.Api) != JsonSerializer.Serialize(newDef.Api))
            changes.Add(new(ChangeRisk.Risky, "api", "Kebijakan API berubah"));
        return changes;
    }
}

// ---------- Registry berversi ----------

public enum MetadataStatus { Published, Deprecated }

public static class WorkflowDiff
{
    public static IReadOnlyList<MetadataChange> Compare(WorkflowDef? oldWf, WorkflowDef newWf)
    {
        var changes = new List<MetadataChange>();
        if (oldWf is null) { changes.Add(new(ChangeRisk.Safe, newWf.Name, "Workflow baru")); return changes; }
        var oldStates = oldWf.States.ToDictionary(x => x.Name);
        var newStates = newWf.States.ToDictionary(x => x.Name);
        foreach (var (name, os) in oldStates)
        {
            if (!newStates.TryGetValue(name, out var ns))
                changes.Add(new(ChangeRisk.Risky, $"states.{name}", $"State \"{name}\" dihapus — dokumen yang sedang di state ini tidak bisa diproses lagi"));
            else
            {
                if (os.DocStatus != ns.DocStatus) changes.Add(new(ChangeRisk.Risky, $"states.{name}.docStatus", $"Status dokumen state \"{name}\" berubah {os.DocStatus} → {ns.DocStatus}"));
                if (os.Tone != ns.Tone) changes.Add(new(ChangeRisk.Safe, $"states.{name}.tone", "Warna status berubah"));
            }
        }
        foreach (var name in newStates.Keys.Where(n => !oldStates.ContainsKey(n)))
            changes.Add(new(ChangeRisk.Safe, $"states.{name}", $"State baru \"{name}\""));
        if (oldWf.InitialState != newWf.InitialState)
            changes.Add(new(ChangeRisk.NeedsTransform, "initialState", $"Dokumen baru mulai dari \"{newWf.InitialState}\" (sebelumnya \"{oldWf.InitialState}\")"));

        static string Key(WorkflowTransitionDef t) => $"{t.From} → {t.Action}";
        var oldT = oldWf.Transitions.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var newT = newWf.Transitions.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, ot) in oldT)
        {
            if (!newT.TryGetValue(key, out var nt)) { changes.Add(new(ChangeRisk.NeedsTransform, $"transitions.{key}", $"Transisi \"{ot.Action}\" dari \"{ot.From}\" dihapus")); continue; }
            if (ot.To != nt.To) changes.Add(new(ChangeRisk.Risky, $"transitions.{key}.to", $"Tujuan \"{ot.Action}\" berubah {ot.To} → {nt.To}"));
            if (!ot.AllowedRoles.Order().SequenceEqual(nt.AllowedRoles.Order()))
                changes.Add(new(ChangeRisk.Risky, $"transitions.{key}.allowedRoles", $"Role \"{ot.Action}\" berubah — perlu review keamanan"));
            if ((ot.Condition ?? "") != (nt.Condition ?? ""))
                changes.Add(new(ChangeRisk.Risky, $"transitions.{key}.condition", $"Kondisi \"{ot.Action}\" berubah — perlu review keamanan"));
            if (ot.RequireComment != nt.RequireComment) changes.Add(new(ChangeRisk.Safe, $"transitions.{key}.requireComment", "Kewajiban komentar berubah"));
        }
        foreach (var (key, nt) in newT.Where(kv => !oldT.ContainsKey(kv.Key)))
            changes.Add(new(ChangeRisk.Safe, $"transitions.{key}", $"Transisi baru \"{nt.Action}\": {nt.From} → {nt.To}"));
        return changes;
    }
}

public sealed record WorkflowVersion(string Name, string? DocType, int Version, WorkflowDef Def, MetadataStatus Status,
    string PublishedBy, DateTimeOffset PublishedAt, IReadOnlyList<MetadataChange> Changes);

public sealed record MetadataVersion(string DocType, int Version, DocTypeDef Def, MetadataStatus Status,
    string PublishedBy, DateTimeOffset PublishedAt, IReadOnlyList<MetadataChange> Changes);

public sealed record PublishPreview(bool Valid, IReadOnlyList<MetadataError> Errors, IReadOnlyList<MetadataChange> Changes, ChangeRisk Risk);

/// <summary>Penyimpanan versi metadata permanen. Save menandai versi published sebelumnya sebagai Deprecated.</summary>
public interface IMetadataVersionStore
{
    IReadOnlyList<MetadataVersion> LoadAll();
    void Save(MetadataVersion version);
    IReadOnlyList<WorkflowVersion> LoadAllWorkflows() => [];
    void SaveWorkflow(WorkflowVersion version) { }
}

public interface IMetadataProvider
{
    MetadataSnapshot Current { get; }
    IReadOnlyList<MetadataVersion> History(string docType);
}

public sealed class MetadataRegistry : IMetadataProvider
{
    private readonly MetadataCompiler _compiler;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<MetadataVersion>> _history = new();
    private readonly Dictionary<string, List<WorkflowVersion>> _wfHistory = new();
    private List<WorkflowDef> _workflows = [];
    private MetadataSnapshot _current;

    private readonly IMetadataVersionStore? _store;

    /// <param name="docTypes">Metadata dari file (sumber awal / hasil deploy).</param>
    /// <param name="store">Opsional: penyimpanan versi permanen (mis. PostgreSQL). Tanpa store, versi hasil publish hilang saat restart.</param>
    public MetadataRegistry(MetadataCompiler compiler, IEnumerable<DocTypeDef> docTypes, IEnumerable<WorkflowDef> workflows, TimeProvider clock,
        IMetadataVersionStore? store = null)
    {
        _compiler = compiler;
        _store = store;
        Clock = clock;
        var fromFiles = docTypes.ToDictionary(d => d.Name);

        // Workflow: aturan sama dengan DocType — versi tersimpan menang, kecuali file membawa versi lebih baru (deploy).
        foreach (var g in (store?.LoadAllWorkflows() ?? []).GroupBy(v => v.Name))
            _wfHistory[g.Key] = g.OrderBy(v => v.Version).ToList();
        var wfFiles = workflows.ToDictionary(w => w.Name);
        var activeWf = new Dictionary<string, WorkflowDef>();
        var autoWf = new List<WorkflowVersion>();
        foreach (var name in wfFiles.Keys.Union(_wfHistory.Keys))
        {
            var stored = _wfHistory.TryGetValue(name, out var h) ? h.LastOrDefault(v => v.Status == MetadataStatus.Published) : null;
            wfFiles.TryGetValue(name, out var file);
            if (file is not null && (stored is null || file.Version > stored.Version))
            {
                activeWf[name] = file;
                autoWf.Add(new WorkflowVersion(name, file.DocType, file.Version, file, MetadataStatus.Published,
                    stored is null ? "system" : "deploy", clock.GetUtcNow(), WorkflowDiff.Compare(stored?.Def, file)));
            }
            else if (stored is not null) activeWf[name] = stored.Def;
        }
        _workflows = activeWf.Values.ToList();

        // Riwayat tersimpan (bila ada store)
        foreach (var g in (store?.LoadAll() ?? []).GroupBy(v => v.DocType))
            _history[g.Key] = g.OrderBy(v => v.Version).ToList();

        // Versi aktif: published di store, kecuali file membawa versi yang lebih baru (deploy) → dipublikasikan otomatis.
        var active = new Dictionary<string, DocTypeDef>();
        var autoPublish = new List<MetadataVersion>();
        foreach (var name in fromFiles.Keys.Union(_history.Keys))
        {
            var stored = _history.TryGetValue(name, out var h) ? h.LastOrDefault(v => v.Status == MetadataStatus.Published) : null;
            fromFiles.TryGetValue(name, out var file);
            if (file is not null && (stored is null || file.Version > stored.Version))
            {
                active[name] = file;
                autoPublish.Add(new MetadataVersion(name, file.Version, file, MetadataStatus.Published,
                    stored is null ? "system" : "deploy", clock.GetUtcNow(), MetadataDiff.Compare(stored?.Def, file)));
            }
            else if (stored is not null) active[name] = stored.Def;
        }

        _current = compiler.Compile(active.Values.ToList(), _workflows);

        foreach (var v in autoWf)
        {
            store?.SaveWorkflow(v);
            DeprecateWorkflow(v.Name);
            (_wfHistory.TryGetValue(v.Name, out var wh) ? wh : _wfHistory[v.Name] = []).Add(v);
        }

        foreach (var v in autoPublish)
        {
            store?.Save(v);
            if (_history.TryGetValue(v.DocType, out var hist))
                for (var i = 0; i < hist.Count; i++)
                    if (hist[i].Status == MetadataStatus.Published) hist[i] = hist[i] with { Status = MetadataStatus.Deprecated };
            (_history.TryGetValue(v.DocType, out var hh) ? hh : _history[v.DocType] = []).Add(v);
        }
    }

    private TimeProvider Clock { get; }

    /// <summary>Dipicu setelah DocType atau workflow dipublikasikan (mis. untuk menyinkronkan view pelaporan).</summary>
    public event Action<MetadataSnapshot>? Published;
    public MetadataSnapshot Current => Volatile.Read(ref _current);

    // ---------------- Workflow ----------------

    public IReadOnlyList<WorkflowDef> Workflows { get { lock (_gate) return _workflows.ToList(); } }

    public IReadOnlyList<WorkflowVersion> WorkflowHistory(string name)
    {
        lock (_gate) return _wfHistory.TryGetValue(name, out var h) ? h.ToList() : [];
    }

    private void DeprecateWorkflow(string name)
    {
        if (_wfHistory.TryGetValue(name, out var h))
            for (var i = 0; i < h.Count; i++)
                if (h[i].Status == MetadataStatus.Published) h[i] = h[i] with { Status = MetadataStatus.Deprecated };
    }

    /// <summary>
    /// Set metadata bila draft workflow diterapkan. Dengan <paramref name="attach"/>, DocType pemilik workflow
    /// ikut diperbarui (versi +1, properti Workflow diisi) agar keduanya aktif bersama secara atomik.
    /// </summary>
    private (List<DocTypeDef> Docs, List<WorkflowDef> Wfs, DocTypeDef? Attached) WithWorkflow(WorkflowDef draft, bool attach)
    {
        var wfs = _workflows.Where(w => w.Name != draft.Name).Append(draft).ToList();
        var docs = _current.DocTypes.Values.Select(c => c.Def).ToList();
        DocTypeDef? attached = null;
        var owner = string.IsNullOrWhiteSpace(draft.DocType) ? null : docs.FirstOrDefault(d => d.Name == draft.DocType);
        if (attach && owner is not null && owner.Workflow != draft.Name)
        {
            attached = MetadataJson.ParseDocType(MetadataJson.Serialize(owner));
            attached.Workflow = draft.Name;
            attached.Version = owner.Version + 1;
            docs = docs.Where(d => d.Name != owner.Name).Append(attached).ToList();
        }
        return (docs, wfs, attached);
    }

    public (MetadataSnapshot? Snapshot, IReadOnlyList<MetadataError> Errors) CompileWorkflowDraft(WorkflowDef draft, bool attach)
    {
        lock (_gate)
        {
            if (attach && string.IsNullOrWhiteSpace(draft.DocType))
                return (null, [new(draft.Name, "docType", "Pilih DocType sebelum mengaitkan workflow")]);
            var (docs, wfs, _) = WithWorkflow(draft, attach);
            try { return (_compiler.Compile(docs, wfs), []); }
            catch (MetadataCompilationException ex) { return (null, ex.Errors); }
        }
    }

    private (List<MetadataError> Errors, List<MetadataChange> Changes, MetadataSnapshot? Snapshot, DocTypeDef? Attached) TryWorkflow(WorkflowDef draft, bool attach)
    {
        var errors = new List<MetadataError>();
        var existing = _workflows.FirstOrDefault(w => w.Name == draft.Name);
        if (existing is not null && draft.Version <= existing.Version)
            errors.Add(new(draft.Name, "version", $"Versi harus lebih besar dari versi aktif ({existing.Version})"));
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.DocType) && existing.DocType != draft.DocType)
            errors.Add(new(draft.Name, "docType", "DocType pemilik workflow tidak boleh diganti; buat workflow baru"));
        if (attach && string.IsNullOrWhiteSpace(draft.DocType))
            errors.Add(new(draft.Name, "docType", "Pilih DocType sebelum mengaitkan workflow"));
        var (docs, wfs, attached) = WithWorkflow(draft, attach);
        var changes = WorkflowDiff.Compare(existing, draft).ToList();
        if (attached is not null)
            changes.AddRange(MetadataDiff.Compare(_current.Get(draft.DocType).Def, attached)
                .Select(c => c with { Path = $"{draft.DocType}.{c.Path}", Description = $"{draft.DocType}: {c.Description}" }));
        MetadataSnapshot? snap = null;
        try { if (errors.Count == 0) snap = _compiler.Compile(docs, wfs); }
        catch (MetadataCompilationException ex) { errors.AddRange(ex.Errors); }
        return (errors, changes, snap, attached);
    }

    public PublishPreview PreviewWorkflow(WorkflowDef draft, bool attach)
    {
        lock (_gate)
        {
            var (errors, changes, _, _) = TryWorkflow(draft, attach);
            return new PublishPreview(errors.Count == 0, errors, changes, changes.Count == 0 ? ChangeRisk.Safe : changes.Max(c => c.Risk));
        }
    }

    public WorkflowVersion PublishWorkflow(WorkflowDef draft, string publishedBy, bool acknowledgeRisk, bool attach)
    {
        lock (_gate)
        {
            var (errors, changes, snap, attached) = TryWorkflow(draft, attach);
            if (errors.Count > 0) throw new MetadataCompilationException(errors);
            if (changes.Any(c => c.Risk == ChangeRisk.Risky) && !acknowledgeRisk)
                throw new InvalidOperationException("Perubahan berisiko harus dikonfirmasi sebelum publish");

            var now = Clock.GetUtcNow();
            var wv = new WorkflowVersion(draft.Name, draft.DocType, draft.Version, draft, MetadataStatus.Published, publishedBy, now,
                WorkflowDiff.Compare(_workflows.FirstOrDefault(w => w.Name == draft.Name), draft));
            _store?.SaveWorkflow(wv);
            DeprecateWorkflow(draft.Name);
            (_wfHistory.TryGetValue(draft.Name, out var wh) ? wh : _wfHistory[draft.Name] = []).Add(wv);

            if (attached is not null)
            {
                var mv = new MetadataVersion(attached.Name, attached.Version, attached, MetadataStatus.Published, publishedBy, now,
                    MetadataDiff.Compare(_current.Get(attached.Name).Def, attached));
                _store?.Save(mv);
                if (_history.TryGetValue(attached.Name, out var hist))
                    for (var i = 0; i < hist.Count; i++)
                        if (hist[i].Status == MetadataStatus.Published) hist[i] = hist[i] with { Status = MetadataStatus.Deprecated };
                (_history.TryGetValue(attached.Name, out var hh) ? hh : _history[attached.Name] = []).Add(mv);
            }

            _workflows = _workflows.Where(w => w.Name != draft.Name).Append(draft).ToList();
            Volatile.Write(ref _current, snap!);
            Published?.Invoke(snap!);
            return wv;
        }
    }

    public IReadOnlyList<MetadataVersion> History(string docType)
    {
        lock (_gate) return _history.TryGetValue(docType, out var h) ? h.ToList() : [];
    }

    public PublishPreview Preview(DocTypeDef draft)
    {
        lock (_gate)
        {
            var (errors, changes) = TryCompileWith(draft, out _);
            var risk = changes.Count == 0 ? ChangeRisk.Safe : changes.Max(c => c.Risk);
            return new PublishPreview(errors.Count == 0, errors, changes, risk);
        }
    }

    /// <summary>
    /// Mengompilasi draft bersama metadata aktif lainnya tanpa mempublikasikannya (untuk validasi langsung dan
    /// pratinjau di editor). Berbeda dengan Preview, versi draft tidak diwajibkan lebih tinggi.
    /// </summary>
    public (MetadataSnapshot? Snapshot, IReadOnlyList<MetadataError> Errors) CompileDraft(DocTypeDef draft)
    {
        lock (_gate)
        {
            var defs = _current.DocTypes.Values.Select(c => c.Def).Where(d => d.Name != draft.Name).Append(draft).ToList();
            try { return (_compiler.Compile(defs, _workflows), []); }
            catch (MetadataCompilationException ex) { return (null, ex.Errors); }
        }
    }

    /// <summary>Publish versi baru secara immutable. Perubahan berisiko butuh konfirmasi eksplisit.</summary>
    public MetadataVersion Publish(DocTypeDef draft, string publishedBy, bool acknowledgeRisk)
    {
        lock (_gate)
        {
            var (errors, changes) = TryCompileWith(draft, out var snapshot);
            if (errors.Count > 0) throw new MetadataCompilationException(errors);
            if (changes.Any(c => c.Risk == ChangeRisk.Risky) && !acknowledgeRisk)
                throw new InvalidOperationException("Perubahan berisiko harus dikonfirmasi sebelum publish");

            var version = new MetadataVersion(draft.Name, draft.Version, draft, MetadataStatus.Published, publishedBy, Clock.GetUtcNow(), changes);
            _store?.Save(version); // simpan dulu; bila gagal, versi aktif tidak berubah

            if (_history.TryGetValue(draft.Name, out var hist))
                for (var i = 0; i < hist.Count; i++)
                    if (hist[i].Status == MetadataStatus.Published) hist[i] = hist[i] with { Status = MetadataStatus.Deprecated };

            (_history.TryGetValue(draft.Name, out var h) ? h : _history[draft.Name] = []).Add(version);
            Volatile.Write(ref _current, snapshot!);
            Published?.Invoke(snapshot!);
            return version;
        }
    }

    private (List<MetadataError> Errors, IReadOnlyList<MetadataChange> Changes) TryCompileWith(DocTypeDef draft, out MetadataSnapshot? snapshot)
    {
        snapshot = null;
        var current = _current.DocTypes.TryGetValue(draft.Name, out var existing) ? existing.Def : null;
        var errors = new List<MetadataError>();
        if (current is not null && draft.Version <= current.Version)
            errors.Add(new(draft.Name, "version", $"Versi harus lebih besar dari versi aktif ({current.Version})"));
        var changes = MetadataDiff.Compare(current, draft);
        var defs = _current.DocTypes.Values.Select(c => c.Def).Where(d => d.Name != draft.Name).Append(draft).ToList();
        try { if (errors.Count == 0) snapshot = _compiler.Compile(defs, _workflows); }
        catch (MetadataCompilationException ex) { errors.AddRange(ex.Errors); }
        return (errors, changes);
    }
}
