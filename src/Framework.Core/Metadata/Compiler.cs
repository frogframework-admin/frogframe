using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Expressions;
using Framework.Persistence;

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

public sealed class CompiledDocHook
{
    public required DocHookDef Def { get; init; }
    public object? ConditionValue { get; init; }
}

public static class DocActions
{
    public const string Read = "read", Create = "create", Write = "write", Delete = "delete",
        Submit = "submit", Cancel = "cancel", Amend = "amend", Export = "export", Report = "report";
    public static readonly ImmutableHashSet<string> All = [Read, Create, Write, Delete, Submit, Cancel, Amend, Export, Report];
}

/// <summary>
/// Daftar yang diizinkan untuk diacu metadata saat publish: nama role master dan kode modul master tenant.
/// null = tidak diperiksa (startup).
/// </summary>
public sealed record MetadataScope(IReadOnlySet<string> Roles, IReadOnlySet<string> Modules);

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
    public bool NoCopy => Def.NoCopy;
    public ParsedExpression? RequiredWhen { get; init; }
    public ParsedExpression? VisibleWhen { get; init; }
    public ParsedExpression? ReadOnlyWhen { get; init; }
    public ParsedExpression? Compute { get; init; }
    public (string LinkField, string TargetField)? FetchFrom { get; init; }
    public Regex? Pattern { get; init; }
    public object? StaticDefault { get; init; }
    public string? DynamicDefault { get; init; }
    public bool IsDerived => Compute is not null || FetchFrom is not null;
    /// <summary>Layout-only field (HTML, Button, Heading): no value, never stored.</summary>
    public bool IsLayout => FieldKinds.IsLayout(Def.Type);
}

public sealed record LayoutColumn(IReadOnlyList<CompiledField> Fields);
public sealed record LayoutSection(string? Title, IReadOnlyList<LayoutColumn> Columns)
{
    /// <summary>Name of the tab inside the section when this part is one (fields with <c>subTab</c>); null for a normal section.</summary>
    public string? SubTab { get; init; }
    /// <summary>Same value for all inner tabs of one section (key for the selected inner tab).</summary>
    public string? SubTabGroup { get; init; }
    /// <summary>All inner tabs of the section, in order (shown as the tab strip).</summary>
    public IReadOnlyList<string> SubTabs { get; init; } = [];
    /// <summary>Title of the section that holds the inner tabs.</summary>
    public string? ParentTitle { get; init; }
    /// <summary>True on inner tabs of a section without own (non-tab) fields: the strip carries the section title.</summary>
    public bool ShowParentTitle { get; init; }
}
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
    public bool AllowAutoRepeat => Def.AllowAutoRepeat;
    public required IReadOnlyList<CompiledField> Fields { get; init; }
    public required IReadOnlyDictionary<string, CompiledField> ByName { get; init; }
    /// <summary>Field compute dalam urutan topologis (dependensi lebih dulu).</summary>
    public required IReadOnlyList<CompiledField> ComputeOrder { get; init; }
    public required IReadOnlyList<CompiledRule> Rules { get; init; }
    public required IReadOnlyList<CompiledDocHook> Hooks { get; init; }
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
    public required ImmutableDictionary<string, IReadOnlyList<DocTypeLinkDef>> IncomingLinks { get; init; }

    public CompiledDocType Get(string docType) =>
        DocTypes.TryGetValue(docType, out var d) ? d : throw new KeyNotFoundException($"DocType '{docType}' not found");
    public bool TryGet(string docType, out CompiledDocType meta) => DocTypes.TryGetValue(docType, out meta!);
}

// ---------- Compiler ----------

