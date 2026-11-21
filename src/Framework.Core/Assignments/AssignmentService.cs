using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Email;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Framework.Sharing;
using Framework.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Assignments;

public sealed record AssignmentNotificationOptions(bool SendEmail = false);

public sealed record TaskItem(Assignment Assignment, string DocTypeTitle, string? DocTitle, string? AssignedToName = null, string? AssignedByName = null);

public sealed record AssignDocumentsRequest(
    IReadOnlyList<string> Assignees,
    DateOnly? DueDate = null,
    AssignmentPriority Priority = AssignmentPriority.Medium,
    string? Description = null);

public sealed record UpdateAssignmentRequest(
    DateOnly? DueDate = null,
    bool UpdateDueDate = false,
    AssignmentPriority? Priority = null,
    string? AssignedTo = null,
    string? Description = null,
    bool UpdateDescription = false);

/// <summary>
/// Application service for document assignments ("Assign To" / ToDo).
/// Enforces tenant isolation, document read permission + row scope for both actor and assignee,
/// records every state change in the document audit trail, and emits outbox events.
/// </summary>
public sealed class AssignmentService(
    IAssignmentStore assignments,
    IDocumentStore docs,
    IMetadataProvider metadata,
    IPermissionService permissions,
    IUserDirectory users,
    TimeProvider clock,
    OutboxWakeSignal? outboxWake = null,
    Framework.Sharing.IDocShareStore? shares = null)
{
    /// <summary>
    /// Today in the application time zone (<see cref="Framework.Metadata.FieldTypeSettings.DefaultTimeZone"/>), not the
    /// server's: a container without TZ runs on UTC, which would move "due today" and "overdue" by seven hours in Jakarta.
    /// </summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Framework.Metadata.FieldTypeSettings.DefaultTimeZone).DateTime);

    private CompiledDocType TopLevel(string docType)
    {
        if (!metadata.Current.TryGet(docType, out var m) || m.Def.IsChild)
            throw new DocumentNotFoundException("DocType", docType);
        return m;
    }

    private async Task<Document> LoadInScopeAsync(UserContext user, CompiledDocType meta, string name, CancellationToken ct)
    {
        var doc = await docs.GetAsync(user.TenantId, meta.Name, name, ct);
        if (doc is null || !await permissions.InScopeAsync(shares, user, meta, DocActions.Read, doc, ct))
            throw new DocumentNotFoundException(meta.Title, name);
        return doc;
    }

    private static bool CanClose(UserContext user, Assignment a) =>
        user.IsAdmin
        || string.Equals(user.Id, a.AssignedTo, StringComparison.OrdinalIgnoreCase)
        || string.Equals(user.Id, a.AssignedBy, StringComparison.OrdinalIgnoreCase);

    private static bool CanEdit(UserContext user, Assignment a) =>
        user.IsAdmin
        || string.Equals(user.Id, a.AssignedBy, StringComparison.OrdinalIgnoreCase);

    /// <summary>Active tenant users who can read this document within their row scope.</summary>
    public async Task<IReadOnlyList<UserContext>> ListEligibleAssigneesAsync(UserContext actor, string docType, string docName, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(actor, meta, DocActions.Read);
        var doc = await LoadInScopeAsync(actor, meta, docName, ct);
        var tenantUsers = await users.ListAsync(actor.TenantId, ct);
        var eligible = new List<UserContext>();
        foreach (var u in tenantUsers)
            if (u.TenantId == actor.TenantId && permissions.Can(u, meta, DocActions.Read)
                && await permissions.InScopeAsync(shares, u, meta, DocActions.Read, doc, ct))
                eligible.Add(u);
        return eligible.OrderBy(u => u.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<Assignment>> ListForDocumentAsync(UserContext actor, string docType, string docName, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(actor, meta, DocActions.Read);
        _ = await LoadInScopeAsync(actor, meta, docName, ct);
        return await assignments.QueryAsync(new AssignmentQuery(actor.TenantId, DocType: meta.Name, DocName: docName), ct);
    }

    public async Task<IReadOnlyList<Assignment>> AssignAsync(
        UserContext actor,
        string docType,
        string docName,
        IReadOnlyList<string> assignees,
        DateOnly? dueDate = null,
        AssignmentPriority priority = AssignmentPriority.Medium,
        string? description = null,
        CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        permissions.Demand(actor, meta, DocActions.Read);
        var doc = await LoadInScopeAsync(actor, meta, docName, ct);

        var distinctAssignees = (assignees ?? [])
            .Select(a => a?.Trim() ?? "")
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fieldErrors = new List<FieldError>();
        if (distinctAssignees.Count == 0)
            fieldErrors.Add(new FieldError("assignees", "Select at least one assignee."));
        if (distinctAssignees.Count > AssignmentLimits.MaxAssigneesPerRequest)
            fieldErrors.Add(new FieldError("assignees", $"Cannot assign to more than {AssignmentLimits.MaxAssigneesPerRequest} users at once."));
        if (dueDate is { } d && d < Today)
            fieldErrors.Add(new FieldError("dueDate", "Due date cannot be in the past."));

        var trimmedDesc = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDesc is { Length: > AssignmentLimits.MaxDescriptionLength })
            fieldErrors.Add(new FieldError("description", $"Note cannot exceed {AssignmentLimits.MaxDescriptionLength} characters."));

        if (fieldErrors.Count > 0)
            throw new ValidationFailedException(fieldErrors);

        var tenantUsers = await users.ListAsync(actor.TenantId, ct);
        var userById = tenantUsers.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);

        var targets = new List<UserContext>(distinctAssignees.Count);
        foreach (var id in distinctAssignees)
        {
            if (!userById.TryGetValue(id, out var target) || target.TenantId != actor.TenantId)
            {
                fieldErrors.Add(new FieldError("assignees", $"User '{id}' was not found or is inactive."));
                continue;
            }
            if (!permissions.Can(target, meta, DocActions.Read) || !await permissions.InScopeAsync(shares, target, meta, DocActions.Read, doc, ct))
            {
                fieldErrors.Add(new FieldError("assignees", $"User '{target.FullName}' does not have access to this document."));
                continue;
            }
            targets.Add(target);
        }

        if (fieldErrors.Count > 0)
            throw new ValidationFailedException(fieldErrors);

        var now = clock.GetUtcNow();
        var created = new List<Assignment>();
        var uow = new UnitOfWork();

        foreach (var target in targets)
        {
            var a = new Assignment
            {
                Id = Guid.NewGuid(),
                TenantId = actor.TenantId,
                DocType = meta.Name,
                DocName = doc.Name,
                AssignedTo = target.Id,
                AssignedBy = actor.Id,
                Description = trimmedDesc,
                DueDate = dueDate,
                Priority = priority,
                Status = AssignmentStatus.Open,
                CreatedAt = now,
                Version = 1
            };

            if (!await assignments.AddAsync(a, ct))
                continue; // Already open for this user on this document

            created.Add(a);

            var changes = new List<FieldChange>
            {
                new("Assigned To", null, target.FullName),
                new("Priority", null, priority.ToString())
            };
            if (dueDate is { } dd)
                changes.Add(new("Due Date", null, AssignmentFormat.Date(dd)));

            uow.Audits.Add(new AuditEntry
            {
                TenantId = doc.TenantId,
                DocType = doc.DocType,
                DocName = doc.Name,
                Action = AssignmentAuditActions.Assigned,
                Actor = actor.FullName,
                At = now,
                Changes = changes,
                Comment = trimmedDesc,
                SchemaVersion = doc.SchemaVersion
            });
            uow.Outbox.Add(AssignmentEvents.Create(doc, a, AssignmentEventActions.Assigned, actor, now));
        }

        if (created.Count > 0)
        {
            await docs.CommitAsync(uow, ct);
            outboxWake?.Wake();
        }

        return created;
    }

    public Task<Assignment> CompleteAsync(UserContext actor, Guid assignmentId, string? note = null, CancellationToken ct = default) =>
        CloseAsync(actor, assignmentId, AssignmentStatus.Completed, note, ct);

    public Task<Assignment> CancelAsync(UserContext actor, Guid assignmentId, string? note = null, CancellationToken ct = default) =>
        CloseAsync(actor, assignmentId, AssignmentStatus.Cancelled, note, ct);

    private async Task<Assignment> CloseAsync(UserContext actor, Guid assignmentId, AssignmentStatus targetStatus, string? note, CancellationToken ct)
    {
        var current = await assignments.GetAsync(actor.TenantId, assignmentId, ct)
            ?? throw new DocumentNotFoundException("Assignment", assignmentId.ToString());

        if (!CanClose(actor, current))
            throw new PermissionDeniedException("You do not have permission to update this assignment.");

        if (!current.IsOpen)
            throw new InvalidStateException($"Assignment is already {current.Status}.");

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > AssignmentLimits.MaxCloseNoteLength })
            throw new ValidationFailedException([new FieldError("note", $"Note cannot exceed {AssignmentLimits.MaxCloseNoteLength} characters.")]);

        var now = clock.GetUtcNow();
        var updated = current with
        {
            Status = targetStatus,
            ClosedAt = now,
            ClosedBy = actor.Id,
            CloseNote = trimmedNote
        };

        if (!await assignments.UpdateAsync(updated, current.Version, ct))
            throw new ConcurrencyException(assignmentId.ToString(), current.Version, current.Version + 1);

        var persisted = updated with { Version = current.Version + 1 };
        await RecordTransitionAsync(actor, persisted,
            targetStatus == AssignmentStatus.Completed ? AssignmentAuditActions.Completed : AssignmentAuditActions.Cancelled,
            targetStatus == AssignmentStatus.Completed ? AssignmentEventActions.Completed : AssignmentEventActions.Cancelled,
            [new FieldChange("Assigned To", current.AssignedTo, null), new FieldChange("Status", current.Status.ToString(), targetStatus.ToString())],
            trimmedNote, now, previousAssignedTo: null, ct);

        return persisted;
    }

    public async Task<Assignment> UpdateAsync(UserContext actor, Guid assignmentId, UpdateAssignmentRequest request, CancellationToken ct = default)
    {
        var current = await assignments.GetAsync(actor.TenantId, assignmentId, ct)
            ?? throw new DocumentNotFoundException("Assignment", assignmentId.ToString());

        if (!CanEdit(actor, current))
            throw new PermissionDeniedException("Only the assigner or a System Manager can modify this assignment.");

        if (!current.IsOpen)
            throw new InvalidStateException($"Assignment is already {current.Status}.");

        var errors = new List<FieldError>();
        var nextDue = request.UpdateDueDate ? request.DueDate : current.DueDate;
        if (request.UpdateDueDate && request.DueDate is { } d && d < Today)
            errors.Add(new FieldError("dueDate", "Due date cannot be in the past."));

        var nextDesc = request.UpdateDescription
            ? (string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim())
            : current.Description;
        if (nextDesc is { Length: > AssignmentLimits.MaxDescriptionLength })
            errors.Add(new FieldError("description", $"Note cannot exceed {AssignmentLimits.MaxDescriptionLength} characters."));

        var nextPriority = request.Priority ?? current.Priority;
        var nextAssignee = string.IsNullOrWhiteSpace(request.AssignedTo) ? current.AssignedTo : request.AssignedTo.Trim();
        var reassigned = !string.Equals(nextAssignee, current.AssignedTo, StringComparison.OrdinalIgnoreCase);

        if (reassigned)
        {
            var meta = TopLevel(current.DocType);
            var doc = await LoadInScopeAsync(actor, meta, current.DocName, ct);
            var target = await users.FindAsync(nextAssignee, ct);
            if (target is null || target.TenantId != actor.TenantId)
                errors.Add(new FieldError("assignedTo", $"User '{nextAssignee}' was not found or is inactive."));
            else if (!permissions.Can(target, meta, DocActions.Read) || !await permissions.InScopeAsync(shares, target, meta, DocActions.Read, doc, ct))
                errors.Add(new FieldError("assignedTo", $"User '{target.FullName}' does not have access to this document."));
            else
            {
                nextAssignee = target.Id;
                var existingForDoc = await assignments.QueryAsync(new AssignmentQuery(actor.TenantId, AssignedTo: nextAssignee, Statuses: [AssignmentStatus.Open], DocType: current.DocType, DocName: current.DocName), ct);
                if (existingForDoc.Any(x => x.Id != current.Id))
                    errors.Add(new FieldError("assignedTo", $"User '{target.FullName}' already has an open assignment on this document."));
            }
        }

        if (errors.Count > 0)
            throw new ValidationFailedException(errors);

        var changes = new List<FieldChange>();
        if (reassigned)
            changes.Add(new("Assigned To", current.AssignedTo, nextAssignee));
        if (nextDue != current.DueDate)
            changes.Add(new("Due Date", AssignmentFormat.Date(current.DueDate), AssignmentFormat.Date(nextDue)));
        if (nextPriority != current.Priority)
            changes.Add(new("Priority", current.Priority.ToString(), nextPriority.ToString()));
        if (!string.Equals(nextDesc, current.Description, StringComparison.Ordinal))
            changes.Add(new("Note", current.Description, nextDesc));

        if (changes.Count == 0)
            return current;

        var updated = current with
        {
            AssignedTo = nextAssignee,
            DueDate = nextDue,
            Priority = nextPriority,
            Description = nextDesc
        };

        if (!await assignments.UpdateAsync(updated, current.Version, ct))
            throw new ConcurrencyException(assignmentId.ToString(), current.Version, current.Version + 1);

        var persisted = updated with { Version = current.Version + 1 };
        await RecordTransitionAsync(actor, persisted, AssignmentAuditActions.Updated, AssignmentEventActions.Updated,
            changes, nextDesc, clock.GetUtcNow(), reassigned ? current.AssignedTo : null, ct);

        return persisted;
    }

    private async Task RecordTransitionAsync(
        UserContext actor,
        Assignment assignment,
        string auditAction,
        string eventAction,
        IReadOnlyList<FieldChange> changes,
        string? comment,
        DateTimeOffset now,
        string? previousAssignedTo,
        CancellationToken ct)
    {
        var doc = await docs.GetAsync(assignment.TenantId, assignment.DocType, assignment.DocName, ct);
        if (doc is null) return;

        var uow = new UnitOfWork();
        uow.Audits.Add(new AuditEntry
        {
            TenantId = doc.TenantId,
            DocType = doc.DocType,
            DocName = doc.Name,
            Action = auditAction,
            Actor = actor.FullName,
            At = now,
            Changes = changes,
            Comment = comment,
            SchemaVersion = doc.SchemaVersion
        });
        uow.Outbox.Add(AssignmentEvents.Create(doc, assignment, eventAction, actor, now, previousAssignedTo));
        await docs.CommitAsync(uow, ct);
        outboxWake?.Wake();
    }

    public async Task<IReadOnlyList<TaskItem>> ListMyTasksAsync(
        UserContext actor,
        AssignmentStatus? status = AssignmentStatus.Open,
        bool onlyOverdue = false,
        bool onlyDueToday = false,
        string? search = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default)
    {
        var statuses = status is { } s ? [s] : Array.Empty<AssignmentStatus>();
        var raw = await assignments.QueryAsync(new AssignmentQuery(actor.TenantId, AssignedTo: actor.Id, Statuses: statuses, Take: AssignmentLimits.MaxQueryTake), ct);
        return await FilterVisibleTasksAsync(actor, raw, onlyOverdue, onlyDueToday, search, skip, take, ct);
    }

    public async Task<IReadOnlyList<TaskItem>> ListAssignedByMeAsync(
        UserContext actor,
        AssignmentStatus? status = null,
        string? search = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default)
    {
        var statuses = status is { } s ? [s] : Array.Empty<AssignmentStatus>();
        var raw = await assignments.QueryAsync(new AssignmentQuery(actor.TenantId, AssignedBy: actor.Id, Statuses: statuses, Take: AssignmentLimits.MaxQueryTake), ct);
        return await FilterVisibleTasksAsync(actor, raw, onlyOverdue: false, onlyDueToday: false, search, skip, take, ct);
    }

    public async Task<AssignmentCounts> CountMyTasksAsync(UserContext actor, CancellationToken ct = default)
    {
        var visible = await ListMyTasksAsync(actor, AssignmentStatus.Open, take: AssignmentLimits.MaxQueryTake, ct: ct);
        var today = Today;
        return new AssignmentCounts(
            visible.Count,
            visible.Count(x => x.Assignment.IsDueToday(today)),
            visible.Count(x => x.Assignment.IsOverdue(today)));
    }

    private async Task<IReadOnlyList<TaskItem>> FilterVisibleTasksAsync(
        UserContext actor,
        IReadOnlyList<Assignment> raw,
        bool onlyOverdue,
        bool onlyDueToday,
        string? search,
        int skip,
        int take,
        CancellationToken ct)
    {
        if (raw.Count == 0) return [];

        var today = Today;
        var filtered = raw.Where(a =>
            (!onlyOverdue || a.IsOverdue(today)) &&
            (!onlyDueToday || a.IsDueToday(today)));

        var docCache = new Dictionary<(string DocType, string DocName), (CompiledDocType? Meta, Document? Doc, bool Allowed)>();
        var tenantUsers = await users.ListAsync(actor.TenantId, ct);
        var userNames = tenantUsers.ToDictionary(u => u.Id, u => u.FullName, StringComparer.OrdinalIgnoreCase);

        var q = search?.Trim();
        var results = new List<TaskItem>();
        foreach (var a in filtered)
        {
            var key = (a.DocType, a.DocName);
            if (!docCache.TryGetValue(key, out var cached))
            {
                if (!metadata.Current.TryGet(a.DocType, out var meta) || meta.Def.IsChild || !permissions.Can(actor, meta, DocActions.Read))
                {
                    cached = (null, null, false);
                }
                else
                {
                    var doc = await docs.GetAsync(actor.TenantId, a.DocType, a.DocName, ct);
                    var allowed = doc is not null && await permissions.InScopeAsync(shares, actor, meta, DocActions.Read, doc, ct);
                    cached = (meta, doc, allowed);
                }
                docCache[key] = cached;
            }

            if (!cached.Allowed || cached.Meta is null || cached.Doc is null)
                continue;

            var docTypeTitle = cached.Meta.Title;
            string? docTitle = null;
            if (cached.Meta.Def.ListView.TitleField is { } tf && cached.Doc.Data.TryGetValue(tf, out var titleVal) && titleVal is not null)
                docTitle = Convert.ToString(titleVal, CultureInfo.InvariantCulture);

            var assignedToName = userNames.GetValueOrDefault(a.AssignedTo, a.AssignedTo);
            var assignedByName = userNames.GetValueOrDefault(a.AssignedBy, a.AssignedBy);

            if (!string.IsNullOrEmpty(q))
            {
                var matches = a.DocName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || docTypeTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(docTitle) && docTitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(a.Description) && a.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                    || assignedToName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || assignedByName.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
            }

            results.Add(new TaskItem(a, docTypeTitle, docTitle, assignedToName, assignedByName));
        }

        if (skip < 0) skip = 0;
        if (take <= 0) take = 50;
        return results.Skip(skip).Take(take).ToList();
    }
}

