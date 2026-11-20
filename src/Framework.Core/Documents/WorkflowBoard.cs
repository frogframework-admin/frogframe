using Framework.Metadata;

namespace Framework.Documents;

/// <summary>
/// Board (Kanban) view of a list: one column per workflow state, in the order the workflow defines them. A DocType
/// without a workflow but with submit enabled gets Draft / Submitted (/ Cancelled) columns. Moving a card between
/// columns runs the action that leads to the target column; which actions exist is decided by
/// <see cref="DocumentService.AvailableActionsAsync(Framework.Security.UserContext, string, string, CancellationToken)"/>
/// (roles, transition conditions, row scope) and enforced again by <see cref="DocumentService.RunActionAsync"/>.
/// </summary>
public static class WorkflowBoard
{
    /// <summary>One board column. <paramref name="Key"/> is the workflow state name (or the DocStatus label).</summary>
    public sealed record Column(string Key, int DocStatus, string Tone);

    /// <summary>The board is offered for top-level DocTypes that have a workflow or submit enabled.</summary>
    public static bool Supports(CompiledDocType meta) =>
        !meta.Def.IsSingle && !meta.Def.IsChild && (meta.Workflow is not null || meta.Def.Document.AllowSubmit);

    public static IReadOnlyList<Column> Columns(CompiledDocType meta)
    {
        if (meta.Workflow is { } wf)
            return wf.Def.States.Select(s => new Column(s.Name, s.DocStatus, s.Tone)).ToList();
        var cols = new List<Column> { new("Draft", DocStatus.Draft, "neutral") };
        if (meta.Def.Document.AllowSubmit) cols.Add(new("Submitted", DocStatus.Submitted, "positive"));
        if (meta.Def.Document.AllowCancel) cols.Add(new("Cancelled", DocStatus.Cancelled, "negative"));
        return cols;
    }

    /// <summary>
    /// Column of a list row. A workflow document without a state (created before the workflow was attached) is
    /// placed like the engine treats it: drafts in the initial state, otherwise the first state with its DocStatus.
    /// </summary>
    public static string ColumnOf(CompiledDocType meta, string? workflowState, int docStatus)
    {
        if (meta.Workflow is not { } wf) return DocStatus.Label(docStatus);
        if (!string.IsNullOrEmpty(workflowState)) return workflowState;
        if (docStatus == DocStatus.Draft && wf.Def.InitialState.Length > 0) return wf.Def.InitialState;
        return wf.Def.States.FirstOrDefault(s => s.DocStatus == docStatus)?.Name ?? DocStatus.Label(docStatus);
    }

    /// <summary>Column of a row returned by <see cref="DocumentService.ListAsync"/>.</summary>
    public static string ColumnOf(CompiledDocType meta, IReadOnlyDictionary<string, object?> row) =>
        ColumnOf(meta, row.GetValueOrDefault(StandardFields.WorkflowState) as string, row.GetValueOrDefault(StandardFields.DocStatus) switch
        {
            int i => i,
            long l => (int)l,
            decimal d => (int)d,
            _ => DocStatus.Draft,
        });

    /// <summary>
    /// Actions that move a card into <paramref name="column"/>. Amend is left out: it creates a new revision
    /// instead of moving the document.
    /// </summary>
    public static IReadOnlyList<ActionView> MovesTo(IEnumerable<ActionView> available, string column) =>
        available.Where(a => a.Action != DocActions.Amend && string.Equals(a.ToState, column, StringComparison.Ordinal)).ToList();
}
