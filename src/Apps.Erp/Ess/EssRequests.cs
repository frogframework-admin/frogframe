using System.Globalization;
using System.Security.Cryptography;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Files;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Apps.Erp.Ess;

public sealed record EssFaceRegistrationInput
{
    public required IReadOnlyList<(byte[] Content, string FileName)> Photos { get; init; }
    public required IReadOnlyList<double> Template { get; init; }
    public string? ModelName { get; init; }
    public string? ModelVersion { get; init; }
    public bool Consent { get; init; }
    public decimal? QualityScore { get; init; }
    public decimal? LivenessScore { get; init; }
    public string? DeviceId { get; init; }
    public string? AppVersion { get; init; }
}

/// <param name="Kind">"approve" or "reject".</param>
public sealed record EssApprovalAction(string Action, string Kind, string ToState, bool RequireComment);

/// <param name="Stage">peer, project, supervisor or hr.</param>
/// <param name="IsOverride">An HR officer's override of a request that waits for the supervisor.</param>
public sealed record EssApproval(CompiledDocType Meta, Document Doc, string Stage, IReadOnlyList<EssApprovalAction> Actions, bool IsOverride);

public sealed record EssOvertimeRequest(string? Date, decimal? Hours, string? DayType, string? Project, string? Reason, bool? Submit);

/// <param name="CheckIn">Local time of the application time zone ("2026-11-20T08:00") or with an offset.</param>
public sealed record EssCorrectionRequest(string? Date, string? CorrectionType, string? CheckIn, string? CheckOut, string? Reason, string? AttachmentFileId, bool? Submit);

/// <param name="Changes">Employee field → new value. Table fields (bank_accounts, dependents, emergency_contacts,
/// identity_documents) take an array of row objects.</param>
public sealed record EssBiodataRequest(string? Category, Dictionary<string, System.Text.Json.JsonElement>? Changes, string? Reason, string? AttachmentFileId, bool? Submit);

