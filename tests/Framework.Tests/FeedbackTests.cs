using Apps.Erp;
using Apps.Erp.Ess;
using Apps.Erp.Hooks;
using Apps.Erp.Reports;
using Framework.Actions;
using Framework.Documents;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>360-degree feedback: anonymity rule, nomination → approval → answers → results, privacy of single responses.</summary>
sealed partial class Tests
{
    private static MultiRater.Answer A(string rel, int score, string? comment = null) => new(rel, 1, score, comment);

    [Test]
    public Task Feedback_360_results_hide_groups_below_the_anonymity_threshold()
    {
        IReadOnlyList<IDictionary<string, object?>> questions =
        [
            new Dictionary<string, object?> { ["competency"] = "COMM", ["question"] = "Explains clearly", ["answer_type"] = "Rating" },
        ];
        (string, IReadOnlyList<MultiRater.Answer>, string?) R(string rel, int score, string? comment = null) =>
            (rel, [A(rel, score, comment)], null);

        // 3 peers (shown), 1 direct report + 1 internal customer (pool of 2 < 3: left out).
        var outcome = MultiRater.Compute(questions,
        [
            R(MultiRater.Self, 4, "I think I do well"), R(MultiRater.Manager, 3, "Needs structure"),
            R(MultiRater.Peer, 5, "peer one"), R(MultiRater.Peer, 4), R(MultiRater.Peer, 3),
            R(MultiRater.DirectReport, 1, "only report"), R(MultiRater.InternalCustomer, 2),
        ], threshold: 3);
        var row = outcome.Rows.Single();
        Assert.Equal(4m, row["peer_score"], "peers shown as a group of 3");
        Assert.True(row["direct_report_score"] is null && row["internal_customer_score"] is null, "groups of 1 are not shown");
        Assert.Equal(3.75m, row["others_score"], "others = manager + peers; the pool of 2 is left out");
        Assert.Equal(-0.25m, row["gap"], "others − self");
        Assert.Equal(4L, row["responses"], "four ratings used");
        Assert.True(!outcome.Comments.Contains("only report"), "comments of a group that is too small are not shown");
        Assert.True(outcome.Comments.Contains("(Manager) Needs structure") && outcome.Comments.Contains("peer one"), "manager named, peer comment anonymous");
        Assert.Equal(3.75m, outcome.OthersByCompetency["COMM"], "per competency for the appraisal");

        // One more internal customer: the pool reaches 3 and counts in "All others", but still not per group.
        var pooled = MultiRater.Compute(questions,
        [
            R(MultiRater.Manager, 3), R(MultiRater.Peer, 5), R(MultiRater.Peer, 4), R(MultiRater.Peer, 3),
            R(MultiRater.DirectReport, 1), R(MultiRater.InternalCustomer, 2), R(MultiRater.InternalCustomer, 2),
        ], threshold: 3);
        var p = pooled.Rows.Single();
        Assert.True(p["direct_report_score"] is null && p["internal_customer_score"] is null, "small groups still hidden");
        Assert.Equal(2.86m, p["others_score"], "pool of 3 merged into All others (20/7)");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Feedback_360_flow_from_nomination_to_results_keeps_single_answers_private()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var hr = MatrixUser("HR-USER", ErpRoles.HrTalent, "Employee");
        var subject = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var peerB = MatrixUser("STAFF-B");
        var peerPlt = MatrixUser("PLT-A");
        var peerPm = MatrixUser("PM-B");
        var ess = sp.GetRequiredService<EssFeedbackService>();
        var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
        var due = D.Iso(Hr.Today(clock).AddDays(14));

        await docs.InsertAsync(sys, "Competency", new Dictionary<string, object?> { ["competency_code"] = "COMM", ["competency_name"] = "Communication", ["category"] = "Core Value" });
        await docs.InsertAsync(sys, "AppraisalCycle", new Dictionary<string, object?>
        {
            ["cycle_name"] = "FY2026", ["company"] = "ACME", ["start_date"] = "2026-01-01", ["end_date"] = "2026-12-31", ["status"] = "In progress",
        });
        await docs.InsertAsync(sys, "FeedbackTemplate", new Dictionary<string, object?>
        {
            ["template_name"] = "Staff 360", ["min_peers"] = 3, ["anonymity_threshold"] = 3, ["max_raters"] = 8,
            ["questions"] = new List<Dictionary<string, object?>>
            {
                new() { ["competency"] = "COMM", ["question"] = "Explains clearly", ["answer_type"] = "Rating", ["is_required"] = true },
                new() { ["question"] = "One thing to keep doing", ["answer_type"] = "Comment", ["is_required"] = false },
            },
        });
        var appraisal = await docs.InsertAsync(sys, "Appraisal", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["appraisal_cycle"] = "FY2026",
            ["goals"] = new List<Dictionary<string, object?>> { new() { ["goal"] = "Deliver project", ["weight"] = 100m } },
            ["competencies"] = new List<Dictionary<string, object?>> { new() { ["competency"] = "COMM", ["required_level"] = 3L } },
        });

