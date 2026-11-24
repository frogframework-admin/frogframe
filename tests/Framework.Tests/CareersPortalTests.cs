using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apps.Erp.Careers;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Email;
using Framework.Files;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Careers portal: Job Posting rules, the public job list (only live postings, nothing internal), apply → e-mail
/// confirmation → Job Applicant, status link, withdrawal, bots and captcha.
/// </summary>
sealed partial class Tests
{
    private sealed class CapturingEmailQueue : IEmailQueue
    {
        public List<(string Tenant, EmailMessage Message, string? Key)> Sent { get; } = [];

        public Task QueueAsync(string tenantId, EmailMessage message, string? idempotencyKey = null, CancellationToken ct = default)
        {
            if (idempotencyKey is null || Sent.All(s => s.Key != idempotencyKey)) Sent.Add((tenantId, message, idempotencyKey));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedHumanCheck(bool pass) : ICareersHumanCheck
    {
        public bool Enabled => true;
        public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct) => Task.FromResult(pass && token == "ok");
    }

    private const string CareersNotice = "Data Anda dipakai untuk proses rekrutmen dan disimpan paling lama 12 bulan.";

    private static string TokenFrom(EmailMessage m) =>
        Uri.UnescapeDataString(Regex.Match(m.BodyHtml, "token=([A-Za-z0-9_\\-%]+)").Groups[1].Value);

    private static MemoryStream Pdf() => new(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << >>\n%%EOF\n"));

    private static async Task<Document> SubmittedRequisitionAsync(DocumentService docs, UserContext sys, string title, int openings = 1)
    {
        var req = await docs.InsertAsync(sys, "JobRequisition", new Dictionary<string, object?>
        {
            ["job_title"] = title, ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD",
            ["openings"] = openings, ["hiring_manager"] = "MGR-A", ["salary_min"] = 7_123_000m, ["salary_max"] = 9_456_000m,
            ["description"] = "<p>Menjual produk ke pelanggan.</p><script>alert('x')</script>",
        });
        req = await docs.RunActionAsync(sys, "JobRequisition", req.Name, "Submit for approval", req.Version, null);
        req = await docs.RunActionAsync(sys, "JobRequisition", req.Name, "Approve (Unit Head)", req.Version, null);
        return await docs.RunActionAsync(sys, "JobRequisition", req.Name, "Approve (HR)", req.Version, null);
    }

    private static async Task CareersSetupAsync(DocumentService docs, UserContext sys) =>
        await docs.SetSingleAsync(sys, "HRSettings", new Dictionary<string, object?>
        {
            ["careers_enabled"] = true, ["careers_portal_url"] = "https://karir.acme.co.id/", ["careers_privacy_notice"] = CareersNotice,
        });

    [Test]
    public async Task Careers_portal_apply_confirm_status_and_withdraw()
    {
        var mail = new CapturingEmailQueue();
        await using var sp = BuildEssRequestProvider(s => s.AddSingleton<IEmailQueue>(mail));
        var (docs, store, m) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var svc = sp.GetRequiredService<CareersService>();
        var today = Hr.Today(TimeProvider.System);

        // Off by default: the public API answers 404.
        var off = await Assert.Throws<CareersException>(() => svc.ListJobsAsync("acme", default), "portal off");
        Assert.Equal(404, off.Status, "portal off → 404");
        await Assert.Throws<ValidationFailedException>(() => docs.SetSingleAsync(sys, "HRSettings", new Dictionary<string, object?> { ["careers_enabled"] = true }),
            "turning the portal on needs the portal address and the privacy notice");
        await CareersSetupAsync(docs, sys);

        // Postings: only for approved requisitions, one published posting per requisition, description sanitized.
        var draft = await docs.InsertAsync(sys, "JobRequisition", new Dictionary<string, object?>
        {
            ["job_title"] = "Draft job", ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD", ["hiring_manager"] = "MGR-A",
        });
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(sys, "JobPosting", new Dictionary<string, object?> { ["job_requisition"] = draft.Name }),
            "a draft requisition cannot be posted");
        var req = await SubmittedRequisitionAsync(docs, sys, "Sales Executive");
        var posting = await docs.InsertAsync(sys, "JobPosting", new Dictionary<string, object?> { ["job_requisition"] = req.Name, ["location_label"] = "Semarang" });
        Assert.Equal("Sales Executive", posting.Data.Str("posting_title"), "title defaults to the requisition's job title");
        Assert.True(posting.Data.Str("description")?.Contains("<script", StringComparison.OrdinalIgnoreCase) == false
                    && posting.Data.Str("description")?.Contains("Menjual produk") == true, "description copied and sanitized");
        Assert.Equal("Live", posting.Data.Str("posting_status"), "posting is live");
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(sys, "JobPosting", new Dictionary<string, object?> { ["job_requisition"] = req.Name }),
            "only one published posting per requisition");

