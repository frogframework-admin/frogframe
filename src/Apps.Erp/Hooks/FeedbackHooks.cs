using System.Globalization;
using System.Text;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

public sealed class FeedbackTemplate { }
public sealed class MultiRaterReview { }
public sealed class FeedbackResponse { }
public sealed class AppraisalCycle { }

/// <summary>
/// 360-degree (multi-rater) feedback, docs/guides/360-feedback.md.
/// <list type="number">
/// <item>HR creates a <c>MultiRaterReview</c> per employee (or launches them for a whole appraisal cycle). Self and the
///   direct manager are added as raters automatically.</item>
/// <item>Nominating: the employee adds peers, direct reports and internal customers, then sends the list to the manager.</item>
/// <item>Awaiting approval: the manager (or HR) may change the list and approves it; one <c>FeedbackResponse</c> per rater
///   is created and the raters are notified.</item>
/// <item>In progress: each rater fills in and submits their own response (only they and HR-Talent can open it).</item>
/// <item>HR closes the review: averages per rater group are computed with the anonymity rule below and copied to the
///   appraisal's competencies (for development, not part of the final score).</item>
/// </list>
/// Anonymity: Self and Manager are shown by name. Peer, Direct report and Internal customer scores are shown per group
/// only when at least <c>anonymity_threshold</c> raters of that group answered the question; smaller groups are merged
/// into one "other raters" pool that is used only if it reaches the threshold too, and is otherwise left out. So no
/// published number (nor a difference of published numbers) rests on fewer raters than the threshold.
/// </summary>
internal static class MultiRater
{
    public const string Nominating = "Nominating", AwaitingApproval = "Awaiting approval", InProgress = "In progress",
        Closed = "Closed", Cancelled = "Cancelled";
    public const string Self = "Self", Manager = "Manager", Peer = "Peer", DirectReport = "Direct report", InternalCustomer = "Internal customer";
    public const string RowNominated = "Nominated", RowRequested = "Requested", RowSubmitted = "Submitted";
    public static readonly string[] AnonymousGroups = [Peer, DirectReport, InternalCustomer];

    /// <summary>Fields only the system writes (status, counts, results). Users' saves keep the stored values.</summary>
    public static readonly string[] ProtectedFields =
    [
        "status", "responses_requested", "responses_submitted", "overall_self", "overall_manager", "overall_others",
        "closed_at", "results", "result_notes", "comments_summary",
    ];

    public static bool IsSystem(UserContext user) => user.Id == "system";
    public static bool IsHr(UserContext user) => user.HasRole(ErpRoles.HrTalent);