        // HR creates the review: Self and Manager are added automatically.
        var review = await docs.InsertAsync(hr, "MultiRaterReview", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["appraisal_cycle"] = "FY2026", ["template"] = "Staff 360", ["due_date"] = due,
        });
        Assert.Equal(MultiRater.Nominating, review.Data.Str("status"), "starts with nominating");
        Assert.True(review.Data.Rows("reviewers").Any(r => r.Str("relationship") == MultiRater.Self && r.Str("rater") == "STAFF-A")
                    && review.Data.Rows("reviewers").Any(r => r.Str("relationship") == MultiRater.Manager && r.Str("rater") == "MGR-A"), "self and manager added");
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(hr, "MultiRaterReview", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["appraisal_cycle"] = "FY2026", ["template"] = "Staff 360", ["due_date"] = due,
        }), "one review per employee and cycle");

        // The employee nominates three peers and cannot touch the status.
        var rows = review.Data.Rows("reviewers").Select(r => new Dictionary<string, object?>(r)).ToList();
        foreach (var p in new[] { "STAFF-B", "PLT-A", "PM-B" }) rows.Add(new() { ["rater"] = p, ["relationship"] = MultiRater.Peer });
        review = await docs.UpdateAsync(subject, "MultiRaterReview", review.Name, review.Version, new Dictionary<string, object?> { ["reviewers"] = rows });
        Assert.Equal(5, review.Data.Rows("reviewers").Count, "five raters");
        await Assert.Throws<PermissionDeniedException>(() => docs.UpdateAsync(subject, "MultiRaterReview", review.Name, review.Version,
            new Dictionary<string, object?> { ["status"] = MultiRater.Closed }), "status is read-only");

        FormActionContext Ctx(UserContext u, Document d) => new() { User = u, Meta = default!, Doc = d, Docs = docs, Services = sp };
        var submit = ActivatorUtilities.CreateInstance<MultiRaterSubmitNominationsAction>(sp);
        Assert.True((await submit.ExecuteAsync(Ctx(subject, review), default)).Success, "nominations sent");
        review = await docs.GetAsync(sys, "MultiRaterReview", review.Name);
        Assert.Equal(MultiRater.AwaitingApproval, review.Data.Str("status"), "awaiting the manager");

        var fewer = review.Data.Rows("reviewers").Where(r => r.Str("rater") != "PM-B").Select(r => new Dictionary<string, object?>(r)).ToList();
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(subject, "MultiRaterReview", review.Name, review.Version,
            new Dictionary<string, object?> { ["reviewers"] = fewer }), "the employee cannot change the list after sending it");

        var send = ActivatorUtilities.CreateInstance<MultiRaterSendRequestsAction>(sp);
        Assert.True(!send.IsVisible(subject, review) && send.IsVisible(manager, review), "only the manager (or HR) approves");
        Assert.True((await send.ExecuteAsync(Ctx(manager, review), default)).Success, "requests sent");
        review = await docs.GetAsync(sys, "MultiRaterReview", review.Name);
        Assert.Equal(MultiRater.InProgress, review.Data.Str("status"), "in progress");
        Assert.Equal(5m, review.Data.Dec("responses_requested"), "five requests");

        // Every rater answers through the ESS API; their own request only.
        async Task AnswerAsync(UserContext u, int score, string? comment = null)
        {
            var mine = (await ess.RequestsAsync(u, u.Attr("employee")!, default)).Single(r => r.Data.Str("review") == review.Name);
            await ess.SaveAsync(u, u.Attr("employee")!, mine.Name,
                new EssFeedbackSave([new(1, score, comment), new(2, null, comment is null ? null : "keep it up")], null, true), default);
        }
        await AnswerAsync(subject, 4);
        await AnswerAsync(manager, 3, "Needs structure");
        await AnswerAsync(peerB, 5, "Great explainer");
        await AnswerAsync(peerPlt, 4);

        var pmRequest = (await ess.RequestsAsync(peerPm, "PM-B", default)).Single();
        var peerBResponse = (await ess.RequestsAsync(peerB, "STAFF-B", default)).Single();
        await Assert.Throws<DocumentNotFoundException>(() => ess.GetAsync(peerPm, "PM-B", peerBResponse.Name, default), "a rater cannot open another rater's answers");
        await Assert.Throws<Exception>(() => docs.GetAsync(subject, "FeedbackResponse", peerBResponse.Name), "the employee cannot open single responses");
        await Assert.Throws<ValidationFailedException>(() => ess.SaveAsync(peerPm, "PM-B", pmRequest.Name, new EssFeedbackSave([], null, true), default),
            "the required rating must be answered before submitting");
        await AnswerAsync(peerPm, 3);

        review = await docs.GetAsync(sys, "MultiRaterReview", review.Name);
        Assert.Equal(5m, review.Data.Dec("responses_submitted"), "progress updated on each submit");

        var close = ActivatorUtilities.CreateInstance<MultiRaterCloseAction>(sp);
        Assert.True(!close.IsVisible(manager, review) && close.IsVisible(hr, review), "HR closes");
        Assert.True((await close.ExecuteAsync(Ctx(hr, review), default)).Success, "closed");
        review = await docs.GetAsync(sys, "MultiRaterReview", review.Name);
        Assert.Equal(MultiRater.Closed, review.Data.Str("status"), "closed");
        var result = review.Data.Rows("results").Single();
        Assert.Equal(4m, result.Dec("peer_score"), "peers (5, 4, 3)");
        Assert.Equal(3.75m, result.Dec("others_score"), "manager + peers");
        Assert.Equal(4m, review.Data.Dec("overall_self"), "self");
        Assert.True(review.Data.Str("comments_summary")!.Contains("(Manager) Needs structure"), "comments summary");

        appraisal = await docs.GetAsync(sys, "Appraisal", appraisal.Name);
        Assert.Equal(3.75m, appraisal.Data.Rows("competencies").Single().Dec("feedback_360_score"), "copied to the appraisal competency");

        // The employee sees the aggregated result; nobody can save the closed review or a response any more.
        var results = await ess.ResultsAsync(subject, "STAFF-A", default);
        Assert.Equal(1, results.Count, "own result visible");
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(subject, "MultiRaterReview", review.Name, review.Version,
            new Dictionary<string, object?> { ["reviewers"] = review.Data.Rows("reviewers") }), "closed review is read-only");

        var report = await new FeedbackSummaryReport(sp.GetRequiredService<IDocumentStore>()).RunAsync(hr,
            new Dictionary<string, string?> { ["appraisal_cycle"] = "FY2026" }, docs, default);
        Assert.Equal(1, report.Rows.Count, "summary report row");
    }
}
