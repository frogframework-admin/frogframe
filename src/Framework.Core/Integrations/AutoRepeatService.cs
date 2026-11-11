using System.Text.Json;
using Framework.Documents;
using Framework.Email;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Integrations;

public sealed record SaveAutoRepeatRequest(
    string? Id,
    string Name,
    string ReferenceDocType,
    string ReferenceDocName,
    AutoRepeatFrequency Frequency,
    DateOnly StartDate,
    DateOnly? EndDate,
    string? RunAsUserId = null,
    bool NotifyByEmail = false,
    IReadOnlyList<string>? Recipients = null,
    bool SubmitOnCreation = false,
    AutoRepeatStatus? Status = null
);

public sealed record AutoRepeatExecutionSummary(
    string AutoRepeatId,
    int DocumentsGenerated,
    IReadOnlyList<string> GeneratedDocNames,
    DateOnly NextScheduleDate,
    AutoRepeatStatus Status,
    string? Error = null
);

public sealed class AutoRepeatService(
    IAutoRepeatStore store,
    DocumentService docs,
    IMetadataProvider metadata,
    IAutomationIdentity automationIdentity,
    IPermissionService permissions,
    IEmailQueue? emailQueue = null)
{
    public const int MaxCatchUpPerCycle = 12;

    private static readonly HashSet<string> SystemFieldsToStrip = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "creation", "modified", "modified_by", "owner",
        "docstatus", "version", "idx", "amended_from"
    };

    private static readonly HashSet<string> ChildSystemFieldsToStrip = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "parent", "parenttype", "parentfield", "idx",
        "creation", "modified", "modified_by", "owner", "docstatus"
    };

    public Task<IReadOnlyList<AutoRepeatDef>> ListAsync(UserContext user, string? docType = null, AutoRepeatStatus? status = null, CancellationToken ct = default) =>
        store.ListAsync(user.TenantId, docType, status, ct);

    public Task<AutoRepeatDef?> GetAsync(UserContext user, string id, CancellationToken ct = default) =>
        store.GetAsync(user.TenantId, id, ct);

    public Task<AutoRepeatDef?> GetByReferenceAsync(UserContext user, string docType, string docName, CancellationToken ct = default) =>
        store.GetByReferenceAsync(user.TenantId, docType, docName, ct);

    public async Task<AutoRepeatDef> SaveAsync(UserContext user, SaveAutoRepeatRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Name is required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.ReferenceDocType)) throw new ArgumentException("Reference DocType is required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.ReferenceDocName)) throw new ArgumentException("Reference DocName is required.", nameof(req));

        if (!metadata.Current.TryGet(req.ReferenceDocType, out var meta))
        {
            throw new InvalidOperationException($"DocType '{req.ReferenceDocType}' was not found.");
        }

        if (!meta.AllowAutoRepeat)
        {
            throw new InvalidOperationException($"DocType '{req.ReferenceDocType}' does not allow Auto Repeat.");
        }

        if (!permissions.Can(user, meta, DocActions.Create) && !user.IsAdmin)
        {
            throw new PermissionDeniedException($"You do not have create permission for {req.ReferenceDocType}.");
        }

        if (req.EndDate.HasValue && req.EndDate.Value < req.StartDate)
        {
            throw new ArgumentException("End date cannot be earlier than start date.", nameof(req));
        }

        var existing = !string.IsNullOrEmpty(req.Id) ? await store.GetAsync(user.TenantId, req.Id, ct) : null;

        // A non-admin may only change schedules that run as themselves.
        if (existing is not null && !user.IsAdmin && !string.Equals(existing.RunAsUserId, user.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PermissionDeniedException("You can only change Auto Repeat schedules that run as yourself.");
        }

        // Documents are generated as the run-as user. Only administrators may choose someone else,
        // otherwise any user could create documents (and auto-submit them) with another user's permissions.
        var runAsUserId = string.IsNullOrWhiteSpace(req.RunAsUserId) ? user.Id : req.RunAsUserId.Trim();
        if (!string.Equals(runAsUserId, user.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (!user.IsAdmin)
                throw new PermissionDeniedException("Only System Managers can run an Auto Repeat schedule as another user.");
            await automationIdentity.ForUserAsync(user.TenantId, runAsUserId, ct); // must exist and be active
        }

        // The creator must be able to read the source document (permission and row scope); throws otherwise.
        await docs.GetAsync(user, req.ReferenceDocType, req.ReferenceDocName, ct);

        var id = existing?.Id ?? req.Id ?? $"rep_{Guid.NewGuid().ToString("N")[..8]}";
        var nextDate = existing is not null
            ? (req.StartDate > existing.NextScheduleDate ? req.StartDate : existing.NextScheduleDate)
            : req.StartDate;

        var def = new AutoRepeatDef
        {
            Id = id,
            TenantId = user.TenantId,
            Name = req.Name.Trim(),
            ReferenceDocType = req.ReferenceDocType,
            ReferenceDocName = req.ReferenceDocName,
            Frequency = req.Frequency,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            NextScheduleDate = nextDate,
            Status = req.Status ?? existing?.Status ?? AutoRepeatStatus.Active,
            RunAsUserId = runAsUserId,
            NotifyByEmail = req.NotifyByEmail,
            Recipients = req.Recipients ?? [],
            SubmitOnCreation = req.SubmitOnCreation,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(def, ct);
        return def;
    }

    public async Task DeleteAsync(UserContext user, string id, CancellationToken ct = default)
    {
        var existing = await store.GetAsync(user.TenantId, id, ct);
        if (existing is null) return;

        if (!user.IsAdmin && !string.Equals(existing.RunAsUserId, user.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PermissionDeniedException($"You do not have permission to delete Auto Repeat schedule for {existing.ReferenceDocType}.");
        }

        await store.DeleteAsync(user.TenantId, id, ct);
    }

    public async Task<IReadOnlyList<AutoRepeatExecutionSummary>> ExecuteDueAsync(string tenantId, DateOnly? asOfDate = null, CancellationToken ct = default)
    {
        var today = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var dueList = await store.ListDueAsync(tenantId, today, ct);
        var summaries = new List<AutoRepeatExecutionSummary>();

        foreach (var repeat in dueList)
        {
            var summary = await ExecuteSingleRepeatAsync(repeat, today, ct);
            summaries.Add(summary);
        }

        return summaries;
    }

    public async Task<AutoRepeatExecutionSummary> ExecuteRepeatNowAsync(UserContext user, string autoRepeatId, CancellationToken ct = default)
    {
        var repeat = await store.GetAsync(user.TenantId, autoRepeatId, ct)
            ?? throw new KeyNotFoundException($"Auto Repeat '{autoRepeatId}' not found.");

        if (!user.IsAdmin && !string.Equals(repeat.RunAsUserId, user.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PermissionDeniedException("You can only run Auto Repeat schedules that run as yourself.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await ExecuteSingleRepeatAsync(repeat, today, ct);
    }

    private async Task<AutoRepeatExecutionSummary> ExecuteSingleRepeatAsync(AutoRepeatDef repeat, DateOnly today, CancellationToken ct)
    {
        var generatedDocs = new List<string>();
        string? failureError = null;
        var curDate = repeat.NextScheduleDate;
        var catchUpCount = 0;

        try
        {
            var runAsUser = await automationIdentity.ForUserAsync(repeat.TenantId, repeat.RunAsUserId, ct);

            if (!metadata.Current.TryGet(repeat.ReferenceDocType, out var meta))
            {
                throw new InvalidOperationException($"DocType '{repeat.ReferenceDocType}' was not found.");
            }

            var sourceDoc = await docs.GetAsync(runAsUser, repeat.ReferenceDocType, repeat.ReferenceDocName, ct);
            if (sourceDoc is null)
            {
                throw new InvalidOperationException($"Reference document '{repeat.ReferenceDocType}/{repeat.ReferenceDocName}' not found.");
            }

            while (curDate <= today && (!repeat.EndDate.HasValue || curDate <= repeat.EndDate.Value) && catchUpCount < MaxCatchUpPerCycle)
            {
                // Claim the period BEFORE creating its document. If it is already done or in progress
                // (concurrent "Run now", a second instance, or a crash after the insert), skip it: a missing
                // document is visible in the run log, a duplicate one is not.
                if (!await store.TryClaimRunAsync(repeat.TenantId, repeat.Id, curDate, ct))
                {
                    curDate = AutoRepeatSchedule.Next(curDate, repeat.Frequency);
                    continue;
                }

                var payload = CloneDocumentPayload(meta, sourceDoc, curDate);

                Document inserted;
                try
                {
                    inserted = await docs.InsertAsync(runAsUser, repeat.ReferenceDocType, payload, ct);
                }
                catch (Exception ex)
                {
                    // Nothing was created: mark the period Failed so a later run may claim and retry it.
                    failureError = ex.Message;
                    await store.CompleteRunAsync(new AutoRepeatRun
                    {
                        Id = Guid.NewGuid(),
                        TenantId = repeat.TenantId,
                        AutoRepeatId = repeat.Id,
                        ScheduledFor = curDate,
                        GeneratedDocName = string.Empty,
                        Status = AutoRepeatRunStatus.Failed,
                        Error = ex.Message,
                        ExecutedAt = DateTimeOffset.UtcNow
                    }, ct);
                    break;
                }

                generatedDocs.Add(inserted.Name);

                string? submitError = null;
                if (repeat.SubmitOnCreation && meta.Def.Workflow is null && !meta.Def.IsSingle)
                {
                    try
                    {
                        await docs.RunActionAsync(runAsUser, repeat.ReferenceDocType, inserted.Name, "submit", inserted.Version, "Auto Repeat auto-submission", ct);
                    }
                    catch (Exception ex)
                    {
                        // The document stays a draft; keep the reason in the run log instead of hiding it.
                        submitError = $"Document created as draft; auto-submit failed: {ex.Message}";
                    }
                }

                // The document exists now. If this update fails the period stays 'Claimed' and is never retried,
                // which prevents a duplicate document.
                await store.CompleteRunAsync(new AutoRepeatRun
                {
                    Id = Guid.NewGuid(),
                    TenantId = repeat.TenantId,
                    AutoRepeatId = repeat.Id,
                    ScheduledFor = curDate,
                    GeneratedDocName = inserted.Name,
                    Status = AutoRepeatRunStatus.Success,
                    Error = submitError,
                    ExecutedAt = DateTimeOffset.UtcNow
                }, ct);

                catchUpCount++;
                curDate = AutoRepeatSchedule.Next(curDate, repeat.Frequency);
            }

            var isCompleted = AutoRepeatSchedule.IsCompleted(repeat, curDate);
            var updatedStatus = isCompleted ? AutoRepeatStatus.Completed : repeat.Status;

            var updatedDef = repeat with
            {
                NextScheduleDate = curDate,
                Status = updatedStatus,
                ModifiedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(updatedDef, ct);

            // Optional email notification for generated documents
            if (repeat.NotifyByEmail && repeat.Recipients.Count > 0 && generatedDocs.Count > 0 && emailQueue is not null)
            {
                try
                {
                    var emailMsg = new EmailMessage
                    {
                        To = repeat.Recipients.ToList(),
                        Subject = $"Auto Repeat: {generatedDocs.Count} document(s) generated for {repeat.Name}",
                        BodyText = $"Auto Repeat '{repeat.Name}' has automatically generated the following document(s):\n" +
                                   string.Join("\n", generatedDocs.Select(d => $"• {repeat.ReferenceDocType}: {d}")) +
                                   $"\n\nNext scheduled run: {curDate:yyyy-MM-dd}"
                    };
                    await emailQueue.QueueAsync(repeat.TenantId, emailMsg, $"autorepeat:{repeat.Id}:{today:yyyyMMdd}", ct);
                }
                catch
                {
                    // Email delivery failure does not roll back document generation
                }
            }

            return new AutoRepeatExecutionSummary(
                AutoRepeatId: repeat.Id,
                DocumentsGenerated: generatedDocs.Count,
                GeneratedDocNames: generatedDocs,
                NextScheduleDate: curDate,
                Status: updatedStatus,
                Error: failureError
            );
        }
        catch (Exception ex)
        {
            return new AutoRepeatExecutionSummary(
                AutoRepeatId: repeat.Id,
                DocumentsGenerated: generatedDocs.Count,
                GeneratedDocNames: generatedDocs,
                NextScheduleDate: curDate,
                Status: repeat.Status,
                Error: ex.Message
            );
        }
    }

    public Dictionary<string, object?> CloneDocumentPayload(CompiledDocType meta, Document source, DateOnly scheduledDate)
    {
        var cloned = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in meta.Fields)
        {
            // Skip fields with NoCopy = true
            if (field.Def.NoCopy) continue;

            // Strip system fields
            if (SystemFieldsToStrip.Contains(field.Name)) continue;

            if (field.Def.Type == "Table")
            {
                if (source.Data.TryGetValue(field.Name, out var tableVal) && tableVal is not null)
                {
                    CompiledDocType? childMeta = null;
                    if (!string.IsNullOrEmpty(field.Def.ChildDocType))
                    {
                        metadata.Current.TryGet(field.Def.ChildDocType, out childMeta);
                    }

                    cloned[field.Name] = CloneTableRows(tableVal, childMeta);
                }
                continue;
            }

            // Set date fields to the scheduled date if matching standard naming conventions
            if (field.Def.Type == "Date" && (field.Name.Equals("posting_date", StringComparison.OrdinalIgnoreCase) ||
                                             field.Name.Equals("request_date", StringComparison.OrdinalIgnoreCase) ||
                                             field.Name.Equals("transaction_date", StringComparison.OrdinalIgnoreCase)))
            {
                cloned[field.Name] = scheduledDate.ToString("yyyy-MM-dd");
                continue;
            }

            // Offset relative due date fields (due_date, required_by) if original had a date offset
            if (field.Def.Type == "Date" && (field.Name.Equals("due_date", StringComparison.OrdinalIgnoreCase) ||
                                             field.Name.Equals("required_by", StringComparison.OrdinalIgnoreCase)))
            {
                if (source.Data.TryGetValue(field.Name, out var dueVal) && dueVal is string dueStr &&
                    DateOnly.TryParse(dueStr, out var origDue))
                {
                    DateOnly? origBaseDate = null;
                    foreach (var baseDateKey in new[] { "request_date", "posting_date", "transaction_date" })
                    {
                        if (source.Data.TryGetValue(baseDateKey, out var bVal) && bVal is string bStr &&
                            DateOnly.TryParse(bStr, out var parsedBase))
                        {
                            origBaseDate = parsedBase;
                            break;
                        }
                    }

                    if (origBaseDate.HasValue)
                    {
                        var offsetDays = origDue.DayNumber - origBaseDate.Value.DayNumber;
                        cloned[field.Name] = scheduledDate.AddDays(offsetDays).ToString("yyyy-MM-dd");
                        continue;
                    }
                }
            }

            if (source.Data.TryGetValue(field.Name, out var val))
            {
                cloned[field.Name] = val;
            }
        }

        return cloned;
    }

    private static List<Dictionary<string, object?>> CloneTableRows(object rawTable, CompiledDocType? childMeta)
    {
        var result = new List<Dictionary<string, object?>>();

        IEnumerable<object?> rows = rawTable switch
        {
            JsonElement el when el.ValueKind == JsonValueKind.Array => el.EnumerateArray().Cast<object?>(),
            IEnumerable<object?> enumerable => enumerable,
            _ => []
        };

        foreach (var row in rows)
        {
            if (row is null) continue;
            var clonedRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (row is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (ChildSystemFieldsToStrip.Contains(prop.Name)) continue;

                    if (childMeta is not null && childMeta.ByName.TryGetValue(prop.Name, out var cf) && cf.Def.NoCopy)
                    {
                        continue;
                    }

                    clonedRow[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
            }
            else if (row is IDictionary<string, object?> dict)
            {
                foreach (var (k, v) in dict)
                {
                    if (ChildSystemFieldsToStrip.Contains(k)) continue;

                    if (childMeta is not null && childMeta.ByName.TryGetValue(k, out var cf) && cf.Def.NoCopy)
                    {
                        continue;
                    }

                    clonedRow[k] = v;
                }
            }

            if (clonedRow.Count > 0)
            {
                result.Add(clonedRow);
            }
        }

        return result;
    }
}
