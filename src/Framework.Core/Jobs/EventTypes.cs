namespace Framework.Jobs;

/// <summary>
/// Central catalog of document lifecycle and workflow event type names dispatched to the transactional outbox.
/// </summary>
public static class EventTypes
{
    public const string DocumentInserted = "document.inserted";
    public const string DocumentUpdated = "document.updated";
    public const string DocumentDeleted = "document.deleted";
    public const string DocumentAmended = "document.amended";
    public const string WorkflowTransitioned = "workflow.transitioned";

    public const string DocumentSubmit = "document.submit";
    public const string DocumentCancel = "document.cancel";

    /// <summary>
    /// Generates a standardized lowercase event name for document actions, e.g. "document.submit", "document.cancel".
    /// </summary>
    public static string DocumentAction(string action) => $"document.{action.Trim().ToLowerInvariant()}";

    /// <summary>
    /// All standard lifecycle events recognized by the core platform.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        DocumentInserted,
        DocumentUpdated,
        DocumentDeleted,
        DocumentAmended,
        WorkflowTransitioned,
        DocumentSubmit,
        DocumentCancel
    ];
}