internal static class AssignmentEvents
{
    public static OutboxMessage Create(Document doc, Assignment a, string action, UserContext actor, DateTimeOffset now, string? previousAssignedTo = null) => new()
    {
        TenantId = doc.TenantId,
        Type = EventTypes.AssignmentChanged,
        CreatedAt = now,
        IdempotencyKey = $"{doc.TenantId}:assignment:{a.Id}:v{a.Version}:{action}:{now.ToUnixTimeMilliseconds()}",
        Payload = new()
        {
            ["assignmentId"] = a.Id.ToString(),
            ["action"] = action,
            ["docType"] = doc.DocType,
            ["name"] = doc.Name,
            ["assignedTo"] = a.AssignedTo,
            ["assignedBy"] = a.AssignedBy,
            ["previousAssignedTo"] = previousAssignedTo,
            ["dueDate"] = AssignmentFormat.Date(a.DueDate),
            ["priority"] = a.Priority.ToString(),
            ["description"] = a.Description,
            ["closeNote"] = a.CloseNote,
            ["actor"] = actor.Id,
            ["actorName"] = actor.FullName,
            ["occurredAt"] = now.ToString("O", CultureInfo.InvariantCulture)
        }
    };
}

/// <summary>
/// Turns assignment outbox events into in-app notifications (and optional emails when <c>Assignments:SendEmail</c> is true).
/// Checks tenant membership and document read permission + row scope before notifying.
/// </summary>
public sealed class AssignmentNotifier(
    IUserDirectory users,
    INotificationStore inbox,
    IDocumentStore store,
    IMetadataProvider metadata,
    IPermissionService permissions,
    TimeProvider clock,
    INotificationLiveFeed? liveFeed = null,
    IEmailQueue? emailQueue = null,
    AssignmentNotificationOptions? options = null,
    Framework.Sharing.IDocShareStore? shares = null) : IEventHandler
{
    public bool CanHandle(string eventType) => eventType == EventTypes.AssignmentChanged;

    private bool SendEmail => options?.SendEmail ?? false;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = message.Payload;
        var action = p.GetValueOrDefault("action");
        var assignmentId = p.GetValueOrDefault("assignmentId") ?? message.Id.ToString();
        var docType = p.GetValueOrDefault("docType");
        var name = p.GetValueOrDefault("name");
        var assignedTo = p.GetValueOrDefault("assignedTo");
        var assignedBy = p.GetValueOrDefault("assignedBy");
        var previousAssignedTo = p.GetValueOrDefault("previousAssignedTo");
        var actor = p.GetValueOrDefault("actor");
        var actorName = p.GetValueOrDefault("actorName") ?? actor ?? "Someone";
        var dueDate = p.GetValueOrDefault("dueDate");
        var priority = p.GetValueOrDefault("priority");
        var description = p.GetValueOrDefault("description");
        var closeNote = p.GetValueOrDefault("closeNote");

        if (action is null || docType is null || name is null) return;
        if (!metadata.Current.TryGet(docType, out var meta)) return;

        var doc = await store.GetAsync(message.TenantId, docType, name, ct);
        if (doc is null) return;

        var title = string.IsNullOrWhiteSpace(meta.Title) ? docType : meta.Title;
        var tenantUsers = await users.ListAsync(message.TenantId, ct);

        async Task NotifyAsync(string? recipientId, string subject, string body, string keySuffix)
        {
            if (string.IsNullOrWhiteSpace(recipientId)) return;
            var target = tenantUsers.FirstOrDefault(u => string.Equals(u.Id, recipientId, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.TenantId != message.TenantId) return;
            if (string.Equals(target.Id, actor, StringComparison.OrdinalIgnoreCase)) return;
            if (!permissions.Can(target, meta, DocActions.Read)) return;
            if (!await permissions.InScopeAsync(shares, target, meta, DocActions.Read, doc, ct)) return;

            var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{message.TenantId}|assignment|{assignmentId}|{keySuffix}|{target.Id}")));
            var notification = new Notification(id, message.TenantId, target.Id, subject, body, docType, name, clock.GetUtcNow());
            inbox.Add(notification);
            if (liveFeed is not null)
                await liveFeed.PublishAsync(notification, ct);

            if (SendEmail && emailQueue is not null && target.Attr("email") is { } email && !string.IsNullOrWhiteSpace(email))
            {
                await emailQueue.QueueAsync(message.TenantId, new EmailMessage
                {
                    To = [email],
                    Subject = subject,
                    BodyText = body,
                    ReferenceDocType = docType,
                    ReferenceName = name
                }, idempotencyKey: $"assignment-email:{assignmentId}:{keySuffix}:{target.Id}", ct: ct);
            }
        }

        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(priority)) detailParts.Add($"Priority: {priority}");
        if (!string.IsNullOrWhiteSpace(dueDate)) detailParts.Add($"Due: {dueDate}");
        if (!string.IsNullOrWhiteSpace(description)) detailParts.Add(description);
        var assignBody = detailParts.Count > 0 ? string.Join(" · ", detailParts) : $"Document {title} {name}";

        switch (action)
        {
            case AssignmentEventActions.Assigned:
                await NotifyAsync(assignedTo, $"{actorName} assigned {title} {name} to you", assignBody, "assigned");
                break;

            case AssignmentEventActions.Updated:
                if (!string.IsNullOrWhiteSpace(previousAssignedTo) && !string.Equals(previousAssignedTo, assignedTo, StringComparison.OrdinalIgnoreCase))
                    await NotifyAsync(assignedTo, $"{actorName} assigned {title} {name} to you", assignBody, $"reassigned:{message.IdempotencyKey}");
                else
                    await NotifyAsync(assignedTo, $"{actorName} updated your assignment on {title} {name}", assignBody, $"updated:{message.IdempotencyKey}");
                break;

            case AssignmentEventActions.Completed:
                var completeBody = string.IsNullOrWhiteSpace(closeNote) ? $"Task on {title} {name} was marked complete." : closeNote;
                await NotifyAsync(assignedBy, $"{actorName} completed assignment on {title} {name}", completeBody, "completed");
                break;

            case AssignmentEventActions.Cancelled:
                var cancelBody = string.IsNullOrWhiteSpace(closeNote) ? $"Assignment on {title} {name} was cancelled." : closeNote;
                if (string.Equals(actor, assignedTo, StringComparison.OrdinalIgnoreCase))
                    await NotifyAsync(assignedBy, $"{actorName} declined assignment on {title} {name}", cancelBody, "declined");
                else
                    await NotifyAsync(assignedTo, $"{actorName} cancelled your assignment on {title} {name}", cancelBody, "cancelled");
                break;
        }
    }
}

