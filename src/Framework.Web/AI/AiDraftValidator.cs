using System.Text.RegularExpressions;
using Framework.Dashboards;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Web.AI;

/// <summary>
/// Validates AI drafts with the same compilers and guards the editors use. Used both as a tool the model can call
/// while it works and as the final server-side check on every answer. Never saves or publishes anything.
/// </summary>
public sealed partial class AiDraftValidator(MetadataRegistry registry, MetadataCompiler compiler, RoleCatalog roleCatalog, DashboardService dashboards)
{
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)\b(?!\s*\()")]
    private static partial Regex QualifiedNameRx();

    public MetadataScope Scope(UserContext user) => roleCatalog.Scope(user.TenantId);

    // ------------------------------------------------------------------ DocType

    /// <summary>
    /// Compiles the drafts together with all active metadata. <paramref name="existingName"/> = null for a new
    /// DocType (names must be free); otherwise the name of the DocType being modified.
    /// </summary>
    public List<string> ValidateDocTypes(UserContext user, DocTypeDef main, IReadOnlyList<DocTypeDef> children, WorkflowDef? workflow, string? existingName)
    {
        var errors = new List<string>();
        var current = registry.Current;

        if (existingName is null)
        {
            if (current.TryGet(main.Name, out _)) errors.Add($"DocType '{main.Name}' already exists; choose another name.");
            if (main.IsChild) errors.Add("The main DocType must not be a child table (isChild = false).");
        }
        else if (main.Name != existingName)
        {
            errors.Add($"The DocType name must stay '{existingName}'.");
        }

        foreach (var c in children)
        {
            if (!c.IsChild) errors.Add($"Child DocType '{c.Name}' must have isChild = true.");
            if (current.TryGet(c.Name, out _)) errors.Add($"DocType '{c.Name}' already exists; reuse it in childDocType instead of redefining it, or choose another name.");
        }

        var drafts = children.Append(main).Select(Clone).ToList();
        var dupes = drafts.GroupBy(d => d.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var d in dupes) errors.Add($"DocType name '{d}' is used more than once.");

        var workflows = registry.Workflows.ToList();
        if (workflow is not null)
        {
            if (registry.Workflows.Any(w => w.Name == workflow.Name)) errors.Add($"Workflow '{workflow.Name}' already exists; choose another name.");
            var wf = CloneWorkflow(workflow);
            wf.DocType = main.Name;
            drafts[^1].Workflow = wf.Name; // main is the last draft
            workflows = workflows.Where(w => w.Name != wf.Name).Append(wf).ToList();
        }

        var defs = current.DocTypes.Values.Select(c => c.Def).Where(d => drafts.All(x => x.Name != d.Name)).Concat(drafts).ToList();
        try
        {
            compiler.Compile(defs, workflows, Scope(user));
        }
        catch (MetadataCompilationException ex)
        {
            errors.AddRange(ex.Errors.Select(e => e.ToString()));
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Malformed model output (e.g. "fields": null) must become a repairable error, not a crash.
            errors.Add($"The draft is malformed: {ex.Message}");
        }

        return errors.Distinct().ToList();
    }

    // ------------------------------------------------------------------ Workflow

    public List<string> ValidateWorkflow(UserContext user, WorkflowDef workflow, bool isNew)
    {
        var errors = new List<string>();
        var existing = registry.Workflows.FirstOrDefault(w => w.Name == workflow.Name);
        if (isNew && existing is not null) errors.Add($"Workflow '{workflow.Name}' already exists; choose another name.");
        if (!isNew && existing is null) errors.Add($"Workflow '{workflow.Name}' does not exist.");

        var attach = !string.IsNullOrWhiteSpace(workflow.DocType);
        try
        {
            var (_, compileErrors) = registry.CompileWorkflowDraft(CloneWorkflow(workflow), attach, Scope(user));
            errors.AddRange(compileErrors.Select(e => e.ToString()));
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            errors.Add($"The draft is malformed: {ex.Message}");
        }
        return errors.Distinct().ToList();
    }

    // ------------------------------------------------------------------ SQL

    public sealed record QueryCheck(List<string> Errors, List<WidgetParameter> Parameters, IReadOnlyList<ResultColumn> Columns);

    /// <summary>
    /// SQL guard, reporting-view check, parameter bindings, and a dry run on the read-only reporting connection.
    /// Only column names and kinds are returned; result rows are discarded and never sent to the model.
    /// </summary>
    public async Task<QueryCheck> ValidateQueryAsync(UserContext user, QueryPurpose purpose, string sql, string? headerSql,
        IReadOnlyList<WidgetParameter> parameters, VisualConfig? visual, CancellationToken ct)
    {
        var errors = new List<string>();
        sql ??= "";

        var analysis = SqlGuard.Analyze(sql);
        if (!analysis.Ok) return new([analysis.Error ?? "The query is not allowed."], [], []);
        if (!string.IsNullOrWhiteSpace(headerSql) && SqlGuard.Analyze(headerSql) is { Ok: false } h)
            errors.Add($"Header query: {h.Error}");

        errors.AddRange(UnknownRelations(sql));
        if (!string.IsNullOrWhiteSpace(headerSql)) errors.AddRange(UnknownRelations(headerSql).Select(e => $"Header query: {e}"));

        // Parameters: keep the model's definitions, add defaults for any @Name it forgot.
        var (_, merged) = DashboardService.DetectParameters(sql, parameters);
        if (!string.IsNullOrWhiteSpace(headerSql))
        {
            var (_, hp) = DashboardService.DetectParameters(headerSql, parameters);
            merged.AddRange(hp.Where(p => merged.All(x => !x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var p in merged)
        {
            if (!ParamTypes.All.Contains(p.Type)) { errors.Add($"Parameter @{p.Name}: type must be one of {string.Join(", ", ParamTypes.All)}."); continue; }
            if (!AllowedBindings(purpose).Contains(p.Binding))
                errors.Add($"Parameter @{p.Name}: binding '{p.Binding}' is not allowed here. Allowed: {string.Join(", ", AllowedBindings(purpose))}.");
            if (string.IsNullOrWhiteSpace(p.Label)) p.Label = p.Name;
            try { DashboardService.Convert(p, p.DefaultValue); }
            catch (QueryRejectedException ex) { errors.Add($"Default value: {ex.Message}"); }
        }

        IReadOnlyList<ResultColumn> columns = [];
        if (errors.Count == 0 && dashboards.IsAvailable)
        {
            try
            {
                columns = (await dashboards.PreviewAsync(user, sql, merged, ct)).Columns;
                if (!string.IsNullOrWhiteSpace(headerSql))
                {
                    var header = await dashboards.PreviewAsync(user, headerSql, merged, ct);
                    if (header.Rows.Count > 1) errors.Add("Header query must return at most one row.");
                }
            }
            catch (QueryRejectedException ex) { errors.Add($"Query failed: {ex.Message}"); }
            catch (ReportingUnavailableException) { /* static checks only */ }
            catch (Exception ex) when (ex is not OperationCanceledException and not PermissionDeniedException)
            {
                // Database errors (unknown column, syntax) are useful for the repair loop.
                errors.Add($"Query failed: {ex.Message}");
            }
        }

        if (purpose == QueryPurpose.Widget && visual is not null && columns.Count > 0)
            errors.AddRange(DashboardService.ValidateVisual(visual, columns).Select(e => $"Chart: {e}"));

        return new(errors, merged, columns);
    }

    public static IReadOnlySet<string> AllowedBindings(QueryPurpose purpose) => purpose == QueryPurpose.Widget
        ? ParamBindings.All
        : new HashSet<string> { ParamBindings.Default, ParamBindings.UserId, ParamBindings.UserDepartment };

    /// <summary>Schema-qualified names (schema.table) must be reporting views from the catalog.</summary>
    private IEnumerable<string> UnknownRelations(string sql)
    {
        var known = ReportingCatalog.Describe(registry.Current).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"\b(?:from|join)\s+[A-Za-z_][A-Za-z0-9_.]*\s+(?:as\s+)?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase))
            aliases.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(sql, @"(?:\bwith|,)\s*(?:recursive\s+)?([A-Za-z_][A-Za-z0-9_]*)\s+as\s*\(", RegexOptions.IgnoreCase))
            aliases.Add(m.Groups[1].Value);

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in QualifiedNameRx().Matches(StripStrings(sql)))
        {
            var schema = m.Groups[1].Value;
            if (aliases.Contains(schema)) continue;                       // alias.column
            if (!schema.Equals("reporting", StringComparison.OrdinalIgnoreCase))
            {
                // Could still be a table alias defined in a way the regex missed; only flag known schemas.
                if (schema.ToLowerInvariant() is "public" or "pg_catalog" or "information_schema" or "frogframe" && reported.Add(m.Value))
                    yield return $"'{m.Value}' is not allowed: query only reporting.* views.";
                continue;
            }
            if (!known.Contains(m.Value) && reported.Add(m.Value))
                yield return $"'{m.Value}' is not a reporting view. Call list_reporting_views for the available views.";
        }
    }

    private static string StripStrings(string sql) => Regex.Replace(sql, "'(?:[^']|'')*'", "''");

    // ------------------------------------------------------------------ helpers

    public static DocTypeDef Clone(DocTypeDef d) => MetadataJson.ParseDocType(MetadataJson.Serialize(d));
    public static WorkflowDef CloneWorkflow(WorkflowDef w) => MetadataJson.ParseWorkflow(MetadataJson.Serialize(w));
}