    public static string Stamp(DateTimeOffset t) => t.ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes system fields straight to the store (no read-only check, no hooks), with an audit entry.
    /// Re-reads and retries when someone saved the document in between.
    /// </summary>
    public static async Task<Document> PatchAsync(IDocumentStore store, string tenant, string docType, string name,
        Func<Document, bool> change, string audit, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await store.GetAsync(tenant, docType, name, ct) ?? throw new DocumentNotFoundException(docType, name);
            var clone = current.Clone();
            if (!change(clone)) return current;
            clone.Version = current.Version + 1;
            clone.Modified = now;
            clone.ModifiedBy = "system";
            var uow = new UnitOfWork();
            uow.Upserts.Add((clone, current.Version));
            uow.Audits.Add(new AuditEntry
            {
                TenantId = tenant, DocType = docType, DocName = name, Action = audit, Actor = "system", At = now,
                Changes = [], SchemaVersion = current.SchemaVersion,
            });
            try { await store.CommitAsync(uow, ct); return clone; }
            catch (ConcurrencyException) when (attempt < 4) { /* saved meanwhile: apply the change again on the new version */ }
        }
    }

    public static List<Dictionary<string, object?>> CopyRows(IDictionary<string, object?> d, string key) =>
        d.Rows(key).Select(r => new Dictionary<string, object?>(r)).ToList();

    public static async Task NotifyAsync(IServiceProvider? services, string tenant, IEnumerable<string?> employees,
        string subject, string body, string docType, string? docName, string key, CancellationToken ct)
    {
        var users = services?.GetService<IUserDirectory>();
        var inbox = services?.GetService<INotificationStore>();
        if (users is null || inbox is null) return;
        await Notify.ToEmployeesAsync(users, inbox, tenant, employees, subject, body, docType, docName, key, ct);
    }

    /// <summary>Submitted responses of a review with the rater's relationship.</summary>
    public static async Task<List<Document>> SubmittedResponsesAsync(Masters m, string review, CancellationToken ct) =>
        (await m.QueryAsync("FeedbackResponse", [new FilterClause("review", "=", review)], ct, take: 200))
            .Where(r => r.DocStatus == DocStatus.Submitted).ToList();

    // ------------------------------------------------------------------ results

    public sealed record Answer(string Relationship, int QuestionNo, int Score, string? Comment);

    public sealed record Outcome(
        List<Dictionary<string, object?>> Rows, decimal? OverallSelf, decimal? OverallManager, decimal? OverallOthers,
        Dictionary<string, decimal> OthersByCompetency, string Notes, string Comments);

    private static decimal? Avg(IReadOnlyCollection<int> s) => s.Count == 0 ? null : Math.Round((decimal)s.Sum() / s.Count, 2);

    /// <summary>
    /// Results per question with the anonymity rule (see class summary). <paramref name="questions"/> = the template's
    /// question rows in order (question_no = position, 1-based); <paramref name="responses"/> = per submitted response its
    /// relationship, answers and overall comment.
    /// </summary>
    public static Outcome Compute(IReadOnlyList<IDictionary<string, object?>> questions,
        IReadOnlyList<(string Relationship, IReadOnlyList<Answer> Answers, string? Overall)> responses, int threshold)
    {
        threshold = Math.Max(2, threshold);
        var rows = new List<Dictionary<string, object?>>();
        var byCompetency = new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
        var selfAll = new List<int>();
        var managerAll = new List<int>();
        var othersAll = new List<decimal>();

        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var no = i + 1;
            if (q.Str("answer_type") == "Comment") continue;
            List<int> Scores(string rel) => responses.Where(r => r.Relationship == rel)
                .SelectMany(r => r.Answers.Where(a => a.QuestionNo == no && a.Score is >= 1 and <= 5).Select(a => a.Score)).ToList();

            var self = Scores(Self);
            var manager = Scores(Manager);
            var published = new Dictionary<string, List<int>>();
            var pool = new List<int>();
            foreach (var g in AnonymousGroups)
            {
                var s = Scores(g);
                if (s.Count >= threshold) published[g] = s; else pool.AddRange(s);
            }
            var combined = pool.Count >= threshold ? pool : [];
            var others = manager.Concat(published.Values.SelectMany(x => x)).Concat(combined).ToList();
            var othersAvg = Avg(others);
            var selfAvg = Avg(self);

            selfAll.AddRange(self);
            managerAll.AddRange(manager);
            if (othersAvg is { } oa)
            {
                othersAll.Add(oa);
                if (q.Str("competency") is { } comp)
                {
                    if (!byCompetency.TryGetValue(comp, out var list)) byCompetency[comp] = list = [];
                    list.Add(oa);
                }
            }

            rows.Add(new Dictionary<string, object?>
            {
                ["question_no"] = (long)no,
                ["competency"] = q.Str("competency"),
                ["question"] = Short(q.Str("question")),
                ["self_score"] = selfAvg,
                ["manager_score"] = Avg(manager),
                ["peer_score"] = published.TryGetValue(Peer, out var p) ? Avg(p) : null,
                ["direct_report_score"] = published.TryGetValue(DirectReport, out var dr) ? Avg(dr) : null,
                ["internal_customer_score"] = published.TryGetValue(InternalCustomer, out var ic) ? Avg(ic) : null,
                ["others_score"] = othersAvg,
                ["gap"] = othersAvg is { } o && selfAvg is { } sa ? o - sa : null,
                ["responses"] = (long)others.Count,
            });
        }

        // Which anonymous responses may show their comments: same rule, counted per response.
        var groupCounts = AnonymousGroups.ToDictionary(g => g, g => responses.Count(r => r.Relationship == g));
        var shownGroups = AnonymousGroups.Where(g => groupCounts[g] >= threshold).ToHashSet();
        var poolCount = AnonymousGroups.Where(g => !shownGroups.Contains(g)).Sum(g => groupCounts[g]);
        var poolShown = poolCount >= threshold;
        bool CommentsShown(string rel) => rel is Self or Manager || shownGroups.Contains(rel) || (poolShown && AnonymousGroups.Contains(rel));

        var notes = new StringBuilder();
        notes.Append(CultureInfo.InvariantCulture, $"Anonymity threshold: {threshold} raters. ");
        foreach (var g in AnonymousGroups.Where(g => groupCounts[g] > 0))
            notes.Append(CultureInfo.InvariantCulture, $"{g}: {groupCounts[g]} answered, ")
                 .Append(shownGroups.Contains(g) ? "shown as a group. " : poolShown ? "merged into 'All others'. " : "left out (too few to stay anonymous). ");
        if (responses.All(r => r.Relationship != Manager)) notes.Append("The manager has not answered. ");
        if (responses.All(r => r.Relationship != Self)) notes.Append("No self-assessment. ");

        var comments = new StringBuilder();
        void Block(string title, IEnumerable<(string Rel, string? Text)> items)
        {
            var named = items.Where(x => x.Rel is Self or Manager && !string.IsNullOrWhiteSpace(x.Text))
                .OrderBy(x => x.Rel == Self ? 0 : 1).Select(x => $"  ({x.Rel}) {x.Text!.Trim()}").ToList();
            var anon = items.Where(x => x.Rel is not (Self or Manager) && CommentsShown(x.Rel) && !string.IsNullOrWhiteSpace(x.Text))
                .Select(x => $"  - {x.Text!.Trim()}").OrderBy(x => x, StringComparer.Ordinal).ToList(); // sorted, not in answer order
            if (named.Count + anon.Count == 0) return;
            comments.AppendLine(title);
            foreach (var line in named.Concat(anon)) comments.AppendLine(line);
        }
        for (var i = 0; i < questions.Count; i++)
        {
            var no = i + 1;
            Block($"{no}. {Short(questions[i].Str("question"))}",
                responses.SelectMany(r => r.Answers.Where(a => a.QuestionNo == no).Select(a => (r.Relationship, a.Comment))));
        }
        Block("Overall", responses.Select(r => (r.Relationship, r.Overall)));

        return new Outcome(rows, Avg(selfAll), Avg(managerAll),
            othersAll.Count == 0 ? null : Math.Round(othersAll.Average(), 2),
            byCompetency.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value.Average(), 2)),
            notes.ToString().Trim(), comments.ToString().TrimEnd());
    }

    private static string? Short(string? s) => s is null ? null : s.Length <= 140 ? s : s[..137] + "...";

    public static IReadOnlyList<Answer> AnswersOf(Document response) =>
        response.Data.Rows("answers").Select(a => new Answer(response.Data.Str("relationship") ?? "", (int)a.Dec("question_no"),
            (int)a.Dec("score"), a.Str("comment"))).ToList();
}