        // A posting past its closing date is not listed and answers 410.
        var oldReq = await SubmittedRequisitionAsync(docs, sys, "Old job");
        var closed = await docs.InsertAsync(sys, "JobPosting", new Dictionary<string, object?>
        {
            ["job_requisition"] = oldReq.Name, ["posting_date"] = D.Iso(today.AddDays(-10)), ["closing_date"] = D.Iso(today.AddDays(-1)),
        });
        Assert.Equal("Closed", closed.Data.Str("posting_status"), "past closing date → Closed");
        Assert.Equal(410, (await Assert.Throws<CareersException>(() => svc.GetJobAsync("acme", closed.Name, default), "closed job")).Status, "closed job → 410");

        var jobs = await svc.ListJobsAsync("acme", default);
        Assert.Equal(1, jobs.Count, "only the live posting is listed");
        Assert.Equal(posting.Name, jobs[0].Id, "listed posting");
        Assert.Equal("Acme Corp", jobs[0].Company, "company name, not code");
        var json = JsonSerializer.Serialize(jobs) + JsonSerializer.Serialize(await svc.GetJobAsync("acme", posting.Name, default));
        Assert.True(!json.Contains("7123000") && !json.Contains("9456000") && !json.Contains("MGR-A") && !json.Contains(req.Name),
            "salary, hiring manager and requisition number are not public");
        var detail = await svc.GetJobAsync("acme", posting.Name, default);
        Assert.Equal(CareersNotice, detail.PrivacyNotice, "privacy notice shown with the job");

        CareersApplyInput Input(string email, bool consent = true, string? honeypot = null) => new()
        {
            FullName = "  Siti   Rahma ", Email = email, Mobile = "+62 812-3456-7890", Consent = consent,
            PrivacyNoticeVersion = detail.PrivacyNoticeVersion, Honeypot = honeypot, ResumeFileName = "cv-siti.pdf", Resume = Pdf(),
        };

        var invalid = await Assert.Throws<CareersException>(() => svc.ApplyAsync("acme", posting.Name, Input("bukan-email", consent: false), default), "invalid form");
        Assert.Equal(422, invalid.Status, "invalid form → 422");
        Assert.True(invalid.Errors.Any(e => e.Path == "email") && invalid.Errors.Any(e => e.Path == "consent"), "email and consent errors listed");
        await Assert.Throws<CareersException>(() => svc.ApplyAsync("acme", posting.Name, new CareersApplyInput
        {
            FullName = "Siti", Email = "siti@example.com", Consent = true, PrivacyNoticeVersion = "old", ResumeFileName = "cv.pdf", Resume = Pdf(),
        }, default), "consent to an older privacy notice is rejected");

        await svc.ApplyAsync("acme", posting.Name, Input("bot@example.com", honeypot: "http://spam"), default);
        Assert.Equal(0, mail.Sent.Count, "hidden field filled → nothing sent");

        // Apply: an e-mail with a confirmation link, nothing in HR's list yet.
        await svc.ApplyAsync("acme", posting.Name, Input("Siti.Rahma@Example.com"), default);
        Assert.Equal(1, mail.Sent.Count, "confirmation e-mail queued");
        Assert.Equal("siti.rahma@example.com", mail.Sent[0].Message.To.Single(), "address normalized");
        Assert.True(mail.Sent[0].Message.BodyHtml.Contains("https://karir.acme.co.id/confirm?token="), "link points to the portal");
        Assert.Equal(0, (await m.QueryAsync("JobApplicant", [], default)).Count, "no applicant before confirmation");
        var confirmToken = TokenFrom(mail.Sent[0].Message);

        Assert.Equal(401, (await Assert.Throws<CareersException>(() => svc.ConfirmAsync("acme", confirmToken[..^4] + "AAAA", default), "tampered")).Status,
            "tampered link → 401");
        var confirmed = await svc.ConfirmAsync("acme", confirmToken, default);
        Assert.True(!confirmed.AlreadyApplied, "new application");
        var applicant = await docs.GetAsync(sys, "JobApplicant", confirmed.Application);
        Assert.Equal("Siti Rahma", applicant.Data.Str("applicant_name"), "name cleaned");
        Assert.Equal(req.Name, applicant.Data.Str("job_requisition"), "linked to the requisition");
        Assert.Equal(posting.Name, applicant.Data.Str("job_posting"), "linked to the posting");
        Assert.Equal(CareersService.PortalSource, applicant.Data.Str("source"), "source");
        Assert.Equal(CareersNotice, applicant.Data.Str("consent_text"), "notice agreed to is stored");
        Assert.True(applicant.Data.Str("consent_at") is not null, "consent time stored");
        var cv = await sp.GetRequiredService<IFileStore>().GetInfoAsync("acme", applicant.Data.Str("resume")!);
        Assert.Equal("JobApplicant", cv?.DocType, "CV attached to the applicant");

