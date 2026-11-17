using System.Globalization;

namespace Framework.Assignments;

/// <summary>Lifecycle of a document assignment (ToDo).</summary>
public enum AssignmentStatus
{
    Open = 0,
    Completed = 1,
    Cancelled = 2
}

public enum AssignmentPriority
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// A document handed off to a user ("Assign To"). Unlike comments, assignments are mutable (Open → Completed/Cancelled),
/// so they live in their own store; every state change is still mirrored as an audit entry on the document timeline.
/// </summary>
public sealed record Assignment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TenantId { get; init; }
    public required string DocType { get; init; }
    public required string DocName { get; init; }
    public required string AssignedTo { get; init; }
    public required string AssignedBy { get; init; }
    public string? Description { get; init; }
    public DateOnly? DueDate { get; init; }
    public AssignmentPriority Priority { get; init; } = AssignmentPriority.Medium;
    public AssignmentStatus Status { get; init; } = AssignmentStatus.Open;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? ClosedBy { get; init; }
    public string? CloseNote { get; init; }
    /// <summary>Optimistic concurrency token, incremented on every update.</summary>
    public int Version { get; init; } = 1;

    public bool IsOpen => Status == AssignmentStatus.Open;
    public bool IsOverdue(DateOnly today) => IsOpen && DueDate is { } d && d < today;
    public bool IsDueToday(DateOnly today) => IsOpen && DueDate is { } d && d == today;
}

/// <summary>Structured store query. All filters are optional except the tenant.</summary>
public sealed record AssignmentQuery(
    string TenantId,
    string? AssignedTo = null,
    string? AssignedBy = null,
    IReadOnlyCollection<AssignmentStatus>? Statuses = null,
    string? DocType = null,
    string? DocName = null,
    DateOnly? DueOnOrBefore = null,
    int Take = AssignmentLimits.DefaultQueryTake);

public sealed record AssignmentCounts(int Open, int DueToday, int Overdue);

public static class AssignmentLimits
{
    public const int MaxDescriptionLength = 1000;
    public const int MaxCloseNoteLength = 1000;
    public const int MaxAssigneesPerRequest = 20;
    public const int DefaultQueryTake = 500;
    public const int MaxQueryTake = 2000;
}

/// <summary>Well-known audit actions written to the document timeline.</summary>
public static class AssignmentAuditActions
{
    public const string Assigned = "Assigned";
    public const string Completed = "Assignment Completed";
    public const string Cancelled = "Assignment Cancelled";
    public const string Updated = "Assignment Updated";

    public static bool IsAssignmentAction(string? action) =>
        action is Assigned or Completed or Cancelled or Updated;
}

/// <summary>Event "action" payload values of <see cref="Framework.Jobs.EventTypes.AssignmentChanged"/>.</summary>
public static class AssignmentEventActions
{
    public const string Assigned = "assigned";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Updated = "updated";
}

public static class AssignmentFormat
{
    public static string Priority(AssignmentPriority p) => p.ToString();

    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

public interface IAssignmentStore
{
    Task<Assignment?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    /// <summary>Ordered by status (open first), due date (nulls last), then newest first.</summary>
    Task<IReadOnlyList<Assignment>> QueryAsync(AssignmentQuery query, CancellationToken ct = default);
    /// <summary>Returns false when the user already has an open assignment on the same document.</summary>
    Task<bool> AddAsync(Assignment assignment, CancellationToken ct = default);
    /// <summary>Saves the assignment when the stored version equals <paramref name="expectedVersion"/>; returns false on conflict.</summary>
    Task<bool> UpdateAsync(Assignment assignment, int expectedVersion, CancellationToken ct = default);
    /// <summary>Open-task counters for a user, relative to <paramref name="today"/>.</summary>
    Task<AssignmentCounts> CountOpenAsync(string tenantId, string userId, DateOnly today, CancellationToken ct = default);
}

public sealed class InMemoryAssignmentStore : IAssignmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Assignment> _items = new();

    public Task<Assignment?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_items.TryGetValue(id, out var a) && a.TenantId == tenantId ? a : null);
    }

    public Task<IReadOnlyList<Assignment>> QueryAsync(AssignmentQuery q, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<Assignment> rows = _items.Values.Where(a => a.TenantId == q.TenantId);
            if (q.AssignedTo is not null) rows = rows.Where(a => string.Equals(a.AssignedTo, q.AssignedTo, StringComparison.OrdinalIgnoreCase));
            if (q.AssignedBy is not null) rows = rows.Where(a => string.Equals(a.AssignedBy, q.AssignedBy, StringComparison.OrdinalIgnoreCase));
            if (q.Statuses is { Count: > 0 }) rows = rows.Where(a => q.Statuses.Contains(a.Status));
            if (q.DocType is not null) rows = rows.Where(a => a.DocType == q.DocType);
            if (q.DocName is not null) rows = rows.Where(a => a.DocName == q.DocName);
            if (q.DueOnOrBefore is { } due) rows = rows.Where(a => a.DueDate is { } d && d <= due);
            var take = Math.Clamp(q.Take, 1, AssignmentLimits.MaxQueryTake);
            IReadOnlyList<Assignment> list = rows
                .OrderBy(a => a.Status == AssignmentStatus.Open ? 0 : 1)
                .ThenBy(a => a.DueDate is null ? 1 : 0)
                .ThenBy(a => a.DueDate)
                .ThenByDescending(a => a.CreatedAt)
                .Take(take)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<bool> AddAsync(Assignment assignment, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (assignment.IsOpen && _items.Values.Any(a => a.IsOpen && a.TenantId == assignment.TenantId
                && a.DocType == assignment.DocType && a.DocName == assignment.DocName
                && string.Equals(a.AssignedTo, assignment.AssignedTo, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(false);
            if (!_items.TryAdd(assignment.Id, assignment)) return Task.FromResult(false);
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateAsync(Assignment assignment, int expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(assignment.Id, out var current) || current.TenantId != assignment.TenantId || current.Version != expectedVersion)
                return Task.FromResult(false);
            _items[assignment.Id] = assignment with { Version = expectedVersion + 1 };
            return Task.FromResult(true);
        }
    }

    public Task<AssignmentCounts> CountOpenAsync(string tenantId, string userId, DateOnly today, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var open = _items.Values.Where(a => a.IsOpen && a.TenantId == tenantId
                && string.Equals(a.AssignedTo, userId, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(new AssignmentCounts(open.Count, open.Count(a => a.IsDueToday(today)), open.Count(a => a.IsOverdue(today))));
        }
    }
}
