using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Email;
using Framework.Files;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Tenancy;
using Framework.Validation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Apps.Erp.Careers;

/// <summary>Error shown to a careers portal visitor: status code, short title and a message safe to display.</summary>
public sealed class CareersException(int status, string title, string message, IReadOnlyList<FieldError>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public IReadOnlyList<FieldError> Errors { get; } = errors ?? [];

    public static CareersException NotFound(string message = "Not found") => new(404, "Not found", message);
    public static CareersException Invalid(params FieldError[] errors) =>
        new(422, "Validation failed", string.Join(" ", errors.Select(e => e.Message)), errors);
}

/// <summary>Careers portal settings of a tenant (HR Settings, section "Careers portal").</summary>
public sealed record CareersSettings(string Tenant, string PortalUrl, string PrivacyNotice, string? ReplyTo)
{
    public string PrivacyNoticeHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PrivacyNotice)))[..16];
}

public sealed record CareersJob(string Id, string Title, string? Company, string? EmploymentType, string? Location, string PostedOn, string? ClosesOn);

public sealed record CareersJobDetail(CareersJob Job, string? DescriptionHtml, string PrivacyNotice, string PrivacyNoticeVersion);

public sealed class CareersApplyInput
{
    public string? FullName { get; init; }
    public string? Email { get; init; }
    public string? Mobile { get; init; }
    public bool Consent { get; init; }
    /// <summary>Version of the privacy notice the applicant saw (<c>privacyNoticeVersion</c> from the job detail).</summary>
    public string? PrivacyNoticeVersion { get; init; }
    public string? CaptchaToken { get; init; }
    /// <summary>Hidden form field that people leave empty; filled in = bot. Answered like a normal request.</summary>
    public string? Honeypot { get; init; }
    public string? RemoteIp { get; init; }
    public string? ResumeFileName { get; init; }
    public Stream? Resume { get; init; }
}

public sealed record CareersConfirmation(string Application, string JobTitle, bool AlreadyApplied, string StatusToken);

public sealed record CareersApplication(string Id, string JobTitle, string AppliedOn, string Stage, bool CanWithdraw);