/// <summary>Feedback Template: at least one rating question; sensible rater limits.</summary>
public sealed class FeedbackTemplateHook : IBeforeValidate<FeedbackTemplate>
{
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var errors = new Errors();
        if (!d.Rows("questions").Any(q => q.Str("answer_type") != "Comment"))
            errors.Add("questions", "Add at least one rating question");
        if (d.Dec("anonymity_threshold") < 2) errors.Add("anonymity_threshold", "The anonymity threshold must be at least 2");
        if (d.Dec("max_raters") > 0 && d.Dec("max_raters") < 2 + d.Dec("min_peers") + d.Dec("min_direct_reports"))
            errors.Add("max_raters", "Maximum raters must leave room for self, manager and the minimum peers and direct reports");
        errors.ThrowIfAny();
        return Task.CompletedTask;
    }
}

/// <summary>
/// 360 Feedback: status and results are written by the system only; who may change the rater list depends on the
/// status; Self and Manager rows are added automatically; one review per employee and cycle.
/// </summary>
public sealed class MultiRaterReviewHook(IDocumentStore store) : IBeforeValidate<MultiRaterReview>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var prev = c.Previous?.Data;
        var system = MultiRater.IsSystem(c.User);
        var hr = MultiRater.IsHr(c.User);

        if (!system)
        {
            foreach (var f in MultiRater.ProtectedFields)
                d[f] = prev is null ? null : prev.GetValueOrDefault(f) is List<Dictionary<string, object?>> stored
                    ? stored.Select(r => new Dictionary<string, object?>(r)).ToList() : prev.GetValueOrDefault(f);
            if (prev is null) d["status"] = MultiRater.Nominating;
        }
        var status = d.Str("status") ?? MultiRater.Nominating;
        d["status"] = status;

        if (prev is not null && !system && status is MultiRater.Closed or MultiRater.Cancelled)
            throw Errors.One("status", $"This 360 feedback is {status.ToLowerInvariant()} and can no longer be changed");

        var employee = d.Str("employee");
        if (prev is not null && !system)
        {
            // Employee and manager only work on the rater list; HR sets up the review.
            foreach (var f in new[] { "employee", "appraisal_cycle", "template", "due_date" })
            {
                if (prev.Str(f) == d.Str(f)) continue;
                if (!hr) throw Errors.One(f, "Only HR can change this");
                if (f != "due_date" && status != MultiRater.Nominating)
                    throw Errors.One(f, "Employee, cycle and template can only be changed while the raters are being nominated");
            }
        }

        var dup = (await m.QueryAsync("MultiRaterReview", [new FilterClause("employee", "=", employee), new FilterClause("appraisal_cycle", "=", d.Str("appraisal_cycle"))], ct, take: 10))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.Data.Str("status") != MultiRater.Cancelled);
        if (dup is not null) throw Errors.One("appraisal_cycle", $"{employee} already has 360 feedback {dup.Name} in this cycle");

        var template = await m.RequireAsync("FeedbackTemplate", d.Str("template"), "template", "Feedback Template", ct);
        if ((prev is null || prev.Str("template") != d.Str("template")) && template.Data.TryGetValue("is_active", out var act) && act is false)
            throw Errors.One("template", $"Feedback template {template.Name} is not active");

        // Manager = fetchFrom employee.reports_to (applied after this hook): read it here for the rater rules.
        var manager = string.IsNullOrEmpty(employee) ? null : await m.StrAsync("Employee", employee, "reports_to", ct);
        var me = c.User.Attr("employee");
        var rows = d.Rows("reviewers").Select(r => new Dictionary<string, object?>(r)).ToList();
        var prevRows = prev is null ? [] : prev.Rows("reviewers");

        static string Sig(IEnumerable<IDictionary<string, object?>> rs) =>
            string.Join("|", rs.Select(r => $"{r.Str("rater")}:{r.Str("relationship")}").Order(StringComparer.Ordinal));
        var mayEditList = system || hr
            || (status == MultiRater.Nominating && me is not null && me == employee)
            || (status == MultiRater.AwaitingApproval && me is not null && me == manager);
        if (prev is not null && !mayEditList && Sig(rows) != Sig(prevRows))
            throw Errors.One("reviewers", status switch
            {
                MultiRater.Nominating => "Only the employee or HR can nominate raters now",
                MultiRater.AwaitingApproval => "Only the manager or HR can change the raters now",
                _ => "Only HR can change the raters now",
            });

        if (!system)
        {
            foreach (var row in rows)
            {
                var before = prevRows.FirstOrDefault(p => p.Str("rater") == row.Str("rater") && p.Str("relationship") == row.Str("relationship"));
                row["status"] = before?.Str("status") ?? MultiRater.RowNominated;
                row["response"] = before?.Str("response");
            }
            foreach (var removed in prevRows.Where(p => p.Str("status") is MultiRater.RowRequested or MultiRater.RowSubmitted
                         && !rows.Any(r => r.Str("rater") == p.Str("rater") && r.Str("relationship") == p.Str("relationship"))))
                throw Errors.One("reviewers", $"{removed.Str("rater_name") ?? removed.Str("rater")} has already been asked for feedback and cannot be removed");
        }

        if (!string.IsNullOrEmpty(employee) && !rows.Any(r => r.Str("relationship") == MultiRater.Self))
            rows.Insert(0, new() { ["rater"] = employee, ["relationship"] = MultiRater.Self, ["status"] = MultiRater.RowNominated });
        if (!string.IsNullOrEmpty(manager) && !rows.Any(r => r.Str("relationship") == MultiRater.Manager))
            rows.Insert(Math.Min(1, rows.Count), new() { ["rater"] = manager, ["relationship"] = MultiRater.Manager, ["status"] = MultiRater.RowNominated });

        var errors = new Errors();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var path = $"reviewers[{i}].rater";
            var rater = r.Str("rater");
            var rel = r.Str("relationship");
            if (rater is null) continue;
            if (!seen.Add(rater)) { errors.Add(path, $"{rater} is listed more than once"); continue; }
            if (rel == MultiRater.Self && rater != employee) errors.Add(path, "The Self row must be the employee");
            if (rel != MultiRater.Self && rater == employee) errors.Add(path, "The employee can only rate as Self");
            if (rel == MultiRater.Manager && rater != manager)
                errors.Add(path, manager is null ? "The employee has no manager (Reports to)" : $"The Manager row must be the direct manager {manager}");
            var emp = await m.GetAsync("Employee", rater, ct);
            if (emp is null) { errors.Add(path, $"Employee {rater} not found"); continue; }
            r["rater_name"] = emp.Data.Str("full_name");
        }
        var max = (int)template.Data.Dec("max_raters");
        if (max > 0 && rows.Count > max) errors.Add("reviewers", $"At most {max} raters (self and manager included)");
        errors.ThrowIfAny();
        d["reviewers"] = rows;
    }
}

