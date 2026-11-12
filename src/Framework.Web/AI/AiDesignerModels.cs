using System.ComponentModel;
using Framework.Dashboards;
using Framework.Metadata;

namespace Framework.Web.AI;

// ---------------------------------------------------------------------------------------------------------------
//  Structured answers requested from the model (JSON schema response format). The model only proposes; every
//  answer is validated by the FrogFrame compilers on the server before it is shown, and nothing is published here.
// ---------------------------------------------------------------------------------------------------------------

public sealed class DocTypeDesignAnswer
{
    [Description("Short explanation for the administrator, in the same language as the request.")]
    public string Explanation { get; set; } = "";

    [Description("The main DocType draft.")]
    public DocTypeDef DocType { get; set; } = new();

    [Description("New child-table DocTypes (isChild = true) used by Table fields of the main DocType. Empty when none are needed.")]
    public List<DocTypeDef> ChildDocTypes { get; set; } = [];

    [Description("Optional approval workflow for the main DocType. Null when no workflow is needed.")]
    public WorkflowDef? Workflow { get; set; }
}

public sealed class WorkflowDesignAnswer
{
    [Description("Short explanation for the administrator, in the same language as the request.")]
    public string Explanation { get; set; } = "";

    public WorkflowDef Workflow { get; set; } = new();
}

public sealed class QueryDesignAnswer
{
    [Description("Short explanation for the administrator, in the same language as the request.")]
    public string Explanation { get; set; } = "";

    [Description("One PostgreSQL SELECT (or WITH ... SELECT) statement over reporting.* views. Parameters are written as @Name.")]
    public string Sql { get; set; } = "";

    [Description("Report templates only: optional one-row header query (e.g. company name, period). Null otherwise.")]
    public string? HeaderSql { get; set; }

    [Description("One entry per @Name parameter used in the SQL.")]
    public List<WidgetParameter> Parameters { get; set; } = [];

    [Description("Dashboard widgets only: suggested chart. Null for query APIs and report templates.")]
    public VisualConfig? Visual { get; set; }
}

/// <summary>What the generated SQL is for; decides which parameter bindings are allowed.</summary>
public enum QueryPurpose { Widget, QueryApi, ReportTemplate }

// ---------------------------------------------------------------------------------------------------------------
//  Results returned to the pages
// ---------------------------------------------------------------------------------------------------------------

public sealed record AiUsage(int Attempts, long? InputTokens, long? OutputTokens, TimeSpan Elapsed);

/// <summary>
/// Ok = the last answer passed server validation. When Ok is false the draft may still be returned so the
/// administrator can fix the remaining <c>Errors</c> by hand.
/// </summary>
public sealed record AiDocTypeResult(bool Ok, DocTypeDef? DocType, IReadOnlyList<DocTypeDef> ChildDocTypes, WorkflowDef? Workflow,
    string Explanation, IReadOnlyList<string> Errors, AiUsage Usage);

public sealed record AiWorkflowResult(bool Ok, WorkflowDef? Workflow, string Explanation, IReadOnlyList<string> Errors, AiUsage Usage);

public sealed record AiQueryResult(bool Ok, string Sql, string? HeaderSql, IReadOnlyList<WidgetParameter> Parameters, VisualConfig? Visual,
    IReadOnlyList<ResultColumn> Columns, string Explanation, IReadOnlyList<string> Errors, AiUsage Usage);

/// <summary>What <c>AiAssistPanel</c> shows after a request. <c>AppliedMessage</c> is the alert title (e.g. "Draft applied to the editor").</summary>
public sealed record AiPanelOutcome(bool Ok, string Explanation, IReadOnlyList<string> Errors, AiUsage? Usage, string? AppliedMessage = null);
