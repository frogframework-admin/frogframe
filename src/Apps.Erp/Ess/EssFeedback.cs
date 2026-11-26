using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Ess;

public sealed record EssFeedbackAnswer(int QuestionNo, int? Score, string? Comment);
public sealed record EssFeedbackSave(IReadOnlyList<EssFeedbackAnswer>? Answers, string? OverallComment, bool? Submit);

/// <summary>
/// ESS: 360 feedback requests the employee has to answer, and the results of their own closed 360 feedback
/// (docs/guides/ess-mobile-api.md section 13). Everything runs with the employee's own permissions: a rater only
/// reaches their own responses (scope rater = employee), the employee only the aggregated result of their review.
/// </summary>
public sealed class EssFeedbackService(DocumentService docs, IDocumentStore store)
{
    public async Task<IReadOnlyList<Document>> RequestsAsync(UserContext user, string employee, CancellationToken ct) =>
        (await new Masters(store, user.TenantId).QueryAsync("FeedbackResponse", [new FilterClause("rater", "=", employee)], ct, "due_date", "asc", 100))
            .Where(r => r.DocStatus != DocStatus.Cancelled).ToList();

    public async Task<Document> GetAsync(UserContext user, string employee, string name, CancellationToken ct)
    {
        var doc = await docs.GetAsync(user, "FeedbackResponse", name, ct);
        return doc.Data.Str("rater") == employee ? doc : throw new DocumentNotFoundException("FeedbackResponse", name);
    }

    /// <summary>Saves scores and comments (only those questions sent), then submits when asked.</summary>
    public async Task<Document> SaveAsync(UserContext user, string employee, string name, EssFeedbackSave body, CancellationToken ct)
    {
        var doc = await GetAsync(user, employee, name, ct);
        if (doc.DocStatus != DocStatus.Draft) throw new InvalidStateException("This feedback has already been submitted.");
        var rows = doc.Data.Rows("answers").Select(r => new Dictionary<string, object?>(r)).ToList();
        foreach (var a in body.Answers ?? [])
        {
            var row = rows.FirstOrDefault(r => (int)r.Dec("question_no") == a.QuestionNo)
                ?? throw new ValidationFailedException([new FieldError("answers", $"Question {a.QuestionNo} does not exist")]);
            if (a.Score is { } s && (s < 1 || s > 5))
                throw new ValidationFailedException([new FieldError("answers", $"Question {a.QuestionNo}: score must be 1 to 5")]);
            row["score"] = a.Score is null ? null : (long)a.Score.Value;
            row["comment"] = a.Comment;
        }
        var patch = new Dictionary<string, object?> { ["answers"] = rows };
        if (body.OverallComment is not null) patch["overall_comment"] = body.OverallComment;
        doc = await docs.UpdateAsync(user, "FeedbackResponse", name, doc.Version, patch, ct);
        if (body.Submit == true) doc = await docs.RunActionAsync(user, "FeedbackResponse", name, DocActions.Submit, doc.Version, null, ct);
        return doc;
    }

    /// <summary>The employee's own closed 360 feedback (aggregated results only).</summary>
    public async Task<IReadOnlyList<Document>> ResultsAsync(UserContext user, string employee, CancellationToken ct)
    {
        var mine = await new Masters(store, user.TenantId).QueryAsync("MultiRaterReview", [new FilterClause("employee", "=", employee)], ct, "modified", "desc", 20);
        var result = new List<Document>();
        foreach (var r in mine.Where(r => r.Data.Str("status") == MultiRater.Closed))
            result.Add(await docs.GetAsync(user, "MultiRaterReview", r.Name, ct)); // permission check as the employee
        return result;
    }

    public static object RequestDto(Document d, bool withAnswers) => new
    {
        name = d.Name, employee = d.Data.Str("employee_name"), relationship = d.Data.Str("relationship"),
        dueDate = d.Data.Str("due_date"), submitted = d.DocStatus == DocStatus.Submitted,
        submittedAt = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("submitted_at")),
        instructions = withAnswers ? d.Data.Str("instructions") : null,
        overallComment = withAnswers ? d.Data.Str("overall_comment") : null,
        answers = withAnswers
            ? d.Data.Rows("answers").Select(a => new
            {
                questionNo = (int)a.Dec("question_no"), question = a.Str("question"), competency = a.Str("competency"),
                answerType = a.Str("answer_type"), required = a.Bool("is_required"),
                score = a.GetValueOrDefault("score") is null ? (int?)null : (int)a.Dec("score"), comment = a.Str("comment"),
            })
            : null,
    };

    public static object ResultDto(Document d) => new
    {
        name = d.Name, cycle = d.Data.Str("appraisal_cycle"), closedAt = DatetimeFieldType.ToZone(d.Data.GetValueOrDefault("closed_at")),
        responses = (int)d.Data.Dec("responses_submitted"),
        overall = new { self = Num(d.Data, "overall_self"), manager = Num(d.Data, "overall_manager"), others = Num(d.Data, "overall_others") },
        questions = d.Data.Rows("results").Select(r => new
        {
            questionNo = (int)r.Dec("question_no"), question = r.Str("question"), competency = r.Str("competency"),
            self = Num(r, "self_score"), manager = Num(r, "manager_score"), peers = Num(r, "peer_score"),
            directReports = Num(r, "direct_report_score"), internalCustomers = Num(r, "internal_customer_score"),
            others = Num(r, "others_score"), gap = Num(r, "gap"),
        }),
        notes = d.Data.Str("result_notes"), comments = d.Data.Str("comments_summary"),
    };

    private static decimal? Num(IDictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is not null ? d.Dec(key) : null;
}