/// <summary>Employee (or HR) sends the nominated raters to the manager.</summary>
public sealed class MultiRaterSubmitNominationsAction(IDocumentStore store, TimeProvider clock) : IFormAction<MultiRaterReview>
{
    public string ActionId => "erp.360.submit_nominations";
    public string Label => "Send raters for approval";
    public string Icon => "send";

    public bool IsVisible(UserContext user, Document doc) =>
        doc.Data.Str("status") == MultiRater.Nominating
        && (MultiRater.IsHr(user) || (user.Attr("employee") is { } me && me == doc.Data.Str("employee")));

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Not available.");
        var m = new Masters(store, ctx.Doc.TenantId);
        var d = ctx.Doc.Data;
        var template = await m.GetAsync("FeedbackTemplate", d.Str("template") ?? "", ct);
        if (template is null) return FormActionResult.Fail("The feedback template no longer exists.");
        var rows = d.Rows("reviewers");
        var peers = rows.Count(r => r.Str("relationship") == MultiRater.Peer);
        var minPeers = (int)template.Data.Dec("min_peers");
        if (peers < minPeers) return FormActionResult.Fail($"Nominate at least {minPeers} peers (now {peers}).");
        var minReports = (int)template.Data.Dec("min_direct_reports");
        if (minReports > 0)
        {
            var available = (await m.QueryAsync("Employee", [new FilterClause("reports_to", "=", d.Str("employee"))], ct, take: 500))
                .Count(e => e.Data.Str("status") is null or "Active");
            var reports = rows.Count(r => r.Str("relationship") == MultiRater.DirectReport);
            if (available >= minReports && reports < minReports)
                return FormActionResult.Fail($"Nominate at least {minReports} direct reports (now {reports}).");
        }

        await MultiRater.PatchAsync(store, ctx.Doc.TenantId, "MultiRaterReview", ctx.Doc.Name, doc =>
        {
            if (doc.Data.Str("status") != MultiRater.Nominating) return false;
            doc.Data["status"] = MultiRater.AwaitingApproval;
            return true;
        }, "Raters sent for approval", clock.GetUtcNow(), ct);

        var manager = d.Str("manager");
        await MultiRater.NotifyAsync(ctx.Services, ctx.Doc.TenantId, [manager],
            $"Approve the 360 feedback raters of {d.Str("employee_name")}",
            $"{d.Str("employee_name")} nominated {rows.Count} raters. Check the list and press 'Send feedback requests'.",
            "MultiRaterReview", ctx.Doc.Name, $"360:approve:{ctx.Doc.Name}", ct);
        return FormActionResult.Ok(manager is null
            ? "Sent. The employee has no manager, so HR approves the raters."
            : "Sent to the manager for approval.");
    }
}

