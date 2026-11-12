using System.ComponentModel;
using System.Text.Json;
using Framework.Dashboards;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.AI;

namespace Framework.Web.AI;

/// <summary>
/// Read-only tools the model may call while designing. They expose metadata (names, fields, roles, views) and run
/// validators. There is deliberately no tool that saves, publishes, executes scripts, or returns document data.
/// One instance per request, bound to the calling user's tenant.
/// </summary>
public sealed class AiDesignerTools(UserContext user, MetadataRegistry registry, RoleCatalog roleCatalog, IFieldTypeRegistry fieldTypes,
    AiDraftValidator validator, QueryPurpose? queryPurpose)
{
    private static readonly JsonSerializerOptions Json = new(MetadataJson.Options) { WriteIndented = false };

    public IList<AITool> MetadataTools() =>
    [
        AIFunctionFactory.Create(ListDocTypes, "list_doctypes", "Lists existing DocTypes: name, title, module, child/single flags, workflow."),
        AIFunctionFactory.Create(GetDocType, "get_doctype", "Returns the full JSON definition of an existing DocType. Use it to copy conventions."),
        AIFunctionFactory.Create(ListFieldTypes, "list_field_types", "Lists the field types that can be used in fields[].type."),
        AIFunctionFactory.Create(ListRoles, "list_roles", "Lists the role names that exist in this tenant. Permissions and workflow transitions may only use these."),
        AIFunctionFactory.Create(ListModules, "list_modules", "Lists the module codes that exist in this tenant (DocType.module must be one of them)."),
        AIFunctionFactory.Create(ListWorkflows, "list_workflows", "Lists existing workflows with their DocType and states."),
        AIFunctionFactory.Create(GetWorkflow, "get_workflow", "Returns the full JSON definition of an existing workflow."),
        AIFunctionFactory.Create(ValidateDocTypeDraft, "validate_doctype_draft",
            "Compiles a DocType draft (and optional new child DocTypes and workflow) with the platform compiler. Returns a list of errors; empty means valid."),
        AIFunctionFactory.Create(ValidateWorkflowDraft, "validate_workflow_draft",
            "Compiles a workflow draft with the platform compiler. Returns a list of errors; empty means valid."),
    ];

    public IList<AITool> QueryTools() =>
    [
        AIFunctionFactory.Create(ListReportingViews, "list_reporting_views", "Lists the reporting.* views that queries may read, with their columns and types."),
        AIFunctionFactory.Create(ListRoles, "list_roles", "Lists the role names that exist in this tenant."),
        AIFunctionFactory.Create(ValidateSql, "validate_sql",
            "Checks a query with the SQL guard and a dry run on the read-only reporting connection. Returns errors and the result column names/types (never data rows)."),
    ];

    // ------------------------------------------------------------------ metadata

    private string ListDocTypes() => Serialize(registry.Current.DocTypes.Values.OrderBy(d => d.Name).Select(d => new
    {
        d.Name, d.Title, d.Def.Module, d.Def.IsChild, d.Def.IsSingle, d.Def.Workflow, Fields = d.Fields.Count
    }));

    private string GetDocType([Description("DocType name, e.g. PurchaseRequest")] string name) =>
        registry.Current.TryGet(name, out var meta) ? MetadataJson.Serialize(meta.Def) : $"DocType '{name}' does not exist.";

    private string ListFieldTypes() => Serialize(fieldTypes.Names.OrderBy(n => n).Select(n => new { Type = n, Use = FieldTypeHint(n) }));

    private string ListRoles() => Serialize(roleCatalog.Names(user.TenantId).OrderBy(r => r));

    private string ListModules() => Serialize(roleCatalog.ModuleCodes(user.TenantId).OrderBy(m => m));

    private string ListWorkflows() => Serialize(registry.Workflows.OrderBy(w => w.Name).Select(w => new
    {
        w.Name, w.DocType, w.InitialState, States = w.States.Select(s => s.Name)
    }));

    private string GetWorkflow([Description("Workflow name")] string name) =>
        registry.Workflows.FirstOrDefault(w => w.Name == name) is { } w ? MetadataJson.Serialize(w) : $"Workflow '{name}' does not exist.";

    private string ValidateDocTypeDraft(
        [Description("DocType JSON")] string docTypeJson,
        [Description("JSON array of new child DocTypes, or empty")] string? childDocTypesJson = null,
        [Description("Workflow JSON, or empty")] string? workflowJson = null,
        [Description("Name of the DocType being modified; empty when creating a new DocType")] string? existingName = null)
    {
        try
        {
            var main = MetadataJson.ParseDocType(docTypeJson);
            var children = string.IsNullOrWhiteSpace(childDocTypesJson)
                ? []
                : JsonSerializer.Deserialize<List<DocTypeDef>>(childDocTypesJson, MetadataJson.Options) ?? [];
            var workflow = string.IsNullOrWhiteSpace(workflowJson) ? null : MetadataJson.ParseWorkflow(workflowJson);
            return Serialize(validator.ValidateDocTypes(user, main, children, workflow, string.IsNullOrWhiteSpace(existingName) ? null : existingName));
        }
        catch (JsonException ex) { return Serialize(new[] { $"Invalid JSON: {ex.Message}" }); }
    }

    private string ValidateWorkflowDraft(
        [Description("Workflow JSON")] string workflowJson,
        [Description("true when creating a new workflow, false when modifying an existing one")] bool isNew = true)
    {
        try { return Serialize(validator.ValidateWorkflow(user, MetadataJson.ParseWorkflow(workflowJson), isNew)); }
        catch (JsonException ex) { return Serialize(new[] { $"Invalid JSON: {ex.Message}" }); }
    }

    // ------------------------------------------------------------------ reporting / SQL

    private string ListReportingViews() => Serialize(ReportingCatalog.Describe(registry.Current).Select(v => new
    {
        v.Name, v.Title, Columns = v.Columns.Select(c => new { Name = c.Column, c.Type })
    }));

    private async Task<string> ValidateSql(
        [Description("The SELECT / WITH query")] string sql,
        [Description("JSON array of parameters: [{\"name\":\"Start\",\"type\":\"date\",\"defaultValue\":\"2026-01-01\",\"binding\":\"default\"}], or empty")] string? parametersJson = null,
        [Description("Report templates only: header query, or empty")] string? headerSql = null,
        CancellationToken cancellationToken = default)
    {
        List<WidgetParameter> parameters;
        try
        {
            parameters = string.IsNullOrWhiteSpace(parametersJson) ? [] : JsonSerializer.Deserialize<List<WidgetParameter>>(parametersJson, DashboardJson.Options) ?? [];
        }
        catch (JsonException ex) { return Serialize(new { Errors = new[] { $"Invalid parameters JSON: {ex.Message}" } }); }

        var check = await validator.ValidateQueryAsync(user, queryPurpose ?? QueryPurpose.Widget, sql, headerSql, parameters, null, cancellationToken);
        return Serialize(new
        {
            check.Errors,
            Parameters = check.Parameters.Select(p => new { p.Name, p.Type, p.Binding, p.DefaultValue }),
            Columns = check.Columns.Select(c => new { c.Name, c.Kind })
        });
    }

    // ------------------------------------------------------------------ helpers

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static string FieldTypeHint(string type) => type switch
    {
        "Data" => "short text (max 140)",
        "Text" => "long text",
        "Email" => "email address",
        "Int" => "whole number",
        "Float" => "decimal number",
        "Currency" => "money amount (2 decimals)",
        "Check" => "yes/no checkbox",
        "Date" => "date",
        "Select" => "one of options[]",
        "Link" => "reference to another DocType (linkTo)",
        "Table" => "child table (childDocType must be an isChild DocType)",
        "Image" => "image upload",
        _ => "custom type",
    };
}