public sealed partial class MetadataCompiler(IFieldTypeRegistry fieldTypes, FunctionRegistry functions)
{
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]{0,63}$")] private static partial Regex DocTypeNameRx();
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}$")] private static partial Regex FieldNameRx();
    [GeneratedRegex(@"^match:([a-z][a-z0-9_]*)=([a-z][a-z0-9_]*)$")] private static partial Regex MatchScopeRx();
    private static readonly string[] DynamicDefaults = ["$today", "$user", "$employee"];

    private readonly object _roleGate = new();
    private MetadataScope? _scope;

    /// <summary>
    /// Kompilasi metadata. <paramref name="scope"/> = daftar role master tenant; null = tidak diperiksa
    /// (dipakai saat startup). Jika diisi, setiap role di permission DocType dan transisi workflow harus ada di daftar.
    /// </summary>
    public MetadataSnapshot Compile(IReadOnlyCollection<DocTypeDef> docTypes, IReadOnlyCollection<WorkflowDef> workflows, MetadataScope? scope = null)
    {
        lock (_roleGate)
        {
            _scope = scope;
            try { return CompileCore(docTypes, workflows); }
            finally { _scope = null; }
        }
    }

    /// <summary>Role yang dipakai metadata harus terdaftar di master role (bila daftar diberikan).</summary>
    private void CheckRole(string src, string path, string? role, List<MetadataError> errors)
    {
        if (_scope is null || string.IsNullOrWhiteSpace(role)) return;
        if (!_scope.Roles.Contains(role))
            errors.Add(new(src, path, $"Role '{role}' belum ada di master role. Buat dulu di Role Admin, atau pilih role yang tersedia."));
    }

    private MetadataSnapshot CompileCore(IReadOnlyCollection<DocTypeDef> docTypes, IReadOnlyCollection<WorkflowDef> workflows)
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

        var incoming = new Dictionary<string, List<DocTypeLinkDef>>(StringComparer.Ordinal);
        foreach (var d in defs.Values)
        {
            if (d.IsChild) continue;
            foreach (var f in d.Fields)
            {
                if (f.Type == "Link" && !string.IsNullOrEmpty(f.LinkTo) && !f.IgnoreLinks)
                {
                    if (!incoming.TryGetValue(f.LinkTo, out var list))
                        incoming[f.LinkTo] = list = [];
                    list.Add(new DocTypeLinkDef(d.Name, f.Name, string.IsNullOrEmpty(f.Label) ? f.Name : f.Label, false, null));
                }
                else if (f.Type == "Table" && !string.IsNullOrEmpty(f.ChildDocType) && defs.TryGetValue(f.ChildDocType, out var childDt))
                {
                    foreach (var cf in childDt.Fields)
                    {
                        if (cf.Type == "Link" && !string.IsNullOrEmpty(cf.LinkTo) && !cf.IgnoreLinks)
                        {
                            if (!incoming.TryGetValue(cf.LinkTo, out var list))
                                incoming[cf.LinkTo] = list = [];
                            list.Add(new DocTypeLinkDef(d.Name, cf.Name, string.IsNullOrEmpty(cf.Label) ? cf.Name : cf.Label, true, f.Name));
                        }
                    }
                }
            }
        }

        return new MetadataSnapshot
        {
            DocTypes = compiled.ToImmutableDictionary(),
            Workflows = compiledWorkflows.ToImmutableDictionary(),
            IncomingLinks = incoming.ToImmutableDictionary(kv => kv.Key, kv => (IReadOnlyList<DocTypeLinkDef>)kv.Value)
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
                    else
                        foreach (var lf in f.LinkFilters)
                        {
                            if (!LinkFilters.Ops.Contains(lf.Op))
                                errors.Add(new(src, path, $"Filter Link: operator '{lf.Op}' tidak didukung (pakai =, !=, in)"));
                            if (!StandardFields.All.Contains(lf.Field) && target.Fields.All(tf => tf.Name != lf.Field))
                                errors.Add(new(src, path, $"Filter Link: field '{lf.Field}' tidak ada di {target.Name}"));
                            if (lf.Op == "in" && lf.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                                errors.Add(new(src, path, "Filter Link: operator 'in' butuh nilai berupa array"));
                        }
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
                case "TableMultiSelect":
                    if (string.IsNullOrEmpty(f.LinkTo) || !defs.TryGetValue(f.LinkTo, out var multiTarget))
                        errors.Add(new(src, path, $"linkTo '{f.LinkTo}' tidak ditemukan"));
                    else if (multiTarget.IsChild)
                        errors.Add(new(src, path, "TableMultiSelect tidak boleh menunjuk child DocType"));
                    break;
                case "DynamicLink":
                    var typeField = string.IsNullOrEmpty(f.LinkToField) ? null : d.Fields.FirstOrDefault(x => x.Name == f.LinkToField);
                    if (typeField is null)
                        errors.Add(new(src, path, "linkToField harus menunjuk field di DocType ini yang berisi nama DocType tujuan"));
                    else if (typeField.Type is not ("Select" or "Data" or "ReadOnly"))
                        errors.Add(new(src, path, $"Field '{typeField.Name}' (linkToField) harus bertipe Select atau Data"));
                    else if (typeField.Type == "Select")
                        foreach (var option in typeField.Options ?? [])
                            if (!defs.TryGetValue(option, out var optionDef) || optionDef.IsChild)
                                errors.Add(new(src, path, $"Pilihan '{option}' di field {typeField.Name} bukan DocType"));
                    break;
                case "Rating":
                    if (f.Max is { } ratingMax && (ratingMax < 1 || ratingMax > 10 || ratingMax != Math.Truncate(ratingMax)))
                        errors.Add(new(src, path, "Maksimum rating harus bilangan bulat 1 sampai 10"));
                    break;
                case "Barcode":
                    if (f.Options is { Count: > 0 } barcodeOptions && !BarcodeFieldType.Formats.Contains(barcodeOptions[0]))
                        errors.Add(new(src, path, $"Format barcode '{barcodeOptions[0]}' tidak didukung ({string.Join(", ", BarcodeFieldType.Formats)})"));
                    break;
                case "Button":
                    if (string.IsNullOrWhiteSpace(f.ButtonAction)) errors.Add(new(src, path, "Field Button membutuhkan buttonAction (ActionId form action)"));
                    break;
            }

            // Gap #3 types: where they may be used and what they may carry.
            if (d.IsChild && !FieldKinds.AllowedInChildTable(f.Type))
                errors.Add(new(src, path, $"Tipe {f.Type} tidak bisa dipakai di child table"));
            if (f.Type is "Password" or "Attach" or "Signature" || FieldKinds.IsLayout(f.Type))
            {
                if (f.Compute is not null) errors.Add(new(src, path, $"Field {f.Type} tidak dapat di-compute"));
                if (f.FetchFrom is not null) errors.Add(new(src, path, $"Field {f.Type} tidak dapat memakai fetchFrom"));
                if (f.Default is { ValueKind: not JsonValueKind.Null }) errors.Add(new(src, path, $"Field {f.Type} tidak boleh punya default"));
            }
            if (FieldKinds.IsLayout(f.Type) && !string.IsNullOrEmpty(f.RequiredWhen))
                errors.Add(new(src, path, $"Field {f.Type} hanya tampilan dan tidak bisa wajib diisi"));

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

        var compiledHooks = new List<CompiledDocHook>();
        var allowedHookEvents = new[] { "BeforeValidate", "BeforeInsert", "BeforeUpdate", "AfterSave", "OnLoad" };
        var hookNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hook in d.Hooks)
        {
            var path = $"hooks.{hook.Name}";
            if (string.IsNullOrWhiteSpace(hook.Name)) errors.Add(new(src, "hooks", "Nama hook wajib diisi"));
            else if (!hookNames.Add(hook.Name)) errors.Add(new(src, path, "Nama hook duplikat"));
            if (!allowedHookEvents.Contains(hook.Event, StringComparer.Ordinal))
                errors.Add(new(src, path + ".event", $"Event '{hook.Event}' tidak diizinkan"));
            if (hook.Actions.Count == 0) errors.Add(new(src, path + ".actions", "Minimal satu aksi wajib diisi"));

            object? conditionValue = null;
            if (hook.When is { } condition)
            {
                if (!byName.TryGetValue(condition.Field, out var field))
                    errors.Add(new(src, path + ".when.field", $"Field '{condition.Field}' tidak ada"));
                else if (condition.Operator is not ("Equals" or "NotEquals" or "ChangedTo"))
                    errors.Add(new(src, path + ".when.operator", $"Operator '{condition.Operator}' tidak diizinkan"));
                else if (condition.Value is { } raw)
                {
                    var normalized = field.Type.Normalize(raw);
                    if (normalized.Ok) conditionValue = normalized.Value;
                    else errors.Add(new(src, path + ".when.value", $"Nilai kondisi tidak sesuai tipe field: {normalized.Error}"));
                }
                else if (condition.Operator != "ChangedTo")
                    errors.Add(new(src, path + ".when.value", "Nilai kondisi tidak valid atau wajib diisi"));
                if (condition.Operator == "ChangedTo" && hook.Event != "BeforeUpdate" && hook.Event != "AfterSave")
                    errors.Add(new(src, path + ".when.operator", "ChangedTo hanya berlaku pada BeforeUpdate atau AfterSave"));
            }

            foreach (var action in hook.Actions)
            {
                if (action.Type == "Reject")
                {
                    if (string.IsNullOrWhiteSpace(action.Message)) errors.Add(new(src, path + ".actions", "Aksi Reject membutuhkan message"));
                    if (hook.Event is "AfterSave" or "OnLoad") errors.Add(new(src, path + ".actions", "Reject tidak dapat digunakan pada AfterSave atau OnLoad"));
                    if (action.Field is not null && !byName.ContainsKey(action.Field)) errors.Add(new(src, path + ".actions.field", $"Field '{action.Field}' tidak ada"));
                }
                else if (action.Type == "EmitEvent")
                {
                    if (hook.Event != "AfterSave") errors.Add(new(src, path + ".actions", "EmitEvent hanya dapat digunakan pada AfterSave"));
                    if (string.IsNullOrWhiteSpace(action.EventType) || action.EventType.Length > 128 || action.EventType.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
                        errors.Add(new(src, path + ".actions.eventType", "eventType hanya boleh berisi huruf, angka, titik, garis bawah, dan tanda hubung (maks. 128)"));
                }
                else errors.Add(new(src, path + ".actions.type", $"Aksi '{action.Type}' tidak diizinkan"));
            }
            compiledHooks.Add(new CompiledDocHook { Def = hook, ConditionValue = conditionValue });
        }

        var migrationTargets = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (d.DataMigration?.Operations.Count ?? 0); i++)
        {
            var migration = d.DataMigration!.Operations[i];
            var path = $"dataMigration.operations[{i}]";
            if (migration.Type == "RenameField")
            {
                if (string.IsNullOrWhiteSpace(migration.ToField) || !fieldNames.Contains(migration.ToField))
                    errors.Add(new(src, path + ".toField", $"Field tujuan '{migration.ToField}' tidak ada"));
                if (migration.FromField == migration.ToField)
                    errors.Add(new(src, path, "Field sumber dan tujuan rename harus berbeda"));
                if (migration.ToField is { } target && !migrationTargets.Add(target))
                    errors.Add(new(src, path + ".toField", $"Field tujuan '{target}' dipakai lebih dari sekali"));
                if (string.IsNullOrWhiteSpace(migration.FromField))
                    errors.Add(new(src, path + ".fromField", "Field sumber wajib diisi"));
            }
            else if (migration.Type == "SetDefault")
            {
                if (string.IsNullOrWhiteSpace(migration.Field) || !byName.TryGetValue(migration.Field, out var field))
                    errors.Add(new(src, path + ".field", $"Field tujuan '{migration.Field}' tidak ada"));
                else if (migration.Value is { } raw)
                {
                    var normalized = field.Type.Normalize(raw);
                    if (!normalized.Ok) errors.Add(new(src, path + ".value", $"Nilai default tidak sesuai tipe field: {normalized.Error}"));
                }
                else errors.Add(new(src, path + ".value", "Nilai default wajib diisi"));
                if (migration.Field is { } target && !migrationTargets.Add(target))
                    errors.Add(new(src, path + ".field", $"Field tujuan '{target}' dipakai lebih dari sekali"));
            }
            else if (migration.Type == "ConvertType")
            {
                if (string.IsNullOrWhiteSpace(migration.Field) || !byName.ContainsKey(migration.Field))
                    errors.Add(new(src, path + ".field", $"Field tujuan '{migration.Field}' tidak ada"));
                if (migration.Field is { } target && !migrationTargets.Add(target))
                    errors.Add(new(src, path + ".field", $"Field tujuan '{target}' dipakai lebih dari sekali"));
                if (migration.Value is not { ValueKind: JsonValueKind.String } targetType ||
                    !fieldTypes.TryGet(targetType.GetString()!, out _))
                    errors.Add(new(src, path + ".value", "ConvertType.value harus nama tipe field yang terdaftar"));
            }
            else errors.Add(new(src, path + ".type", $"Operasi migrasi '{migration.Type}' tidak didukung"));
        }

        if (d.IsSingle && d.IsChild) errors.Add(new(src, "isSingle", "Child table tidak bisa sekaligus Single DocType"));
        if (d.IsSingle && !string.IsNullOrWhiteSpace(d.Workflow)) errors.Add(new(src, "workflow", "Single DocType tidak mendukung workflow"));
        if (d.AllowAutoRepeat && d.IsChild) errors.Add(new(src, "allowAutoRepeat", "Child DocType cannot have AllowAutoRepeat enabled"));
        if (d.AllowAutoRepeat && d.IsSingle) errors.Add(new(src, "allowAutoRepeat", "Single DocType cannot have AllowAutoRepeat enabled"));

        // Setiap DocType (bukan child table) wajib punya modul dari master modul tenant.
        if (_scope is not null && !d.IsChild)
        {
            if (string.IsNullOrWhiteSpace(d.Module))
                errors.Add(new(src, "module", "Module wajib diisi: pilih modul dari Master Modul."));
            else if (!_scope.Modules.Contains(d.Module))
                errors.Add(new(src, "module", $"Module '{d.Module}' belum ada di Master Modul. Buat dulu di Master Modules, atau pilih modul yang tersedia."));
        }

        foreach (var p in d.Permissions)
        {
            if (string.IsNullOrWhiteSpace(p.Role)) errors.Add(new(src, "permissions", "Role wajib diisi"));
            CheckRole(src, $"permissions.{p.Role}", p.Role, errors);
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
            if (!StandardFields.Queryable.Contains(c) && !(byName.TryGetValue(c, out var cf) && cf.Def.Type != "Table"
                    && (cf.Def.Type == "Image" || FieldKinds.IsQueryable(cf.Def.Type))))
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
            Def = d, Fields = compiledFields, ByName = byName, ComputeOrder = computeOrder, Rules = rules, Hooks = compiledHooks,
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
        // Section dengan subTab: field tanpa subTab tampil di atas, lalu satu bagian per tab di dalam section.
        static List<LayoutColumn> Columns(IEnumerable<CompiledField> fs) =>
            fs.GroupBy(f => Math.Clamp(f.Def.Column, 1, 3)).OrderBy(g => g.Key).Select(cg => new LayoutColumn(cg.ToList())).ToList();
        static string? SubTabOf(CompiledField f) => string.IsNullOrWhiteSpace(f.Def.SubTab) ? null : f.Def.SubTab.Trim();

        var tabs = new List<LayoutTab>();
        foreach (var tabGroup in fields.GroupBy(f => f.Def.Tab ?? "Detail"))
        {
            var sections = new List<LayoutSection>();
            foreach (var sg in tabGroup.GroupBy(f => f.Def.Section))
            {
                var main = sg.Where(f => SubTabOf(f) is null).ToList();
                var inner = sg.Where(f => SubTabOf(f) is not null).GroupBy(f => SubTabOf(f)!, StringComparer.Ordinal).ToList();
                if (inner.Count == 0)
                {
                    sections.Add(new LayoutSection(sg.Key, Columns(main)));
                    continue;
                }
                if (main.Count > 0) sections.Add(new LayoutSection(sg.Key, Columns(main)));
                var names = inner.Select(g => g.Key).ToList();
                var group = $"{tabGroup.Key}\u001f{sg.Key}";
                foreach (var g in inner)
                    // Title stays null: the inner tab is shown by its strip, not as a titled section.
                    sections.Add(new LayoutSection(null, Columns(g))
                    {
                        SubTab = g.Key, SubTabGroup = group, SubTabs = names, ParentTitle = sg.Key, ShowParentTitle = main.Count == 0,
                    });
            }
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
            foreach (var role in t.AllowedRoles) CheckRole(src, path, role, errors);
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
            if (of.Label != nf.Label || of.Description != nf.Description || of.Tab != nf.Tab || of.Section != nf.Section || of.SubTab != nf.SubTab || of.Column != nf.Column || of.InListView != nf.InListView || of.VisibleWhen != nf.VisibleWhen || of.Filterable != nf.Filterable || of.Visible != nf.Visible || of.Pinned != nf.Pinned || of.RowGrouping != nf.RowGrouping || of.GroupCollapsedByDefault != nf.GroupCollapsedByDefault)
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
        if (JsonSerializer.Serialize(oldDef.Hooks) != JsonSerializer.Serialize(newDef.Hooks))
            changes.Add(new(ChangeRisk.Risky, "hooks", "Visual hooks berubah — perilaku dokumen berubah dan perlu review"));
        if (JsonSerializer.Serialize(oldDef.DataMigration) != JsonSerializer.Serialize(newDef.DataMigration))
            changes.Add(new(ChangeRisk.NeedsTransform, "dataMigration", "Rencana transformasi data berubah"));
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
    /// <summary>All saved customization versions (every save is a new row; the highest version per DocType is active).</summary>
    IReadOnlyList<DocTypeCustomization> LoadAllCustomizations() => [];
    void SaveCustomization(DocTypeCustomization customization) { }
}

public interface IAtomicDocTypeMigrationPublisher
{
    DataMigrationPreviewResult PreviewMigration(CompiledDocType source, CompiledDocType target, int maxDocuments);
    DataMigrationResult MigrateAndPublish(MetadataVersion version, CompiledDocType source, CompiledDocType target,
        int expectedDocuments, int maxDocuments);
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

    // Customization layer: base definitions (file or stored version) + administrator overlay per DocType.
    private readonly HashSet<string> _appDocTypes;
    private readonly Dictionary<string, int> _fileVersions;
    private readonly Dictionary<string, DocTypeDef> _base = new();
    private readonly Dictionary<string, List<DocTypeCustomization>> _customHistory = new();
    private readonly Dictionary<string, DocTypeCustomization> _custom = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _customWarnings = new();

    /// <param name="docTypes">Metadata dari file (sumber awal / hasil deploy).</param>
    /// <param name="store">Opsional: penyimpanan versi permanen (mis. PostgreSQL). Tanpa store, versi hasil publish hilang saat restart.</param>
    public MetadataRegistry(MetadataCompiler compiler, IEnumerable<DocTypeDef> docTypes, IEnumerable<WorkflowDef> workflows, TimeProvider clock,
        IMetadataVersionStore? store = null)
    {
        _compiler = compiler;
        _store = store;
        Clock = clock;
        var fromFiles = docTypes.ToDictionary(d => d.Name);
        _appDocTypes = fromFiles.Keys.ToHashSet(StringComparer.Ordinal);
        _fileVersions = fromFiles.ToDictionary(kv => kv.Key, kv => kv.Value.Version);

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

        foreach (var (name, def) in active) _base[name] = def;
        foreach (var g in (store?.LoadAllCustomizations() ?? []).GroupBy(c => c.DocType))
        {
            var versions = g.OrderBy(c => c.Version).ToList();
            _customHistory[g.Key] = versions;
            if (!versions[^1].IsEmpty) _custom[g.Key] = versions[^1];
        }
        _current = CompileAtStartup();

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

    // ---------------- Customization layer ----------------

    /// <summary>DocType shipped with the application (a *.doctype.json file). UI edits of it are saved as customizations.</summary>
    public bool IsAppOwned(string docType) => _appDocTypes.Contains(docType);

    /// <summary>
    /// True when the active base of an application DocType is a version published from the UI before the
    /// customization layer existed (newer than the file). The next deploy of a newer file replaces it.
    /// </summary>
    public bool HasLegacyUiVersion(string docType)
    {
        lock (_gate)
            return _fileVersions.TryGetValue(docType, out var fileVersion) && _base.TryGetValue(docType, out var b) && b.Version > fileVersion;
    }

    /// <summary>Definition without customizations (from the file, or the stored base version).</summary>
    public DocTypeDef? BaseDefinition(string docType) { lock (_gate) return _base.TryGetValue(docType, out var d) ? Customizations.Clone(d) : null; }

    public DocTypeCustomization? Customization(string docType) { lock (_gate) return _custom.GetValueOrDefault(docType); }

    public IReadOnlyList<DocTypeCustomization> CustomizationHistory(string docType)
    {
        lock (_gate) return _customHistory.TryGetValue(docType, out var h) ? h.ToList() : [];
    }

    /// <summary>Parts of the customization that could not be applied (e.g. a deploy removed the field they changed).</summary>
    public IReadOnlyList<string> CustomizationWarnings(string docType)
    {
        lock (_gate) return _customWarnings.TryGetValue(docType, out var w) ? w : [];
    }

    /// <summary>Removes all customizations of a DocType (custom field values stay in the documents).</summary>
    public void ResetCustomization(string docType, string resetBy, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            if (!_custom.ContainsKey(docType)) return;
            var defs = _current.DocTypes.Values.Select(c => c.Def).Where(d => d.Name != docType).Append(_base[docType]).ToList();
            var snapshot = _compiler.Compile(defs, _workflows, scope); // throws when other DocTypes depend on custom fields
            SaveCustomizationVersion(new DocTypeCustomization { DocType = docType }, resetBy);
            _customWarnings.Remove(docType);
            Volatile.Write(ref _current, snapshot);
            Published?.Invoke(snapshot);
        }
    }

    private DocTypeDef Effective(DocTypeDef baseDef)
    {
        var result = Customizations.Apply(baseDef, _custom.GetValueOrDefault(baseDef.Name));
        if (result.Warnings.Count > 0) _customWarnings[baseDef.Name] = result.Warnings;
        else _customWarnings.Remove(baseDef.Name);
        return result.Def;
    }

    /// <summary>
    /// Startup compile. A customization that no longer compiles against a newer base (for example it references a
    /// field the deploy removed) is switched off for that DocType instead of stopping the application.
    /// </summary>
    private MetadataSnapshot CompileAtStartup()
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var defs = _base.Values.Select(b => disabled.Contains(b.Name) ? Customizations.Clone(b) : Effective(b)).ToList();
            try { return _compiler.Compile(defs, _workflows); }
            catch (MetadataCompilationException ex)
            {
                static string DocTypeOf(MetadataError e) => e.Source.Split('@')[0]; // compiler sources look like "Name@v3"
                var culprits = ex.Errors.Select(DocTypeOf).Where(s => _custom.ContainsKey(s) && !disabled.Contains(s)).ToHashSet();
                if (culprits.Count == 0) culprits = _custom.Keys.Where(k => !disabled.Contains(k)).ToHashSet();
                if (culprits.Count == 0) throw;
                foreach (var name in culprits)
                {
                    disabled.Add(name);
                    _customWarnings[name] = ex.Errors.Where(e => DocTypeOf(e) == name).Select(e => $"Customization switched off: {e}")
                        .DefaultIfEmpty($"Customization switched off because the metadata did not compile with it.")
                        .Append("Saving this DocType in the editor replaces the switched-off customization; fix or re-add what is still needed first.").ToList();
                }
            }
        }
        return _compiler.Compile(_base.Values.ToList(), _workflows);
    }

    private void SaveCustomizationVersion(DocTypeCustomization c, string by)
    {
        var history = _customHistory.TryGetValue(c.DocType, out var h) ? h : _customHistory[c.DocType] = [];
        c.Version = history.Count == 0 ? 1 : history[^1].Version + 1;
        c.ModifiedBy = by;
        c.Modified = Clock.GetUtcNow();
        _store?.SaveCustomization(c); // persist first; on failure nothing changes in memory
        history.Add(c);
        if (c.IsEmpty) _custom.Remove(c.DocType); else _custom[c.DocType] = c;
    }

    /// <summary>Dipicu setelah DocType atau workflow dipublikasikan (mis. untuk menyinkronkan view pelaporan).</summary>
    public event Action<MetadataSnapshot>? Published;
    public MetadataSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Memeriksa metadata yang sedang aktif terhadap master role & modul tenant TANPA mempublikasikan apa pun.
    /// Dipakai saat startup untuk melaporkan metadata lama yang tidak lagi cocok dengan master.
    /// </summary>
    public IReadOnlyList<MetadataError> AuditAgainst(MetadataScope scope)
    {
        lock (_gate)
        {
            try
            {
                _compiler.Compile(_current.DocTypes.Values.Select(c => c.Def).ToList(), _workflows, scope);
                return [];
            }
            catch (MetadataCompilationException ex) { return ex.Errors; }
        }
    }

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
            // Application DocType: the link is stored as a customization, the base version does not change.
            attached.Version = IsAppOwned(owner.Name) ? owner.Version : owner.Version + 1;
            docs = docs.Where(d => d.Name != owner.Name).Append(attached).ToList();
        }
        return (docs, wfs, attached);
    }

    public (MetadataSnapshot? Snapshot, IReadOnlyList<MetadataError> Errors) CompileWorkflowDraft(WorkflowDef draft, bool attach, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            if (attach && string.IsNullOrWhiteSpace(draft.DocType))
                return (null, [new(draft.Name, "docType", "Pilih DocType sebelum mengaitkan workflow")]);
            var (docs, wfs, _) = WithWorkflow(draft, attach);
            try { return (_compiler.Compile(docs, wfs, scope), []); }
            catch (MetadataCompilationException ex) { return (null, ex.Errors); }
        }
    }

    private (List<MetadataError> Errors, List<MetadataChange> Changes, MetadataSnapshot? Snapshot, DocTypeDef? Attached) TryWorkflow(WorkflowDef draft, bool attach, MetadataScope? scope = null)
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
        try { if (errors.Count == 0) snap = _compiler.Compile(docs, wfs, scope); }
        catch (MetadataCompilationException ex) { errors.AddRange(ex.Errors); }
        return (errors, changes, snap, attached);
    }

    public PublishPreview PreviewWorkflow(WorkflowDef draft, bool attach, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            var (errors, changes, _, _) = TryWorkflow(draft, attach, scope);
            return new PublishPreview(errors.Count == 0, errors, changes, changes.Count == 0 ? ChangeRisk.Safe : changes.Max(c => c.Risk));
        }
    }

    public WorkflowVersion PublishWorkflow(WorkflowDef draft, string publishedBy, bool acknowledgeRisk, bool attach, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            var (errors, changes, snap, attached) = TryWorkflow(draft, attach, scope);
            if (errors.Count > 0) throw new MetadataCompilationException(errors);
            if (changes.Any(c => c.Risk == ChangeRisk.Risky) && !acknowledgeRisk)
                throw new InvalidOperationException("Risky changes must be confirmed before publishing");

            // Linking a workflow to an application DocType is a customization (survives deploys); check it before saving anything.
            DocTypeCustomization? attachCustomization = null;
            if (attached is not null && IsAppOwned(attached.Name) && _base.TryGetValue(attached.Name, out var attachBase))
            {
                var (customization, extractErrors) = Customizations.Extract(attachBase, attached);
                if (extractErrors.Count > 0) throw new MetadataCompilationException(extractErrors);
                attachCustomization = customization;
            }

            var now = Clock.GetUtcNow();
            var wv = new WorkflowVersion(draft.Name, draft.DocType, draft.Version, draft, MetadataStatus.Published, publishedBy, now,
                WorkflowDiff.Compare(_workflows.FirstOrDefault(w => w.Name == draft.Name), draft));
            _store?.SaveWorkflow(wv);
            DeprecateWorkflow(draft.Name);
            (_wfHistory.TryGetValue(draft.Name, out var wh) ? wh : _wfHistory[draft.Name] = []).Add(wv);

            if (attachCustomization is not null)
            {
                SaveCustomizationVersion(attachCustomization, publishedBy);
            }
            else if (attached is not null)
            {
                var mv = new MetadataVersion(attached.Name, attached.Version, attached, MetadataStatus.Published, publishedBy, now,
                    MetadataDiff.Compare(_current.Get(attached.Name).Def, attached));
                _store?.Save(mv);
                if (_history.TryGetValue(attached.Name, out var hist))
                    for (var i = 0; i < hist.Count; i++)
                        if (hist[i].Status == MetadataStatus.Published) hist[i] = hist[i] with { Status = MetadataStatus.Deprecated };
                (_history.TryGetValue(attached.Name, out var hh) ? hh : _history[attached.Name] = []).Add(mv);
                if (!_custom.ContainsKey(attached.Name)) _base[attached.Name] = attached;
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

    public PublishPreview Preview(DocTypeDef draft, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            var (errors, changes) = TryCompileWith(draft, out _, scope);
            var risk = changes.Count == 0 ? ChangeRisk.Safe : changes.Max(c => c.Risk);
            return new PublishPreview(errors.Count == 0, errors, changes, risk);
        }
    }

    /// <summary>
    /// Mengompilasi draft bersama metadata aktif lainnya tanpa mempublikasikannya (untuk validasi langsung dan
    /// pratinjau di editor). Berbeda dengan Preview, versi draft tidak diwajibkan lebih tinggi.
    /// </summary>
    public (MetadataSnapshot? Snapshot, IReadOnlyList<MetadataError> Errors) CompileDraft(DocTypeDef draft, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            var old = _current.DocTypes.TryGetValue(draft.Name, out var current) ? current.Def : null;
            var effective = draft;
            var extractErrors = new List<MetadataError>();
            if (IsAppOwned(draft.Name) && _base.TryGetValue(draft.Name, out var baseDef))
            {
                var (customization, errs) = Customizations.Extract(baseDef, draft);
                extractErrors.AddRange(errs);
                effective = Customizations.Apply(baseDef, customization).Def;
            }
            else effective = Customizations.Apply(draft, _custom.GetValueOrDefault(draft.Name)).Def;
            var defs = _current.DocTypes.Values.Select(c => c.Def).Where(d => d.Name != draft.Name).Append(effective).ToList();
            try
            {
                var snapshot = _compiler.Compile(defs, _workflows, scope);
                var migrationErrors = IsAppOwned(draft.Name) ? [] : ValidateDataMigrations(old, draft);
                var all = extractErrors.Concat(migrationErrors).ToList();
                return all.Count == 0 ? (snapshot, []) : (null, all);
            }
            catch (MetadataCompilationException ex) { return (null, extractErrors.Concat(ex.Errors).ToList()); }
        }
    }

    public DataMigrationPreviewResult PreviewDataMigration(DocTypeDef draft, int maxDocuments = 10_000, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            var (errors, _) = TryCompileWith(draft, out var snapshot, scope);
            if (errors.Count > 0) return new(false, 0, string.Join("; ", errors.Select(e => e.Message)));
            if (draft.DataMigration is null) return new(false, 0, "Draft tidak memiliki rencana migrasi data.");
            if (_store is not IAtomicDocTypeMigrationPublisher migrationPublisher)
                return new(false, 0, "Penyimpanan metadata ini tidak mendukung dry-run migrasi atomik.");
            var source = _current.Get(draft.Name);
            var target = snapshot!.Get(draft.Name);
            return migrationPublisher.PreviewMigration(source, target, maxDocuments);
        }
    }

    /// <summary>Publish versi baru secara immutable. Perubahan berisiko butuh konfirmasi eksplisit.</summary>
    public MetadataVersion Publish(DocTypeDef draft, string publishedBy, bool acknowledgeRisk, MetadataScope? scope = null)
    {
        if (draft.DataMigration is null) return Publish(draft, publishedBy, acknowledgeRisk, 0, scope);
        var dryRun = PreviewDataMigration(draft, scope: scope);
        if (!dryRun.Valid)
            throw new InvalidOperationException($"Migration dry-run failed: {dryRun.Error}");
        return Publish(draft, publishedBy, acknowledgeRisk, dryRun.Documents, scope);
    }

    public MetadataVersion Publish(DocTypeDef draft, string publishedBy, bool acknowledgeRisk, int migratedDocuments, MetadataScope? scope = null)
    {
        lock (_gate)
        {
            var (errors, changes) = TryCompileWith(draft, out var snapshot, out var effective, out var customization, scope);
            if (errors.Count > 0) throw new MetadataCompilationException(errors);
            if (changes.Any(c => c.Risk == ChangeRisk.Risky) && !acknowledgeRisk)
                throw new InvalidOperationException("Risky changes must be confirmed before publishing");

            if (customization is not null)
            {
                // Application DocType: only the overlay is saved; the base stays as deployed.
                SaveCustomizationVersion(customization, publishedBy);
                Effective(_base[draft.Name]); // refresh warnings
                Volatile.Write(ref _current, snapshot!);
                Published?.Invoke(snapshot!);
                return new MetadataVersion(draft.Name, _base[draft.Name].Version, effective, MetadataStatus.Published, publishedBy, Clock.GetUtcNow(), changes);
            }

            var source = _current.TryGet(draft.Name, out var currentMeta) ? currentMeta : null;
            var target = snapshot!.Get(draft.Name);
            var publishedDef = draft;
            var publishedSnapshot = snapshot;
            IAtomicDocTypeMigrationPublisher? migrationPublisher = null;
            if (draft.DataMigration is not null)
            {
                if (source is null) throw new InvalidOperationException("Data migration can only be applied to an active DocType.");
                if (_store is not IAtomicDocTypeMigrationPublisher publisher)
                    throw new InvalidOperationException("This metadata store does not support atomic data migration.");
                migrationPublisher = publisher;
                publishedDef = MetadataJson.ParseDocType(MetadataJson.Serialize(draft));
                publishedDef.DataMigration = null;
                var defs = _current.DocTypes.Values.Select(c => c.Def).Where(d => d.Name != draft.Name).Append(Effective(publishedDef)).ToList();
                publishedSnapshot = _compiler.Compile(defs, _workflows, scope);
            }
            var version = new MetadataVersion(draft.Name, draft.Version, publishedDef, MetadataStatus.Published, publishedBy, Clock.GetUtcNow(), changes);
            if (draft.DataMigration is not null)
            {
                migrationPublisher!.MigrateAndPublish(version, source!, target, migratedDocuments, 10_000);
            }
            else _store?.Save(version); // simpan dulu; bila gagal, versi aktif tidak berubah

            if (_history.TryGetValue(draft.Name, out var hist))
                for (var i = 0; i < hist.Count; i++)
                    if (hist[i].Status == MetadataStatus.Published) hist[i] = hist[i] with { Status = MetadataStatus.Deprecated };

            (_history.TryGetValue(draft.Name, out var h) ? h : _history[draft.Name] = []).Add(version);
            _base[draft.Name] = publishedDef;
            Volatile.Write(ref _current, publishedSnapshot!);
            Published?.Invoke(publishedSnapshot!);
            return version;
        }
    }

    private (List<MetadataError> Errors, IReadOnlyList<MetadataChange> Changes) TryCompileWith(DocTypeDef draft, out MetadataSnapshot? snapshot, MetadataScope? scope = null) =>
        TryCompileWith(draft, out snapshot, out _, out _, scope);

    /// <summary>
    /// For an application DocType the draft is an edited effective definition: it is turned into a customization
    /// (<paramref name="customization"/>), and <paramref name="effective"/> is base + that customization.
    /// For other DocTypes the draft is a new base version.
    /// </summary>
    private (List<MetadataError> Errors, IReadOnlyList<MetadataChange> Changes) TryCompileWith(DocTypeDef draft, out MetadataSnapshot? snapshot,
        out DocTypeDef effective, out DocTypeCustomization? customization, MetadataScope? scope = null)
    {
        snapshot = null;
        customization = null;
        var current = _current.DocTypes.TryGetValue(draft.Name, out var existing) ? existing.Def : null;
        var errors = new List<MetadataError>();

        if (IsAppOwned(draft.Name) && _base.TryGetValue(draft.Name, out var baseDef))
        {
            var (extracted, extractErrors) = Customizations.Extract(baseDef, draft);
            errors.AddRange(extractErrors);
            customization = extracted;
            var applied = Customizations.Apply(baseDef, extracted);
            effective = applied.Def;
            errors.AddRange(applied.Warnings.Select(w => new MetadataError(draft.Name, "customization", w)));
        }
        else
        {
            if (current is not null && draft.Version <= current.Version)
                errors.Add(new(draft.Name, "version", $"Versi harus lebih besar dari versi aktif ({current.Version})"));
            effective = Customizations.Apply(draft, _custom.GetValueOrDefault(draft.Name)).Def;
        }

        var changes = MetadataDiff.Compare(current, effective);
        var defs = _current.DocTypes.Values.Select(c => c.Def).Where(d => d.Name != draft.Name).Append(effective).ToList();
        try
        {
            if (errors.Count == 0) snapshot = _compiler.Compile(defs, _workflows, scope);
            if (customization is null) errors.AddRange(ValidateDataMigrations(current, draft));
        }
        catch (MetadataCompilationException ex) { errors.AddRange(ex.Errors); }
        return (errors, changes);
    }

    private static List<MetadataError> ValidateDataMigrations(DocTypeDef? source, DocTypeDef target)
    {
        var errors = new List<MetadataError>();
        if (target.DataMigration is null) return errors;
        if (target.DataMigration.FromVersion != source?.Version)
            errors.Add(new(target.Name, "dataMigration.fromVersion", $"Rencana migrasi harus berasal dari versi aktif {source?.Version}"));

        for (var i = 0; i < target.DataMigration.Operations.Count; i++)
        {
            var migration = target.DataMigration.Operations[i];
            var path = $"dataMigration.operations[{i}]";
            if (source is null)
            {
                errors.Add(new(target.Name, path, "Migrasi data hanya berlaku untuk DocType yang sudah aktif"));
                continue;
            }

            if (migration.Type == "RenameField")
            {
                var oldField = source.Fields.FirstOrDefault(f => f.Name == migration.FromField);
                var newField = target.Fields.FirstOrDefault(f => f.Name == migration.ToField);
                if (oldField is null) errors.Add(new(target.Name, path + ".fromField", $"Field sumber '{migration.FromField}' tidak ada di versi aktif"));
                else if (oldField.Type != newField?.Type) errors.Add(new(target.Name, path, "RenameField hanya mendukung field dengan tipe sama"));
            }
            else if (migration.Type == "ConvertType")
            {
                var oldField = source.Fields.FirstOrDefault(f => f.Name == migration.Field);
                var newField = target.Fields.FirstOrDefault(f => f.Name == migration.Field);
                if (oldField is null) errors.Add(new(target.Name, path + ".field", $"Field '{migration.Field}' tidak ada di versi aktif"));
                else if (newField is not null && oldField.Type == newField.Type)
                    errors.Add(new(target.Name, path, "ConvertType membutuhkan perubahan tipe field"));
                if (oldField?.Type is "Table" or "Image" or "Link" || newField?.Type is "Table" or "Image" or "Link")
                    errors.Add(new(target.Name, path, "ConvertType MVP hanya mendukung field skalar biasa"));
            }
        }
        return errors;
    }

    // ---------------- Multi-node ----------------

    /// <summary>Fields that belong to this instance and are never taken from a rebuilt registry.</summary>
    private static readonly HashSet<string> OwnFields = new(StringComparer.Ordinal) { "_compiler", "_gate", "_store", "Published", "<Clock>k__BackingField" };

    /// <summary>
    /// Takes over the state of a registry rebuilt from the shared store, after another node published metadata.
    /// <paramref name="rebuild"/> runs under this registry's lock, so it cannot overwrite a publish made here meanwhile.
    /// All state fields are taken over (also those added by extensions of this class, e.g. customizations), so the
    /// method stays correct when fields are added. <see cref="Published"/> is NOT raised: the node that published
    /// already synchronized the database objects (reporting views, indexes).
    /// </summary>
    public void ReloadFrom(Func<MetadataRegistry> rebuild)
    {
        lock (_gate)
        {
            var fresh = rebuild();
            foreach (var field in typeof(MetadataRegistry).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            {
                if (OwnFields.Contains(field.Name)) continue;
                field.SetValue(this, field.GetValue(fresh));
            }
            Volatile.Write(ref _current, fresh.Current);
        }
    }
}