/// <summary>
/// Public careers portal (see docs/guides/careers-portal-api.md). Nothing reaches HR until the applicant confirms the
/// e-mail link: applying stores the CV as an unattached file and mails a signed, time-limited link; confirming creates
/// the Job Applicant as the system user. Unconfirmed CVs are removed by the orphan-file cleanup (Files:OrphanHours).
/// Applicants have no account: their application status is read with a signed link mailed to the applicant's address.
/// Responses never reveal whether an e-mail address is known.
/// </summary>
public sealed class CareersService(
    DocumentService docs,
    IDocumentStore store,
    FileService files,
    IFileStore fileStore,
    IEmailQueue email,
    IDataProtectionProvider dataProtection,
    ICareersHumanCheck human,
    IConfiguration config,
    ILogger<CareersService> log,
    TimeProvider? clock = null,
    TenantDirectory? tenants = null)
{
    public const string PortalSource = "Company website";
    public const int MaxListed = 200;
    private const string ProtectorPurpose = "Apps.Erp.Careers.v1";
    private static readonly string[] WithdrawableStages = ["Applied", "Screening", "Interview"];
    private static readonly Regex MobilePattern = new(@"^\+?[0-9][0-9 ()\-]{5,19}$", RegexOptions.CultureInvariant);

    private TimeProvider Clock => clock ?? TimeProvider.System;
    private DateOnly Today => Hr.Today(Clock);
    private TimeSpan ConfirmLifetime => TimeSpan.FromHours(Math.Clamp(config.GetValue("Careers:ConfirmLinkHours", 12), 1, 23));
    private TimeSpan StatusLifetime => TimeSpan.FromHours(Math.Clamp(config.GetValue("Careers:StatusLinkHours", 24), 1, 168));
    private ITimeLimitedDataProtector Protector => dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    // ------------------------------------------------------------------ settings

    /// <summary>Settings of an active tenant whose careers portal is turned on; otherwise 404 (no hint whether the tenant exists).</summary>
    public async Task<CareersSettings> SettingsAsync(string tenant, CancellationToken ct)
    {
        if (!TenantIds.IsValid(tenant) || (tenants is not null && !await tenants.IsActiveAsync(tenant, ct)))
            throw CareersException.NotFound("The careers portal is not available");
        var s = await new Masters(store, tenant).SingleAsync("HRSettings", ct);
        if (!s.Bool("careers_enabled") || s.Str("careers_portal_url") is not { } url || s.Str("careers_privacy_notice") is not { } notice)
            throw CareersException.NotFound("The careers portal is not available");
        return new(tenant, url.TrimEnd('/'), notice, s.Str("careers_reply_to"));
    }

    /// <summary>Browser origin of the portal (scheme://host[:port]) for CORS, or null when the address is not an absolute URL.</summary>
    public static string? PortalOrigin(CareersSettings s) =>
        Uri.TryCreate(s.PortalUrl, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.GetLeftPart(UriPartial.Authority) : null;

    // ------------------------------------------------------------------ jobs

    public async Task<IReadOnlyList<CareersJob>> ListJobsAsync(string tenant, CancellationToken ct)
    {
        await SettingsAsync(tenant, ct);
        var m = new Masters(store, tenant);
        var postings = await m.QueryAsync("JobPosting", [new FilterClause("is_published", "=", true)], ct, "posting_date", "desc", 1000);
        var result = new List<CareersJob>();
        foreach (var p in postings)
        {
            var state = await JobPostings.StateAsync(m, p, Today, ct);
            if (state.IsLive) result.Add(await JobAsync(m, p, state.Requisition, ct));
            if (result.Count >= MaxListed) break;
        }
        return result;
    }

    public async Task<CareersJobDetail> GetJobAsync(string tenant, string id, CancellationToken ct)
    {
        var settings = await SettingsAsync(tenant, ct);
        var m = new Masters(store, tenant);
        var (posting, req) = await LivePostingAsync(m, id, ct);
        return new(await JobAsync(m, posting, req, ct), posting.Data.Str("description"), settings.PrivacyNotice, settings.PrivacyNoticeHash);
    }

    private async Task<(Document Posting, Document Requisition)> LivePostingAsync(Masters m, string? id, CancellationToken ct)
    {
        var posting = string.IsNullOrWhiteSpace(id) || id.Length > 64 ? null : await m.GetAsync("JobPosting", id, ct);
        if (posting is null) throw CareersException.NotFound("This job was not found");
        var state = await JobPostings.StateAsync(m, posting, Today, ct);
        if (!state.IsLive || state.Requisition is null)
            throw state.Status == PostingState.Closed
                ? new CareersException(410, "Closed", "This job is no longer accepting applications")
                : CareersException.NotFound("This job was not found");
        return (posting, state.Requisition);
    }

    private static async Task<CareersJob> JobAsync(Masters m, Document p, Document? req, CancellationToken ct)
    {
        var d = p.Data;
        var company = d.Str("company") ?? req?.Data.Str("company");
        var companyName = company is null ? null : await m.StrAsync("Company", company, "company_name", ct) ?? company;
        return new(p.Name, JobPostings.Title(p, req), companyName, d.Str("employment_type") ?? req?.Data.Str("employment_type"),
            d.Str("location_label"), D.Iso(d.Date("posting_date") ?? DateOnly.FromDateTime(p.Created.Date)),
            d.Date("closing_date") is { } c ? D.Iso(c) : null);
    }

    // ------------------------------------------------------------------ apply → confirm

    /// <summary>
    /// Validates the form, stores the CV and mails a confirmation link. The answer is the same whether or not the address
    /// already applied (the e-mail tells the applicant). Bots that fill the hidden field get the same answer and nothing is stored.
    /// </summary>
    public async Task ApplyAsync(string tenant, string postingId, CareersApplyInput input, CancellationToken ct)
    {
        var settings = await SettingsAsync(tenant, ct);
        var m = new Masters(store, tenant);
        var (posting, req) = await LivePostingAsync(m, postingId, ct);

        var name = Clean(input.FullName, 140);
        var address = NormalizeEmail(input.Email);
        var mobile = Clean(input.Mobile, 20);
        var errors = new List<FieldError>();
        if (name is null || name.Length < 2) errors.Add(new("fullName", "Fill in your full name"));
        if (address is null) errors.Add(new("email", "Fill in a valid e-mail address"));
        if (mobile is not null && !MobilePattern.IsMatch(mobile)) errors.Add(new("mobile", "Fill in a valid mobile number"));
        if (!input.Consent) errors.Add(new("consent", "You must agree to the privacy notice to apply"));
        else if (input.PrivacyNoticeVersion != settings.PrivacyNoticeHash)
            errors.Add(new("consent", "The privacy notice has changed. Reload the page, read it and agree again"));
        if (input.Resume is null) errors.Add(new("resume", "Attach your CV"));
        if (errors.Count > 0) throw CareersException.Invalid([.. errors]);

        if (!string.IsNullOrEmpty(input.Honeypot))
        {
            log.LogInformation("Careers application to {Posting} ignored: hidden field filled (ip {Ip})", posting.Name, input.RemoteIp);
            return;
        }
        if (!await human.VerifyAsync(input.CaptchaToken, input.RemoteIp, ct))
            throw new CareersException(400, "Verification failed", "Please complete the verification and try again");

        var title = JobPostings.Title(posting, req);
        var existing = await FindApplicationAsync(m, req.Name, address!, ct);
        if (existing is not null)
        {
            await MailAsync(settings, address!, $"Lamaran Anda untuk {title}",
                $"<p>Halo {Html(name)},</p><p>Anda sudah melamar posisi <b>{Html(title)}</b> sebelumnya, jadi lamaran baru tidak kami buat.</p>" +
                "<p>Untuk melihat status lamaran Anda, buka halaman karier dan pilih <i>Cek status lamaran</i>.</p>",
                $"careers-dup:{tenant}:{req.Name}:{address}:{Bucket(TimeSpan.FromHours(1))}", ct);
            return;
        }

        string fileId;
        try
        {
            fileId = await files.UploadFieldFileAsync(UserContext.System(tenant), "JobApplicant", "resume",
                input.ResumeFileName ?? "cv", input.Resume!, ct);
        }
        catch (FileRejectedException ex) { throw CareersException.Invalid(new FieldError("resume", ex.Message)); }

        var token = Protect(new Ticket
        {
            Kind = "apply", Tenant = tenant, Posting = posting.Name, Name = name!, Email = address!, Mobile = mobile, File = fileId,
            ConsentAt = Clock.GetUtcNow().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture),
            Notice = settings.PrivacyNoticeHash,
        }, ConfirmLifetime);
        var link = $"{settings.PortalUrl}/confirm?token={Uri.EscapeDataString(token)}";
        await MailAsync(settings, address!, $"Konfirmasi lamaran: {title}",
            $"<p>Halo {Html(name)},</p><p>Terima kasih telah melamar posisi <b>{Html(title)}</b>. Lamaran Anda baru kami terima setelah Anda menekan tombol di bawah ini.</p>" +
            Button(link, "Konfirmasi lamaran") +
            $"<p><small>Tautan berlaku {ConfirmLifetime.TotalHours:0} jam. Jika Anda tidak merasa melamar, abaikan e-mail ini.</small></p>",
            $"careers-apply:{tenant}:{posting.Name}:{address}:{Bucket(TimeSpan.FromMinutes(10))}", ct);
    }

    /// <summary>Creates the Job Applicant from a confirmation link. Clicking the link again returns the same application.</summary>
    public async Task<CareersConfirmation> ConfirmAsync(string tenant, string? token, CancellationToken ct)
    {
        var settings = await SettingsAsync(tenant, ct);
        var t = Unprotect(token, "apply", tenant);
        var m = new Masters(store, tenant);
        var posting = await m.GetAsync("JobPosting", t.Posting, ct) ?? throw CareersException.NotFound("This job was not found");
        var req = await m.GetAsync("JobRequisition", posting.Data.Str("job_requisition"), ct) ?? throw CareersException.NotFound("This job was not found");
        var title = JobPostings.Title(posting, req);
        var statusToken = StatusToken(tenant, t.Email);

        if (await FindApplicationAsync(m, req.Name, t.Email, ct) is { } existing)
            return new(existing.Name, title, true, statusToken);
        // Accepted while the requisition is still open, even if the posting closed or was unpublished after the applicant applied.
        if (req.DocStatus != DocStatus.Submitted || await JobPostings.FilledAsync(m, req, ct))
            throw new CareersException(410, "Closed", "This job is no longer accepting applications");
        if (t.Notice != settings.PrivacyNoticeHash)
            throw new CareersException(409, "Apply again", "The privacy notice changed after you applied. Please apply again");
        if (t.File is null || await fileStore.GetInfoAsync(tenant, t.File, ct) is not { DocType: null })
            throw new CareersException(410, "Apply again", "Your uploaded CV is no longer available. Please apply again");

        Document applicant;
        try
        {
            applicant = await docs.InsertAsync(UserContext.System(tenant), "JobApplicant", new Dictionary<string, object?>
            {
                ["applicant_name"] = t.Name, ["email"] = t.Email, ["mobile"] = t.Mobile, ["job_requisition"] = req.Name,
                ["job_posting"] = posting.Name, ["status"] = "Applied", ["source"] = PortalSource, ["resume"] = t.File,
                ["consent_at"] = t.ConsentAt, ["consent_text"] = settings.PrivacyNotice,
            }, "Applied via careers portal", ct);
        }
        catch (ValidationFailedException ex)
        {
            // Two clicks at the same moment: the second insert fails on the duplicate check.
            if (await FindApplicationAsync(m, req.Name, t.Email, ct) is { } raced) return new(raced.Name, title, true, statusToken);
            log.LogWarning("Careers application for {Posting} rejected: {Message}", posting.Name, ex.Message);
            throw new CareersException(409, "Application not accepted", "Your application could not be saved. Please contact us");
        }
        log.LogInformation("Careers application {Applicant} created for {Requisition} via {Posting}", applicant.Name, req.Name, posting.Name);
        return new(applicant.Name, title, false, statusToken);
    }

    // ------------------------------------------------------------------ status

    /// <summary>Mails a link to the applicant's applications. Same answer whether or not the address is known.</summary>
    public async Task RequestStatusLinkAsync(string tenant, string? emailAddress, string? captchaToken, string? remoteIp, CancellationToken ct)
    {
        var settings = await SettingsAsync(tenant, ct);
        var address = NormalizeEmail(emailAddress) ?? throw CareersException.Invalid(new FieldError("email", "Fill in a valid e-mail address"));
        if (!await human.VerifyAsync(captchaToken, remoteIp, ct))
            throw new CareersException(400, "Verification failed", "Please complete the verification and try again");
        var m = new Masters(store, tenant);
        if ((await m.QueryAsync("JobApplicant", [new FilterClause("email", "=", address)], ct, take: 1)).Count == 0) return;
        var link = $"{settings.PortalUrl}/applications?token={Uri.EscapeDataString(StatusToken(tenant, address))}";
        await MailAsync(settings, address, "Status lamaran Anda",
            "<p>Halo,</p><p>Tekan tombol di bawah ini untuk melihat status lamaran Anda.</p>" + Button(link, "Lihat status lamaran") +
            $"<p><small>Tautan berlaku {StatusLifetime.TotalHours:0} jam. Jika Anda tidak memintanya, abaikan e-mail ini.</small></p>",
            $"careers-status:{tenant}:{address}:{Bucket(TimeSpan.FromHours(1))}", ct);
    }

    public async Task<IReadOnlyList<CareersApplication>> ApplicationsAsync(string tenant, string? token, CancellationToken ct)
    {
        await SettingsAsync(tenant, ct);
        var t = Unprotect(token, "status", tenant);
        var m = new Masters(store, tenant);
        var list = new List<CareersApplication>();
        foreach (var a in await m.QueryAsync("JobApplicant", [new FilterClause("email", "=", t.Email)], ct, take: 100))
        {
            var posting = await m.GetAsync("JobPosting", a.Data.Str("job_posting"), ct);
            var req = await m.GetAsync("JobRequisition", a.Data.Str("job_requisition"), ct);
            var title = posting is not null ? JobPostings.Title(posting, req) : req?.Data.Str("job_title") ?? "-";
            var stage = a.Data.Str("status") ?? "Applied";
            list.Add(new(a.Name, title, D.Iso(DateOnly.FromDateTime(a.Created.Date)), PublicStage(stage), WithdrawableStages.Contains(stage)));
        }
        return list.OrderByDescending(x => x.AppliedOn).ToList();
    }

    public async Task<CareersApplication> WithdrawAsync(string tenant, string? token, string application, CancellationToken ct)
    {
        await SettingsAsync(tenant, ct);
        var t = Unprotect(token, "status", tenant);
        var sys = UserContext.System(tenant);
        var a = await new Masters(store, tenant).GetAsync("JobApplicant", application, ct);
        if (a is null || !string.Equals(a.Data.Str("email"), t.Email, StringComparison.OrdinalIgnoreCase))
            throw CareersException.NotFound("This application was not found");
        var stage = a.Data.Str("status") ?? "Applied";
        if (!WithdrawableStages.Contains(stage))
            throw new CareersException(409, "Cannot withdraw", "This application can no longer be withdrawn. Please contact us");
        await docs.UpdateAsync(sys, "JobApplicant", a.Name, a.Version, new Dictionary<string, object?> { ["status"] = "Withdrawn" }, ct: ct);
        log.LogInformation("Careers application {Applicant} withdrawn by the applicant", a.Name);
        return (await ApplicationsAsync(tenant, token, ct)).First(x => x.Id == a.Name);
    }

    /// <summary>What the applicant sees: internal stages are grouped, rejection reasons are never shown.</summary>
    public static string PublicStage(string stage) => stage switch
    {
        "Applied" => "Received",
        "Screening" or "Interview" => "In review",
        "Offer" => "Offer",
        "Hired" => "Hired",
        "Withdrawn" => "Withdrawn",
        _ => "Closed",
    };

    // ------------------------------------------------------------------ helpers

    private static async Task<Document?> FindApplicationAsync(Masters m, string requisition, string address, CancellationToken ct) =>
        (await m.QueryAsync("JobApplicant", [new FilterClause("job_requisition", "=", requisition), new FilterClause("email", "=", address)], ct, take: 1))
        .FirstOrDefault();

    private string StatusToken(string tenant, string address) =>
        Protect(new Ticket { Kind = "status", Tenant = tenant, Email = address }, StatusLifetime);

    private sealed class Ticket
    {
        public string Kind { get; init; } = "";
        public string Tenant { get; init; } = "";
        public string Email { get; init; } = "";
        public string? Posting { get; init; }
        public string Name { get; init; } = "";
        public string? Mobile { get; init; }
        public string? File { get; init; }
        public string? ConsentAt { get; init; }
        public string? Notice { get; init; }
    }

    private string Protect(Ticket t, TimeSpan lifetime) => Protector.Protect(JsonSerializer.Serialize(t), lifetime);

    private Ticket Unprotect(string? token, string kind, string tenant)
    {
        Ticket? t = null;
        if (!string.IsNullOrWhiteSpace(token) && token.Length <= 8192)
        {
            try { t = JsonSerializer.Deserialize<Ticket>(Protector.Unprotect(token, out _)); }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { t = null; }
        }
        if (t is null || t.Kind != kind || t.Tenant != tenant || string.IsNullOrEmpty(t.Email))
            throw new CareersException(401, "Link expired", "This link is invalid or has expired. Please request a new one");
        return t;
    }

    private Task MailAsync(CareersSettings s, string to, string subject, string bodyHtml, string idempotencyKey, CancellationToken ct) =>
        email.QueueAsync(s.Tenant, new EmailMessage
        {
            To = [to], Subject = subject, BodyHtml = bodyHtml, ReplyTo = s.ReplyTo,
            BodyText = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(bodyHtml.Replace("</p>", "\n\n"), "<a [^>]*href=\"([^\"]+)\"[^>]*>.*?</a>", "$1")), "<[^>]+>", "").Trim(),
        }, idempotencyKey, ct);

    private static string Button(string link, string label) =>
        $"<p><a href=\"{Html(link)}\" style=\"display:inline-block;padding:10px 16px;background:#2563eb;color:#ffffff;text-decoration:none;border-radius:6px;\">{Html(label)}</a></p>" +
        $"<p><small>Atau salin alamat ini ke browser:<br/>{Html(link)}</small></p>";

    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");

    private long Bucket(TimeSpan size) => Clock.GetUtcNow().ToUnixTimeSeconds() / (long)size.TotalSeconds;

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = Regex.Replace(value.Trim(), @"[\p{Cc}\s]+", " ");
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>Lower-case address, or null when it is not a single plain address.</summary>
    public static string? NormalizeEmail(string? value)
    {
        var s = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s) || s.Length > 254 || s.Any(char.IsWhiteSpace)) return null;
        try
        {
            var a = new MailAddress(s);
            return a.Address == s && a.Host.Contains('.') ? s : null;
        }
        catch (FormatException) { return null; }
    }
}
