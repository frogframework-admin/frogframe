# Careers Portal API (v1)

The careers portal is a separate public website (for example `https://karir.acme.co.id`) where people see open jobs
and apply. It has no database of its own: it calls the public API of the ERP under **`/api/careers/v1/{tenant}`**.
Applicants never get an ERP account and never see the ERP desk.

```
Browser ──> careers site (your front end) ──> /api/careers/v1/{tenant}/... ──> JobPosting, JobApplicant
```

What HR sees is unchanged: a confirmed application is an ordinary **Job Applicant** with source *Company website*,
linked to the requisition and the posting, with the CV attached.

## 1. Setting up

1. **HR Settings › Careers portal** (per tenant):

   | Field | Meaning |
   |---|---|
   | Accept applications from the careers portal | Turns the API on for this tenant. Off: every call answers 404. |
   | Careers portal address | Public address of the careers site. E-mail links point here, and browsers on this origin may call the API (CORS). |
   | Privacy notice for applicants | Shown before applying; the applicant must agree. The text agreed to is stored on the applicant (UU PDP). |
   | Reply-to address | Reply-To of the e-mails sent to applicants. |

   The portal cannot be turned on without an address and a privacy notice.

2. **Job Posting** (menu *Talent & Recruitment › Job Posting*), one per approved (submitted) requisition:

   | Field | Meaning |
   |---|---|
   | Job requisition | Must be submitted. |
   | Title shown to applicants | Defaults to the requisition's job title. |
   | Work location / Location shown to applicants | The label defaults to the location's city. |
   | Published, Show from, Closing date | Publication window. Empty closing date: open until the requisition is filled or cancelled. |
   | Job description & requirements | Defaults to the requisition's description; HTML is sanitized. **Public**: no salary or internal notes. |
   | Status on the portal | *Live*, *Scheduled*, *Closed* or *Unpublished*, recalculated when the posting is opened. |

   Only one published posting per requisition. A posting is **Live** when it is published, shown from today or
   earlier, not past its closing date, and the requisition is submitted with fewer hired applicants than openings.
   Cancelling or filling the requisition closes the posting without editing it.

   Never public: salary range, reason, replaced employee, hiring manager, recruiter, requisition number, unit.

3. **Server settings** (`appsettings.json` or environment):

   | Setting | Default | Meaning |
   |---|---|---|
   | `Careers:Captcha:VerifyUrl`, `Careers:Captcha:Secret` | empty (off) | Captcha check with the *siteverify* protocol: Cloudflare Turnstile `https://challenges.cloudflare.com/turnstile/v0/siteverify`, hCaptcha `https://api.hcaptcha.com/siteverify`, reCAPTCHA `https://www.google.com/recaptcha/api/siteverify`. If the provider cannot be reached, the request is rejected. |
   | `Careers:ReadPerMinute` | 120 | GET requests per client IP per minute. |
   | `Careers:SubmitPerMinute` | 10 | POST requests per client IP per minute. |
   | `Careers:ConfirmLinkHours` | 12 | Validity of the confirmation link (1–23). Must stay below `Files:OrphanHours` (24), which removes CVs of unconfirmed applications. |
   | `Careers:StatusLinkHours` | 24 | Validity of the status link (1–168). |
   | `Careers:AllowedOrigins` | empty | Extra browser origins allowed to call the API, e.g. `http://localhost:3000` during development. |

   Behind a reverse proxy, forward the client IP (forwarded headers) so the limits apply per visitor. Expose only
   `/api/careers/` to the internet if the desk is not public. Data Protection keys must be shared by all instances,
   or links made on one instance fail on another.

## 2. Flow

1. The site lists jobs (`GET /jobs`) and shows one (`GET /jobs/{id}`) with the privacy notice.
2. The applicant fills the form and sends it with the CV (`POST /jobs/{id}/apply`). The ERP stores the CV and e-mails
   a confirmation link `{portal}/confirm?token=…`. **Nothing is visible to HR yet.**
3. The confirm page of the site sends the token (`POST /applications/confirm`). The Job Applicant is created.
4. Later the applicant asks for a status link (`POST /applications/status-link`), opens
   `{portal}/applications?token=…`, and the site reads the applications (`POST /applications/lookup`) and can withdraw
   one (`POST /applications/{id}/withdraw`).

Why e-mail confirmation: forms filled by bots or with someone else's address never reach HR, and the applicant
proves the address is theirs before the status link can show anything.

## 3. Endpoints

All paths start with `/api/careers/v1/{tenant}` (the tenant code, e.g. `acme`). JSON uses camelCase. Errors are
`application/problem+json` with `title`, `detail` and, for 422, `errors` (`path`, `message`). The texts are safe to
show to the visitor.

