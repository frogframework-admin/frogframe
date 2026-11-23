using Apps.Erp.Hooks;
using Framework.Communication;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Careers;

public sealed class JobPosting { }

/// <summary>Status of a posting on the careers portal, with the requisition it advertises.</summary>
internal sealed record PostingState(string Status, Document? Requisition)
{
    public const string Live = "Live", Scheduled = "Scheduled", Closed = "Closed", Unpublished = "Unpublished";
    public bool IsLive => Status == Live;
}

internal static class JobPostings
{
    /// <summary>
    /// Live = published, shown from today, not past the closing date, and the requisition is submitted and not yet filled
    /// (hired &lt; openings). Cancelling or filling the requisition closes its postings without editing them.
    /// </summary>
    public static async Task<PostingState> StateAsync(Masters m, Document posting, DateOnly today, CancellationToken ct)
    {
        var d = posting.Data;
        var req = await m.GetAsync("JobRequisition", d.Str("job_requisition"), ct);
        if (!d.Bool("is_published")) return new(PostingState.Unpublished, req);
        if (req is null || req.DocStatus != DocStatus.Submitted) return new(PostingState.Closed, req);
        if (d.Date("closing_date") is { } closes && closes < today) return new(PostingState.Closed, req);
        if (await FilledAsync(m, req, ct)) return new(PostingState.Closed, req);
        if (d.Date("posting_date") is { } from && from > today) return new(PostingState.Scheduled, req);
        return new(PostingState.Live, req);
    }

    /// <summary>Hired applicants reached the number of openings.</summary>
    public static async Task<bool> FilledAsync(Masters m, Document req, CancellationToken ct) =>
        (await m.QueryAsync("JobApplicant", [new FilterClause("job_requisition", "=", req.Name), new FilterClause("status", "=", "Hired")], ct, take: 5000)).Count
        >= Math.Max(1m, req.Data.Dec("openings"));

    /// <summary>Title shown to applicants: the posting title, else the requisition's job title.</summary>
    public static string Title(Document posting, Document? req) =>
        posting.Data.Str("posting_title") ?? req?.Data.Str("job_title") ?? posting.Name;
}

/// <summary>
/// Job Posting: the requisition must be submitted (approved); title, description and location label default from the
/// requisition and the work location; at most one published posting per requisition; the description is sanitized
/// because it is shown on a public site. The portal status is recalculated when the posting is opened.
/// </summary>
public sealed class JobPostingHook(IDocumentStore store, TimeProvider? clock = null) : IBeforeValidate<JobPosting>, IOnLoad<JobPosting>
{
    private DateOnly Today => Hr.Today(clock ?? TimeProvider.System);

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var req = await m.RequireAsync("JobRequisition", d.Str("job_requisition"), "job_requisition", "Job requisition", ct);
        var reqChanged = c.Previous is null || c.Previous.Data.Str("job_requisition") != req.Name;
        if (reqChanged && req.DocStatus != DocStatus.Submitted)
            throw Errors.One("job_requisition", $"{req.Name} is not approved (submitted); only approved requisitions can be posted");

        d.SetIfEmpty("posting_title", req.Data.Str("job_title"));
        d.SetIfEmpty("description", req.Data.Str("description"));
        if (await m.GetAsync("Location", d.Str("location"), ct) is { } loc)
            d.SetIfEmpty("location_label", loc.Data.Str("city") ?? loc.Data.Str("location_name"));
        if (d.Str("description") is { } html) d["description"] = HtmlContentSanitizer.Sanitize(html);

        if (d.Bool("is_published"))
        {
            var other = (await m.QueryAsync("JobPosting", [new FilterClause("job_requisition", "=", req.Name), new FilterClause("is_published", "=", true)], ct, take: 20))
                .FirstOrDefault(p => p.Name != c.Doc.Name);
            if (other is not null)
                throw Errors.One("is_published", $"{req.Name} is already published as {other.Name}; unpublish it first");
        }

        d["posting_status"] = (await JobPostings.StateAsync(m, c.Doc, Today, ct)).Status;
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct) =>
        c.Doc.Data["posting_status"] = (await JobPostings.StateAsync(new Masters(store, c.Doc.TenantId), c.Doc, Today, ct)).Status;
}