/// <summary>
/// Manager (or HR) approves the raters: one Feedback Response per new rater, the raters are notified and the review is
/// In progress. HR can use it again later for raters added afterwards.
/// </summary>
public sealed class MultiRaterSendRequestsAction(IDocumentStore store, TimeProvider clock) : IFormAction<MultiRaterReview>
{
    public string ActionId => "erp.360.send_requests";
    public string Label => "Send feedback requests";
    public string Icon => "mail";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc)
    {
        var status = doc.Data.Str("status");
        var hr = MultiRater.IsHr(user);
        if (status == MultiRater.AwaitingApproval) return hr || (user.Attr("employee") is { } me && me == doc.Data.Str("manager"));
        if (status == MultiRater.Nominating) return hr; // HR may skip the nomination step
        return status == MultiRater.InProgress && hr && doc.Data.Rows("reviewers").Any(r => r.Str("status") == MultiRater.RowNominated);
    }

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Not available.");
        var tenant = ctx.Doc.TenantId;
        var d = ctx.Doc.Data;
        var today = Hr.Today(clock);
        if (d.Date("due_date") is not { } due || due < today) return FormActionResult.Fail("Set a feedback due date that is today or later first.");

        var m = new Masters(store, tenant);
        var existing = await m.QueryAsync("FeedbackResponse", [new FilterClause("review", "=", ctx.Doc.Name)], ct, take: 200);
        var sys = new SystemDocs(ctx.Docs, tenant);
        var created = new Dictionary<string, string>(StringComparer.Ordinal); // rater → response
        foreach (var row in d.Rows("reviewers").Where(r => r.Str("status") == MultiRater.RowNominated && r.Str("rater") is not null))
        {
            var rater = row.Str("rater")!;
            var response = existing.FirstOrDefault(x => x.Data.Str("rater") == rater && x.DocStatus != DocStatus.Cancelled)
                ?? await sys.InsertAsync("FeedbackResponse", new Dictionary<string, object?>
                {
                    ["review"] = ctx.Doc.Name, ["rater"] = rater, ["relationship"] = row.Str("relationship"),
                }, "Requested", ct);
            created[rater] = response.Name;
        }
        if (created.Count == 0) return FormActionResult.Fail("There are no new raters to send requests to.");

        var updated = await MultiRater.PatchAsync(store, tenant, "MultiRaterReview", ctx.Doc.Name, doc =>
        {
            var rows = MultiRater.CopyRows(doc.Data, "reviewers");
            foreach (var r in rows)
                if (r.Str("rater") is { } rater && created.TryGetValue(rater, out var resp) && r.Str("status") == MultiRater.RowNominated)
                {
                    r["status"] = MultiRater.RowRequested;
                    r["response"] = resp;
                }
            doc.Data["reviewers"] = rows;
            doc.Data["status"] = MultiRater.InProgress;
            doc.Data["responses_requested"] = (long)rows.Count(r => r.Str("status") is MultiRater.RowRequested or MultiRater.RowSubmitted);
            return true;
        }, "Feedback requested", clock.GetUtcNow(), ct);

        await MultiRater.NotifyAsync(ctx.Services, tenant, created.Keys,
            $"Your feedback is requested for {d.Str("employee_name")}",
            $"Please answer the 360 feedback for {d.Str("employee_name")} by {D.Iso(due)}. Your answers are combined with others' and not shown with your name (except self and manager).",
            "FeedbackResponse", null, $"360:request:{ctx.Doc.Name}:{updated.Version}", ct);
        return FormActionResult.Ok($"Feedback requested from {created.Count} rater(s).");
    }
}

/// <summary>HR closes the review: results with the anonymity rule, copied to the appraisal; open responses are removed.</summary>
public sealed class MultiRaterCloseAction(IDocumentStore store, TimeProvider clock) : IFormAction<MultiRaterReview>
{
    public string ActionId => "erp.360.close";
    public string Label => "Close and show results";
    public string Icon => "lock";