        var again = await svc.ConfirmAsync("acme", confirmToken, default);
        Assert.True(again.AlreadyApplied && again.Application == confirmed.Application, "second click returns the same application");

        // Applying again: the applicant is told by e-mail, no second confirmation link.
        await svc.ApplyAsync("acme", posting.Name, Input("siti.rahma@example.com"), default);
        Assert.Equal(2, mail.Sent.Count, "duplicate notice sent");
        Assert.True(!mail.Sent[1].Message.BodyHtml.Contains("token="), "duplicate notice has no confirmation link");

        // Status: link only for known addresses; confirmation links cannot be used as status links.
        await svc.RequestStatusLinkAsync("acme", "unknown@example.com", null, null, default);
        Assert.Equal(2, mail.Sent.Count, "unknown address → no e-mail, same answer");
        await svc.RequestStatusLinkAsync("acme", "siti.rahma@example.com", null, null, default);
        Assert.Equal(3, mail.Sent.Count, "status link sent");
        var statusToken = TokenFrom(mail.Sent[2].Message);
        await Assert.Throws<CareersException>(() => svc.ApplicationsAsync("acme", confirmToken, default), "confirmation link is not a status link");

        var apps = await svc.ApplicationsAsync("acme", statusToken, default);
        Assert.Equal(1, apps.Count, "one application");
        Assert.Equal("Received", apps[0].Stage, "Applied shown as Received");
        Assert.True(apps[0].CanWithdraw, "can withdraw");

        var withdrawn = await svc.WithdrawAsync("acme", statusToken, apps[0].Id, default);
        Assert.Equal("Withdrawn", withdrawn.Stage, "withdrawn");
        Assert.Equal("Withdrawn", (await docs.GetAsync(sys, "JobApplicant", apps[0].Id)).Data.Str("status"), "status on the applicant");
        Assert.Equal(409, (await Assert.Throws<CareersException>(() => svc.WithdrawAsync("acme", statusToken, apps[0].Id, default), "twice")).Status,
            "withdrawn applications cannot be withdrawn again");

        // Another tenant: 404, even with a valid link of this tenant.
        Assert.Equal(404, (await Assert.Throws<CareersException>(() => svc.ApplicationsAsync("globex", statusToken, default), "other tenant")).Status,
            "other tenant without the portal → 404");
    }

    [Test]
    public async Task Careers_portal_requires_captcha_when_configured_and_hides_internal_stages()
    {
        var mail = new CapturingEmailQueue();
        await using var sp = BuildEssRequestProvider(s =>
        {
            s.AddSingleton<IEmailQueue>(mail);
            s.AddSingleton<ICareersHumanCheck>(new FixedHumanCheck(pass: true));
        });
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var svc = sp.GetRequiredService<CareersService>();
        await CareersSetupAsync(docs, sys);
        var req = await SubmittedRequisitionAsync(docs, sys, "Admin Gudang");
        var posting = await docs.InsertAsync(sys, "JobPosting", new Dictionary<string, object?> { ["job_requisition"] = req.Name });
        var version = (await svc.GetJobAsync("acme", posting.Name, default)).PrivacyNoticeVersion;

        CareersApplyInput Input(string? captcha) => new()
        {
            FullName = "Budi", Email = "budi@example.com", Consent = true, PrivacyNoticeVersion = version, CaptchaToken = captcha,
            ResumeFileName = "cv.pdf", Resume = Pdf(),
        };
        Assert.Equal(400, (await Assert.Throws<CareersException>(() => svc.ApplyAsync("acme", posting.Name, Input(null), default), "no captcha")).Status,
            "missing captcha → 400");
        Assert.Equal(0, mail.Sent.Count, "nothing sent without captcha");
        await svc.ApplyAsync("acme", posting.Name, Input("ok"), default);
        var confirmed = await svc.ConfirmAsync("acme", TokenFrom(mail.Sent.Single().Message), default);

        // HR moves the applicant to Rejected with a reason: the applicant sees "Closed", never the reason.
        var a = await docs.GetAsync(sys, "JobApplicant", confirmed.Application);
        await docs.UpdateAsync(sys, "JobApplicant", a.Name, a.Version, new Dictionary<string, object?> { ["status"] = "Rejected", ["rejection_reason"] = "Gaji terlalu tinggi" });
        var apps = await svc.ApplicationsAsync("acme", confirmed.StatusToken, default);
        Assert.Equal("Closed", apps.Single().Stage, "Rejected shown as Closed");
        Assert.True(!apps.Single().CanWithdraw && !JsonSerializer.Serialize(apps).Contains("Gaji"), "no withdrawal, no reason shown");
        Assert.Equal("In review", CareersService.PublicStage("Interview"), "interview shown as In review");
    }
}