| Status | When |
|---|---|
| 404 | Tenant unknown or portal off; job or application not found |
| 410 | Job closed; CV upload expired (apply again) |
| 401 | Link invalid or expired |
| 409 | Privacy notice changed after applying; application can no longer be withdrawn |
| 413 | CV larger than 10 MB |
| 422 | Form errors |
| 429 | Too many requests (`Retry-After` header) |

### GET /jobs
```json
{ "jobs": [ { "id": "JP-2026-00001", "title": "Sales Executive", "company": "PT Acme Nusantara",
              "employmentType": "Permanent (PKWTT)", "location": "Semarang", "postedOn": "2026-11-20", "closesOn": "2026-12-15" } ],
  "captchaRequired": false }
```
Live postings only, newest first, at most 200. Cached for 60 seconds.

### GET /jobs/{id}
The job fields above plus `descriptionHtml` (sanitized HTML), `privacyNotice`, `privacyNoticeVersion` and
`captchaRequired`. Send `privacyNoticeVersion` back when applying.

### POST /jobs/{id}/apply
`multipart/form-data`:

| Field | Required | Notes |
|---|---|---|
| `fullName` | yes | 2–140 characters |
| `email` | yes | Stored in lower case |
| `mobile` | no | Digits, spaces, `+ ( ) -` |
| `consent` | yes | `true` |
| `privacyNoticeVersion` | yes | From `GET /jobs/{id}`; a different value means the notice changed: reload |
| `resume` | yes | PDF, Word or image, max 10 MB (content is checked, not the extension) |
| `captchaToken` | when `captchaRequired` | Also read from `cf-turnstile-response`, `h-captcha-response`, `g-recaptcha-response` |
| `website` | no | Hidden honeypot: keep it empty and hidden. Filled = treated as a bot (same answer, nothing stored) |

Answer `202` `{ "message": "…" }`. If the address already applied for this requisition, the applicant gets an
e-mail saying so instead of a confirmation link; the answer is the same, so the API does not reveal who applied.
At most one confirmation e-mail per address and job every 10 minutes.

```bash
curl -X POST https://erp.example.co.id/api/careers/v1/acme/jobs/JP-2026-00001/apply \
  -F fullName="Siti Rahma" -F email=siti@example.com -F consent=true \
  -F privacyNoticeVersion=3F2A9C1B7D4E5A60 -F resume=@cv.pdf
```

### POST /applications/confirm
`{ "token": "…" }` → `201` (created) or `200` (already confirmed):
```json
{ "application": "APP-2026-000123", "jobTitle": "Sales Executive", "alreadyApplied": false, "statusToken": "…" }
```
`statusToken` works like the status link, so the site can show the application right away. The application is
accepted while the requisition is open, even if the posting closed in the meantime.

### POST /applications/status-link
`{ "email": "…", "captchaToken": "…" }` → `202`. An e-mail with `{portal}/applications?token=…` is sent only if the
address has applied (at most one per hour); the answer is always the same.

### POST /applications/lookup
`{ "token": "…" }` →
```json
{ "applications": [ { "id": "APP-2026-000123", "jobTitle": "Sales Executive", "appliedOn": "2026-11-23",
                      "stage": "In review", "canWithdraw": true } ] }
```

| Stage in the ERP | Stage shown |
|---|---|
| Applied | Received |
| Screening, Interview | In review |
| Offer | Offer |
| Hired | Hired |
| Withdrawn | Withdrawn |
| Rejected | Closed (the rejection reason is never shown) |

### POST /applications/{id}/withdraw
`{ "token": "…" }` → the application as in `lookup`, stage *Withdrawn*. Only from *Received* or *In review*.

## 4. Security notes

- Tokens are signed and encrypted with ASP.NET Core Data Protection and expire; they carry the tenant and the
  address, so a link of one tenant does not work for another, and a confirmation link is not a status link.
- Tokens travel in POST bodies, not in API URLs, so they do not end up in server logs; the site reads them from its
  own page URL.
- The API is anonymous and limited per IP; the captcha is recommended for production.
- Applicants are created by the system user. Applicant personal data stays permission level 1 (HR only); the
  consent time and notice text are on the applicant.
- Not included yet: notifications to the recruiter (use a Notification Rule on *Job Applicant*, *On create*), deleting
  applicant data after the retention period, and job boards (they can use the same API with their own source later).

## 5. Where it lives

| Part | Location |
|---|---|
| Endpoints, CORS, rate limit | `src/Apps.Erp/Careers/CareersEndpoints.cs` |
| Logic, tokens, e-mails | `src/Apps.Erp/Careers/CareersService.cs` |
| Job Posting rules | `src/Apps.Erp/Careers/JobPostings.cs` |
| Captcha | `src/Apps.Erp/Careers/HumanCheck.cs` |
| Metadata | `JobPosting.doctype.json`; new fields on `JobApplicant` and `HRSettings` |
| Tests | `tests/Framework.Tests/CareersPortalTests.cs` |