    public bool IsVisible(UserContext user, Document doc) => doc.Data.Str("status") == MultiRater.InProgress && MultiRater.IsHr(user);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Not available.");
        var (doc, message) = await CloseAsync(store, ctx.Docs, ctx.Services, ctx.Doc.TenantId, ctx.Doc.Name, clock.GetUtcNow(), ct);
        return doc is null ? FormActionResult.Fail(message) : FormActionResult.Ok(message);
    }

    internal static async Task<(Document? Doc, string Message)> CloseAsync(IDocumentStore store, DocumentService docs, IServiceProvider? services,
        string tenant, string name, DateTimeOffset now, CancellationToken ct)
    {
        var m = new Masters(store, tenant);
        var review = await store.GetAsync(tenant, "MultiRaterReview", name, ct);
        if (review is null || review.Data.Str("status") != MultiRater.InProgress) return (null, "Only a 360 feedback in progress can be closed.");
        var template = await m.GetAsync("FeedbackTemplate", review.Data.Str("template") ?? "", ct);
        if (template is null) return (null, "The feedback template no longer exists.");

        var all = await m.QueryAsync("FeedbackResponse", [new FilterClause("review", "=", name)], ct, take: 200);
        var submitted = all.Where(r => r.DocStatus == DocStatus.Submitted).ToList();
        var outcome = MultiRater.Compute(template.Data.Rows("questions").Cast<IDictionary<string, object?>>().ToList(),
            submitted.Select(r => (r.Data.Str("relationship") ?? "", MultiRater.AnswersOf(r), r.Data.Str("overall_comment"))).ToList(),
            (int)template.Data.Dec("anonymity_threshold"));
        var submittedRaters = submitted.Select(r => r.Data.Str("rater")).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var closed = await MultiRater.PatchAsync(store, tenant, "MultiRaterReview", name, doc =>
        {
            if (doc.Data.Str("status") != MultiRater.InProgress) return false;
            var rows = MultiRater.CopyRows(doc.Data, "reviewers");
            foreach (var r in rows)
                if (r.Str("rater") is { } rater && submittedRaters.Contains(rater)) r["status"] = MultiRater.RowSubmitted;
            doc.Data["reviewers"] = rows;
            doc.Data["status"] = MultiRater.Closed;
            doc.Data["closed_at"] = MultiRater.Stamp(now);
            doc.Data["responses_submitted"] = (long)submitted.Count;
            doc.Data["results"] = outcome.Rows;
            doc.Data["overall_self"] = outcome.OverallSelf;
            doc.Data["overall_manager"] = outcome.OverallManager;
            doc.Data["overall_others"] = outcome.OverallOthers;
            doc.Data["result_notes"] = outcome.Notes;
            doc.Data["comments_summary"] = outcome.Comments;
            return true;
        }, "Closed", now, ct);
        if (closed.Data.Str("status") != MultiRater.Closed) return (null, "The 360 feedback was changed meanwhile; reload and try again.");

        // Unanswered requests are withdrawn so they leave the raters' lists.
        var sys = UserContext.System(tenant);
        foreach (var open in all.Where(r => r.DocStatus == DocStatus.Draft))
        {
            try { await docs.DeleteAsync(sys, "FeedbackResponse", open.Name, open.Version, ct); }
            catch (Exception ex) when (ex is ConcurrencyException or DocumentNotFoundException) { }
        }

        var appraisalNote = await CopyToAppraisalAsync(m, docs, tenant, review.Data.Str("employee"), review.Data.Str("appraisal_cycle"), outcome.OthersByCompetency, ct);
        await MultiRater.NotifyAsync(services, tenant, [review.Data.Str("employee"), review.Data.Str("manager")],
            "360 feedback results are available",
            $"The 360 feedback of {review.Data.Str("employee_name")} is closed: {submitted.Count} response(s).",
            "MultiRaterReview", name, $"360:closed:{name}", ct);
        return (closed, $"Closed with {submitted.Count} response(s). {appraisalNote}".Trim());
    }

    /// <summary>Per competency the average of the other raters → Appraisal Competency "360 (others)" of the same cycle.</summary>
    private static async Task<string> CopyToAppraisalAsync(Masters m, DocumentService docs, string tenant, string? employee, string? cycle,
        IReadOnlyDictionary<string, decimal> byCompetency, CancellationToken ct)
    {
        if (employee is null || cycle is null || byCompetency.Count == 0) return "";
        var appraisal = (await m.QueryAsync("Appraisal", [new FilterClause("employee", "=", employee), new FilterClause("appraisal_cycle", "=", cycle)], ct, take: 10))
            .FirstOrDefault(a => a.DocStatus != DocStatus.Cancelled);
        if (appraisal is null) return "";
        if (appraisal.DocStatus != DocStatus.Draft) return $"Appraisal {appraisal.Name} is already final; the 360 scores were not copied to it.";
        var rows = MultiRater.CopyRows(appraisal.Data, "competencies");
        var hits = 0;
        foreach (var r in rows)
            if (r.Str("competency") is { } comp && byCompetency.TryGetValue(comp, out var v)) { r["feedback_360_score"] = v; hits++; }
        if (hits == 0) return "";
        var updated = await new SystemDocs(docs, tenant).UpdateAsync("Appraisal", appraisal.Name, new Dictionary<string, object?> { ["competencies"] = rows }, ct);
        return updated is null ? "" : $"360 scores copied to appraisal {appraisal.Name} ({hits} competencies).";
    }
}

/// <summary>HR cancels a review that is not closed; open requests are withdrawn.</summary>
public sealed class MultiRaterCancelAction(IDocumentStore store, TimeProvider clock) : IFormAction<MultiRaterReview>
{
    public string ActionId => "erp.360.cancel";
    public string Label => "Cancel 360 feedback";
    public string Icon => "x";
    public string Tone => "negative";

    public bool IsVisible(UserContext user, Document doc) =>
        MultiRater.IsHr(user) && doc.Data.Str("status") is MultiRater.Nominating or MultiRater.AwaitingApproval or MultiRater.InProgress;

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Not available.");
        var tenant = ctx.Doc.TenantId;
        await MultiRater.PatchAsync(store, tenant, "MultiRaterReview", ctx.Doc.Name, doc =>
        {
            if (doc.Data.Str("status") is MultiRater.Closed or MultiRater.Cancelled) return false;
            doc.Data["status"] = MultiRater.Cancelled;
            return true;
        }, "Cancelled", clock.GetUtcNow(), ct);
        var sys = UserContext.System(tenant);
        foreach (var r in await new Masters(store, tenant).QueryAsync("FeedbackResponse", [new FilterClause("review", "=", ctx.Doc.Name)], ct, take: 200))
        {
            if (r.DocStatus != DocStatus.Draft) continue;
            try { await ctx.Docs.DeleteAsync(sys, "FeedbackResponse", r.Name, r.Version, ct); }
            catch (Exception ex) when (ex is ConcurrencyException or DocumentNotFoundException) { }
        }
        return FormActionResult.Ok("360 feedback cancelled.");
    }
}