/// <summary>Automatically cancels open assignments when their parent document is deleted.</summary>
public sealed class AssignmentDocumentCleanupHandler(IAssignmentStore assignments, TimeProvider clock) : IEventHandler
{
    public bool CanHandle(string eventType) => eventType == EventTypes.DocumentDeleted;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var docType = message.Payload.GetValueOrDefault("docType");
        var docName = message.Payload.GetValueOrDefault("name");
        var actor = message.Payload.GetValueOrDefault("actor") ?? "system";
        if (string.IsNullOrWhiteSpace(docType) || string.IsNullOrWhiteSpace(docName)) return;

        var open = await assignments.QueryAsync(new AssignmentQuery(
            message.TenantId,
            Statuses: [AssignmentStatus.Open],
            DocType: docType,
            DocName: docName,
            Take: AssignmentLimits.MaxQueryTake), ct);

        var now = clock.GetUtcNow();
        foreach (var a in open)
        {
            await assignments.UpdateAsync(a with
            {
                Status = AssignmentStatus.Cancelled,
                ClosedAt = now,
                ClosedBy = actor,
                CloseNote = "Document deleted"
            }, a.Version, ct);
        }
    }
}

/// <summary>Daily scheduled job that reminds assignees of tasks due today or overdue (at most once per task per day).</summary>
public sealed class AssignmentReminderJob : IScheduledJob
{
    public string Name => "Framework.AssignmentReminder";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var sp = context.Services;
        var assignments = sp.GetRequiredService<IAssignmentStore>();
        var docs = sp.GetRequiredService<IDocumentStore>();
        var metadata = sp.GetRequiredService<IMetadataProvider>();
        var permissions = sp.GetRequiredService<IPermissionService>();
        var shares = sp.GetService<Framework.Sharing.IDocShareStore>();
        var users = sp.GetRequiredService<IUserDirectory>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var liveFeed = sp.GetService<INotificationLiveFeed>();
        var emailQueue = sp.GetService<IEmailQueue>();
        var sendEmail = sp.GetService<AssignmentNotificationOptions>()?.SendEmail ?? false;