/// <summary>
/// Self-service requests of the ESS app: face registration, overtime, attendance correction and biodata change. Every
/// document is created and submitted as the employee, so permissions, workflow conditions and hooks apply as on the desk.
/// </summary>
public sealed class EssRequestService(
    DocumentService docs, IDocumentStore store, IFileStore files, FileService fileService, IDataProtectionProvider dataProtection,
    IMetadataProvider metadata, TimeProvider clock)
{
    public const int MaxFacePhotos = 3;

    /// <summary>DocTypes and Attach/Image fields the app may upload files for (<c>POST /files/{docType}/{field}</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> UploadFields = new Dictionary<string, string[]>
    {
        ["LeaveApplication"] = ["attachment"],
        ["AttendanceCorrection"] = ["attachment"],
        ["BiodataChangeRequest"] = ["attachment"],
    };

    // ------------------------------------------------------------------ face

    public async Task<Document> RegisterFaceAsync(UserContext user, string employee, EssFaceRegistrationInput input, CancellationToken ct)
    {
        if (!input.Consent)
            throw new ValidationFailedException([new FieldError("consent", "The employee's consent is required to register a face")]);
        if (input.Photos.Count is < 1 or > MaxFacePhotos)
            throw new ValidationFailedException([new FieldError("photos", $"Send 1–{MaxFacePhotos} face photos")]);
        var canonical = FaceTemplates.Canonical(input.Template);

        var payload = new Dictionary<string, object?>
        {
            ["employee"] = employee, ["consent"] = true,
            ["model_name"] = Trim(input.ModelName, 100), ["model_version"] = Trim(input.ModelVersion, 50),
            ["quality_score"] = input.QualityScore, ["liveness_score"] = input.LivenessScore,
            ["device_id"] = Trim(input.DeviceId, 200), ["app_version"] = Trim(input.AppVersion, 50),
        };
        for (var i = 0; i < input.Photos.Count; i++)
            payload[$"photo_{i + 1}"] = await fileService.UploadImageAsync(user, "FaceRegistration", $"photo_{i + 1}", new MemoryStream(input.Photos[i].Content), ct);

        var protectedBytes = FaceTemplates.Protect(dataProtection, canonical);
        var record = new FileRecord
        {
            Id = "f_" + Guid.NewGuid().ToString("N"), TenantId = user.TenantId, UploadedBy = user.Id, ContentType = FaceTemplates.ContentType,
            Size = protectedBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(protectedBytes)), CreatedAt = clock.GetUtcNow(),
            FileName = "face-template.bin",
        };
        await files.SaveAsync(record, protectedBytes, ct);
        payload["template_file"] = record.Id;

        var doc = await docs.InsertAsync(user, "FaceRegistration", payload, "Registered from the ESS app", ct);
        return await docs.RunActionAsync(user, "FaceRegistration", doc.Name, "Submit for verification", doc.Version, null, ct);
    }

    /// <summary>The employee's registrations, newest first, and the active one.</summary>
    public async Task<(IReadOnlyList<Document> All, Document? Active)> FaceStatusAsync(UserContext user, string employee, CancellationToken ct)
    {
        var list = await new Masters(store, user.TenantId).QueryAsync("FaceRegistration", [new("employee", "=", employee)], ct, StandardFields.Modified, "desc", 20);
        return (list, list.FirstOrDefault(d => d.WorkflowState == "Active"));
    }

    /// <summary>The verified template of the employee for matching on the phone, or null.</summary>
    public async Task<(Document Registration, double[] Template)?> ActiveTemplateAsync(UserContext user, string employee, CancellationToken ct)
    {
        var (_, active) = await FaceStatusAsync(user, employee, ct);
        if (active?.Data.Str("template_file") is not { } fileId || await files.GetAsync(user.TenantId, fileId, ct) is not { } stored) return null;
        return FaceTemplates.Unprotect(dataProtection, stored.Content) is { } vector ? (active, vector) : null;
    }

    // ------------------------------------------------------------------ overtime

    public async Task<Document> CreateOvertimeAsync(UserContext user, string employee, EssOvertimeRequest req, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["employee"] = employee, ["overtime_date"] = req.Date, ["hours"] = req.Hours, ["reason"] = req.Reason,
        };
        // Omitted values keep the DocType defaults (day type "Working day"; the hook switches rest days by the schedule).
        if (Trim(req.Project, 140) is { } project) payload["project"] = project;
        if (!string.IsNullOrWhiteSpace(req.DayType)) payload["day_type"] = req.DayType;
        var doc = await docs.InsertAsync(user, "OvertimeRequest", payload, "Created via ESS Mobile", ct);
        if (req.Submit == false) return doc;
        var action = doc.Data.Str("project_approver") is null ? "Submit for approval" : "Submit for project approval";
        return await docs.RunActionAsync(user, "OvertimeRequest", doc.Name, action, doc.Version, null, ct);
    }

    // ------------------------------------------------------------------ attendance correction

    public async Task<Document> CreateCorrectionAsync(UserContext user, string employee, EssCorrectionRequest req, CancellationToken ct)
    {
        var doc = await docs.InsertAsync(user, "AttendanceCorrection", new Dictionary<string, object?>
        {
            ["employee"] = employee, ["attendance_date"] = req.Date, ["correction_type"] = req.CorrectionType,
            ["requested_check_in"] = ToUtc(req.CheckIn, "checkIn"), ["requested_check_out"] = ToUtc(req.CheckOut, "checkOut"),
            ["reason"] = req.Reason, ["attachment"] = string.IsNullOrWhiteSpace(req.AttachmentFileId) ? null : req.AttachmentFileId,
        }, "Created via ESS Mobile", ct);
        if (req.Submit == false) return doc;
        var action = doc.Data.Str("approver") is null ? "Submit to HR" : "Submit for approval";
        return await docs.RunActionAsync(user, "AttendanceCorrection", doc.Name, action, doc.Version, null, ct);
    }

    /// <summary>"2026-11-20T08:00" (application time zone) or a timestamp with offset → stored UTC datetime.</summary>
    internal static string? ToUtc(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var styles = DateTimeStyles.AllowWhiteSpaces;
        if (value.EndsWith('Z') || value.Length > 19 && (value[^6] is '+' or '-'))
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles, out var withOffset)
                ? withOffset.ToUniversalTime().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture)
                : throw new ValidationFailedException([new FieldError(field, "Use yyyy-MM-ddTHH:mm")]);
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out var local))
            throw new ValidationFailedException([new FieldError(field, "Use yyyy-MM-ddTHH:mm")]);
        var zone = FieldTypeSettings.DefaultTimeZone;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local))
            .ToUniversalTime().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ biodata

    /// <summary>Fields the employee may ask to change, per category, with their current values (tables as row lists).</summary>
    public async Task<object> BiodataAsync(UserContext user, string employee, CancellationToken ct)
    {
        var emp = await new Masters(store, user.TenantId).GetAsync("Employee", employee, ct) ?? throw new DocumentNotFoundException("Employee", employee);
        var meta = metadata.Current.Get("Employee");
        return BiodataChangeRequestHook.Fields.Select(c => new
        {
            category = c.Key,
            attachmentRequired = c.Key != "Contact & address",
            fields = c.Value.Where(meta.ByName.ContainsKey).Select(f => new
            {
                field = f, label = meta.ByName[f].Label, type = meta.ByName[f].Def.Type, options = meta.ByName[f].Def.Options,
                columns = meta.ByName[f].Def.Type == "Table"
                    ? metadata.Current.Get(meta.ByName[f].Def.ChildDocType!).Fields.Where(x => !x.IsLayout)
                        .Select(x => new { field = x.Name, label = x.Label, type = x.Def.Type, options = x.Def.Options })
                    : null,
                value = emp.Data.GetValueOrDefault(f),
            }),
        }).ToList();
    }

    public async Task<Document> CreateBiodataChangeAsync(UserContext user, string employee, EssBiodataRequest req, CancellationToken ct)
    {
        var rows = (req.Changes ?? []).Select(kv => new Dictionary<string, object?>
        {
            ["field"] = kv.Key,
            ["new_value"] = kv.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Array or System.Text.Json.JsonValueKind.Object => kv.Value.GetRawText(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => kv.Value.ToString(),
            },
        }).ToList();
        var doc = await docs.InsertAsync(user, "BiodataChangeRequest", new Dictionary<string, object?>
        {
            ["employee"] = employee, ["category"] = req.Category, ["reason"] = req.Reason, ["changes"] = rows,
            ["attachment"] = string.IsNullOrWhiteSpace(req.AttachmentFileId) ? null : req.AttachmentFileId,
        }, "Created via ESS Mobile", ct);
        if (req.Submit == false) return doc;
        return await docs.RunActionAsync(user, "BiodataChangeRequest", doc.Name, "Submit for approval", doc.Version, null, ct);
    }

    // ------------------------------------------------------------------ approvals (supervisor and HR)

    /// <summary>Request DocTypes the ESS app can approve or reject.</summary>
    public static readonly IReadOnlyList<string> ApprovalDocTypes =
        ["LeaveApplication", "OvertimeRequest", "ShiftSwapRequest", "AttendanceCorrection", "FaceRegistration", "BiodataChangeRequest", "ExpenseClaim", "EmployeeAdvance"];

    /// <summary>
    /// Requests waiting for this user's decision, from the workflows themselves (role, condition, row scope), oldest first.
    /// HR overrides are left out unless asked for: an HR officer may approve any pending leave or overtime with
    /// "Approve (HR)", but those wait for the supervisor and would flood the HR inbox.
    /// </summary>
    public async Task<IReadOnlyList<EssApproval>> ApprovalInboxAsync(UserContext user, bool includeOverrides, CancellationToken ct)
    {
        var queue = await docs.DecisionQueueAsync(user, ct: ct);
        var result = new List<EssApproval>();
        foreach (var (meta, doc, actionNames) in queue)
        {
            if (!ApprovalDocTypes.Contains(meta.Name) || meta.Workflow is not { } wf) continue;
            var actions = Describe(meta, doc, actionNames);
            if (actions.Count == 0) continue;
            var fromState = wf.Transitions.Where(t => t.Def.From == doc.WorkflowState).ToList();
            var isOverride = actions.All(a => a.Action.EndsWith("(HR)", StringComparison.Ordinal))
                             && fromState.Any(t => !t.Def.Action.EndsWith("(HR)", StringComparison.Ordinal) && Kind(wf, t) == "approve");
            if (isOverride && !includeOverrides) continue;
            result.Add(new EssApproval(meta, doc, Stage(doc.WorkflowState, actions, isOverride), actions, isOverride));
        }
        return result.OrderBy(a => a.Doc.Modified).ToList();
    }

    /// <summary>
    /// Runs the user's approve or reject action on a request. <paramref name="action"/> picks one action when the
    /// state offers several (e.g. "Approve (HR)"); otherwise the only approve/reject action available is used.
    /// </summary>
    public async Task<Document> DecideAsync(UserContext user, string docType, string name, string decision, string? action, string? comment, CancellationToken ct)
    {
        if (!ApprovalDocTypes.Contains(docType)) throw new DocumentNotFoundException(docType, name);
        var kind = decision.ToLowerInvariant() switch
        {
            "approve" => "approve",
            "reject" => "reject",
            _ => throw new ValidationFailedException([new FieldError("decision", "decision must be 'approve' or 'reject'")]),
        };
        var doc = await docs.GetAsync(user, docType, name, ct);
        var meta = metadata.Current.Get(docType);
        var available = (await docs.AvailableActionsAsync(user, doc, ct)).Select(a => a.Action).ToList();
        var candidates = Describe(meta, doc, available).Where(a => a.Kind == kind).ToList();
        var chosen = action is { Length: > 0 }
            ? candidates.FirstOrDefault(a => a.Action == action)
            : candidates.OrderBy(a => a.Action.EndsWith("(HR)", StringComparison.Ordinal) ? 1 : 0).FirstOrDefault();
        if (chosen is null)
            throw new PermissionDeniedException(candidates.Count == 0
                ? $"You cannot {kind} {name} in its current state ({doc.WorkflowState})."
                : $"Choose one of: {string.Join(", ", candidates.Select(c => c.Action))}.");
        if (chosen.RequireComment && string.IsNullOrWhiteSpace(comment))
            throw new ValidationFailedException([new FieldError("comment", $"A comment is required for '{chosen.Action}'")]);
        return await docs.RunActionAsync(user, docType, name, chosen.Action, doc.Version, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), ct);
    }

    private static IReadOnlyList<EssApprovalAction> Describe(CompiledDocType meta, Document doc, IEnumerable<string> actionNames)
    {
        var wf = meta.Workflow!;
        var list = new List<EssApprovalAction>();
        foreach (var name in actionNames.Distinct(StringComparer.Ordinal))
        {
            if (name is "Withdraw" or "Cancel" or "Revoke") continue; // the requester's or after-the-fact actions
            var t = wf.Transitions.FirstOrDefault(x => x.Def.Action == name && x.Def.From == doc.WorkflowState);
            if (t is null) continue;
            list.Add(new EssApprovalAction(name, Kind(wf, t), t.Def.To, t.Def.RequireComment));
        }
        return list;
    }

    private static string Kind(CompiledWorkflow wf, CompiledTransition t) =>
        wf.States.TryGetValue(t.Def.To, out var to) && to.DocStatus == DocStatus.Cancelled ? "reject" : "approve";

    private static string Stage(string? state, IReadOnlyList<EssApprovalAction> actions, bool isOverride) =>
        state is { } s && s.Contains("Peer", StringComparison.Ordinal) ? "peer"
        : state is { } p && p.Contains("Project", StringComparison.Ordinal) ? "project"
        : isOverride || actions.Any(a => a.Action.EndsWith("(HR)", StringComparison.Ordinal) || a.Action == "Verify") ? "hr"
        : "supervisor";

    // ------------------------------------------------------------------ shared

    /// <summary>The employee's own documents of a request DocType, newest first.</summary>
    public async Task<IReadOnlyList<Document>> MineAsync(UserContext user, string docType, string employeeField, string employee, int take, CancellationToken ct) =>
        await new Masters(store, user.TenantId).QueryAsync(docType, [new(employeeField, "=", employee)], ct, StandardFields.Modified, "desc", take);

    /// <summary>Withdraws the employee's own pending request (workflow action "Withdraw").</summary>
    public async Task<Document> WithdrawAsync(UserContext user, string docType, string employeeField, string employee, string name, CancellationToken ct)
    {
        var doc = await docs.GetAsync(user, docType, name, ct);
        if (doc.Data.Str(employeeField) != employee) throw new DocumentNotFoundException(docType, name);
        return await docs.RunActionAsync(user, docType, name, "Withdraw", doc.Version, null, ct);
    }

    public async Task<string> UploadAsync(UserContext user, string docType, string field, string fileName, Stream content, CancellationToken ct)
    {
        if (!UploadFields.TryGetValue(docType, out var fields) || !fields.Contains(field)
            || !metadata.Current.TryGet(docType, out var meta) || !meta.ByName.ContainsKey(field))
            throw new DocumentNotFoundException(docType, field);
        return await fileService.UploadFieldFileAsync(user, docType, field, fileName, content, ct);
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();
}