/// <summary>
/// Feedback Response: created by the system with the template's questions; the rater changes only scores and comments,
/// and only while the review is in progress; submitting checks the required answers and updates the review's progress.
/// </summary>
public sealed class FeedbackResponseHook(IDocumentStore store, TimeProvider clock) : IBeforeValidate<FeedbackResponse>, IAfterSave<FeedbackResponse>
{
    private static readonly string[] Header = ["review", "employee", "employee_name", "rater", "relationship", "due_date", "instructions", "appraisal_cycle", "submitted_at"];

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var prev = c.Previous?.Data;

        if (prev is null)
        {
            if (!c.User.IsAdmin) throw new PermissionDeniedException("Feedback requests are created from a 360 feedback.");
            var review = await m.RequireAsync("MultiRaterReview", d.Str("review"), "review", "360 Feedback", ct);
            var template = await m.RequireAsync("FeedbackTemplate", review.Data.Str("template"), "review", "Feedback Template", ct);
            d["employee"] = review.Data.Str("employee");
            d["employee_name"] = review.Data.Str("employee_name");
            d["appraisal_cycle"] = review.Data.Str("appraisal_cycle");
            d["due_date"] = review.Data.Str("due_date");
            d["instructions"] = template.Data.Str("description");
            d["submitted_at"] = null;
            if (d.Rows("answers").Count == 0)
                d["answers"] = template.Data.Rows("questions").Select((q, i) => new Dictionary<string, object?>
                {
                    ["question_no"] = (long)(i + 1), ["competency"] = q.Str("competency"), ["question"] = q.Str("question"),
                    ["answer_type"] = q.Str("answer_type") ?? "Rating", ["is_required"] = q.Bool("is_required"),
                    ["score"] = null, ["comment"] = null,
                }).ToList();
            return;
        }

        if (!MultiRater.IsSystem(c.User))
        {
            foreach (var f in Header) d[f] = prev.GetValueOrDefault(f);
            // Only score and comment of the existing questions can change.
            var incoming = d.Rows("answers");
            d["answers"] = prev.Rows("answers").Select(p =>
            {
                var row = new Dictionary<string, object?>(p);
                var mine = incoming.FirstOrDefault(x => x.Dec("question_no") == p.Dec("question_no"));
                if (mine is not null)
                {
                    row["score"] = row.Str("answer_type") == "Comment" ? null : mine.GetValueOrDefault("score");
                    row["comment"] = mine.GetValueOrDefault("comment");
                }
                return row;
            }).ToList();

            var status = await m.StrAsync("MultiRaterReview", d.Str("review"), "status", ct);
            if (status != MultiRater.InProgress)
                throw Errors.One("answers", $"This 360 feedback is {status?.ToLowerInvariant() ?? "no longer open"}; answers can no longer be changed");
        }

        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous!.DocStatus == DocStatus.Draft)
        {
            var errors = new Errors();
            var answers = d.Rows("answers");
            for (var i = 0; i < answers.Count; i++)
            {
                var a = answers[i];
                if (!a.Bool("is_required")) continue;
                if (a.Str("answer_type") == "Comment" ? string.IsNullOrWhiteSpace(a.Str("comment")) : a.Dec("score") is < 1 or > 5)
                    errors.Add($"answers[{i}]." + (a.Str("answer_type") == "Comment" ? "comment" : "score"),
                        $"Question {a.Dec("question_no"):0} needs an answer");
            }
            errors.ThrowIfAny();
            d["submitted_at"] = MultiRater.Stamp(clock.GetUtcNow());
        }
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var rater = c.Doc.Data.Str("rater");
        if (c.Doc.Data.Str("review") is not { } review || rater is null) return;
        await MultiRater.PatchAsync(store, c.Doc.TenantId, "MultiRaterReview", review, doc =>
        {
            var rows = MultiRater.CopyRows(doc.Data, "reviewers");
            var row = rows.FirstOrDefault(r => r.Str("rater") == rater);
            if (row is null || row.Str("status") == MultiRater.RowSubmitted) return false;
            row["status"] = MultiRater.RowSubmitted;
            doc.Data["reviewers"] = rows;
            doc.Data["responses_submitted"] = (long)rows.Count(r => r.Str("status") == MultiRater.RowSubmitted);
            return true;
        }, "Feedback received", clock.GetUtcNow(), ct);
    }
}

/// <summary>Appraisal: "360 (others)" on competencies is filled by the system only.</summary>
internal static class AppraisalFeedback
{
    public static void Protect(HookContext c)
    {
        if (MultiRater.IsSystem(c.User)) return;
        var before = c.Previous?.Data.Rows("competencies") ?? [];
        foreach (var row in c.Doc.Data.Rows("competencies"))
            row["feedback_360_score"] = before.FirstOrDefault(b => b.Str("competency") == row.Str("competency"))?.GetValueOrDefault("feedback_360_score");
    }
}