        var localTz = (sp.GetService<TimeProvider>() ?? TimeProvider.System).LocalTimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.ScheduledTime, localTz).DateTime);
        var due = await assignments.QueryAsync(new AssignmentQuery(
            context.TenantId,
            Statuses: [AssignmentStatus.Open],
            DueOnOrBefore: today,
            Take: AssignmentLimits.MaxQueryTake), ct);

        if (due.Count == 0) return;

        var tenantUsers = (await users.ListAsync(context.TenantId, ct))
            .ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var a in due)
        {
            if (!tenantUsers.TryGetValue(a.AssignedTo, out var target) || target.TenantId != context.TenantId)
                continue;
            if (!metadata.Current.TryGet(a.DocType, out var meta))
                continue;
            var doc = await docs.GetAsync(context.TenantId, a.DocType, a.DocName, ct);
            if (doc is null)
                continue;
            if (!permissions.Can(target, meta, DocActions.Read) || !await permissions.InScopeAsync(shares, target, meta, DocActions.Read, doc, ct))
                continue;

            var title = string.IsNullOrWhiteSpace(meta.Title) ? a.DocType : meta.Title;
            var subject = a.IsOverdue(today)
                ? $"Overdue task: {title} {a.DocName} (due {AssignmentFormat.Date(a.DueDate)})"
                : $"Task due today: {title} {a.DocName}";
            var body = !string.IsNullOrWhiteSpace(a.Description)
                ? a.Description
                : $"Priority: {a.Priority} · Assigned by {a.AssignedBy}";

            var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{context.TenantId}|assignment-reminder|{a.Id}|{today:yyyy-MM-dd}|{target.Id}")));
            var notification = new Notification(id, context.TenantId, target.Id, subject, body, a.DocType, a.DocName, context.ScheduledTime);
            inbox.Add(notification);
            if (liveFeed is not null)
                await liveFeed.PublishAsync(notification, ct);

            if (sendEmail && emailQueue is not null && target.Attr("email") is { } email && !string.IsNullOrWhiteSpace(email))
            {
                await emailQueue.QueueAsync(context.TenantId, new EmailMessage
                {
                    To = [email],
                    Subject = subject,
                    BodyText = body,
                    ReferenceDocType = a.DocType,
                    ReferenceName = a.DocName
                }, idempotencyKey: $"assignment-reminder-email:{a.Id}:{today:yyyy-MM-dd}:{target.Id}", ct: ct);
            }
        }
    }
}