/// <summary>
/// Appraisal Cycle → "Launch 360 feedback": one review (status Nominating) per employee with an appraisal in the cycle
/// who has none yet, with the cycle's template and due date; the employees are asked to nominate raters.
/// </summary>
public sealed class AppraisalCycleLaunch360Action(IDocumentStore store) : IFormAction<AppraisalCycle>
{
    public string ActionId => "erp.appraisal_cycle.launch_360";
    public string Label => "Launch 360 feedback";
    public string Icon => "users";

    public bool IsVisible(UserContext user, Document doc) =>
        MultiRater.IsHr(user) && doc.Data.Str("feedback_template") is not null && doc.Data.Str("status") != "Closed";

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Set the 360 feedback template first.");
        if (ctx.Doc.Data.Str("feedback_due_date") is not { } due) return FormActionResult.Fail("Set the 360 feedback due date first.");
        var tenant = ctx.Doc.TenantId;
        var m = new Masters(store, tenant);
        var appraisals = (await m.QueryAsync("Appraisal", [new FilterClause("appraisal_cycle", "=", ctx.Doc.Name)], ct, take: 20000))
            .Where(a => a.DocStatus != DocStatus.Cancelled && a.Data.Str("employee") is not null).ToList();
        var existing = (await m.QueryAsync("MultiRaterReview", [new FilterClause("appraisal_cycle", "=", ctx.Doc.Name)], ct, take: 20000))
            .Where(r => r.Data.Str("status") != MultiRater.Cancelled).Select(r => r.Data.Str("employee")).ToHashSet(StringComparer.Ordinal);

        var sys = new SystemDocs(ctx.Docs, tenant);
        var created = new List<string>();
        var failed = new List<string>();
        foreach (var employee in appraisals.Select(a => a.Data.Str("employee")!).Distinct(StringComparer.Ordinal).Where(e => !existing.Contains(e)))
        {
            try
            {
                await sys.InsertAsync("MultiRaterReview", new Dictionary<string, object?>
                {
                    ["employee"] = employee, ["appraisal_cycle"] = ctx.Doc.Name,
                    ["template"] = ctx.Doc.Data.Str("feedback_template"), ["due_date"] = due,
                }, "Launched", ct);
                created.Add(employee);
            }
            catch (ValidationFailedException ex) { failed.Add($"{employee}: {ex.Errors.FirstOrDefault()?.Message}"); }
        }
        await MultiRater.NotifyAsync(ctx.Services, tenant, created, "Nominate raters for your 360 feedback",
            $"Open your 360 feedback for {ctx.Doc.Name}, add the colleagues you want feedback from and press 'Send raters for approval'.",
            "MultiRaterReview", null, $"360:launch:{ctx.Doc.Name}", ct);
        var msg = $"{created.Count} 360 feedback(s) created.";
        if (failed.Count > 0) msg += $" Not created: {string.Join("; ", failed.Take(5))}{(failed.Count > 5 ? " …" : "")}";
        return created.Count == 0 && failed.Count == 0 ? FormActionResult.Ok("Every employee with an appraisal in this cycle already has a 360 feedback.") : FormActionResult.Ok(msg);
    }
}

/// <summary>
/// Daily: reminds raters with an open request 3 days before the due date and once a day after it, and reminds
/// employees who have not sent their nominations.
/// </summary>
public sealed class FeedbackReminderJob : IScheduledJob
{
    public string Name => "Erp.FeedbackReminders";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var store = context.Services.GetRequiredService<IDocumentStore>();
        var clock = context.Services.GetService<TimeProvider>() ?? TimeProvider.System;
        var today = Hr.Today(clock);
        var m = new Masters(store, context.TenantId);
        foreach (var review in await m.QueryAsync("MultiRaterReview", [new FilterClause("status", "in", new List<object?> { MultiRater.Nominating, MultiRater.InProgress })], ct, take: 20000))
        {
            if (review.Data.Date("due_date") is not { } due) continue;
            var days = due.DayNumber - today.DayNumber;
            if (days > 3) continue;
            var key = $"360:remind:{review.Name}:{D.Iso(today)}";
            if (review.Data.Str("status") == MultiRater.Nominating)
            {
                await MultiRater.NotifyAsync(context.Services, context.TenantId, [review.Data.Str("employee")],
                    "Nominate raters for your 360 feedback", $"Your 360 feedback is due {D.Iso(due)}; send your raters for approval first.",
                    "MultiRaterReview", review.Name, key, ct);
                continue;
            }
            var waiting = review.Data.Rows("reviewers").Where(r => r.Str("status") == MultiRater.RowRequested).Select(r => r.Str("rater")).ToList();
            if (waiting.Count == 0) continue;
            await MultiRater.NotifyAsync(context.Services, context.TenantId, waiting,
                days >= 0 ? $"Reminder: 360 feedback for {review.Data.Str("employee_name")} is due {D.Iso(due)}"
                          : $"Overdue: 360 feedback for {review.Data.Str("employee_name")}",
                "Open 'My feedback requests', answer the questions and press Submit.", "FeedbackResponse", null, key, ct);
        }
    }
}
