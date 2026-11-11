# Inbound Email & Communication Timeline

FrogFrame includes a bidirectional email and communication infrastructure designed for document-centric workflows. It enables sending emails directly from documents, logging outgoing and incoming correspondence into a unified activity timeline, and automatically ingesting incoming emails via IMAP into new or existing business documents.

---

## 1. Architecture & Core Concepts

```
                  ┌──────────────────────────────────────────────┐
                  │                 FormPage                     │
                  │  [Send Email]  ──►  IEmailQueue (Outbox)     │
                  │  [Timeline]    ◄──  ICommunicationStore      │
                  └───────────────────────▲──────────────────────┘
                                          │
                                          │ Outbound Log
   ┌────────────────────────────────┐     │
   │      OutboxEmailHandler        ├─────┤
   │  (MailKit SMTP / MimeKit)      │     │
   └───────────────▲────────────────┘     │
                   │                      │
   ┌───────────────┴────────────────┐     │
   │      EmailAccountPollJob       │     │
   │  (@every_5m Scheduled Job)     │     │
   └───────────────▲────────────────┘     │
                   │                      │
   ┌───────────────┴────────────────┐     │ Inbound Record
   │     InboundEmailProcessor      ├─────┘
   │  (Thread Match & Doc Append)   │
   └───────────────▲────────────────┘
                   │
         [IMAP Server (MailKit)]
```

### Key Components

1. **MailKit & MimeKit Modern Engine**: Replaces legacy `System.Net.Mail` with RFC-compliant MIME parsing and SMTP/IMAP protocol support.
2. **Communication Store (`ICommunicationStore`)**: Central log of all incoming and outgoing document messages (`frogframe_communications`), preventing duplicate entries via `(tenant_id, message_id)` unique constraint.
3. **IMAP Poller (`EmailAccountPollJob : IScheduledJob`)**: Runs every 5 minutes (`@every_5m`), checking active email accounts per tenant, advancing UIDs per message, and tolerating server restarts and `UIDVALIDITY` resets.
4. **Unified FormPage Timeline**: Combines document audit trails and email communications into a single chronological feed with expandable sanitized HTML bodies, attachments, and one-click replies.
5. **Admin Management (`/admin/email-accounts`)**: Web interface for managing IMAP mailboxes, live connection testing, and resolving unlinked incoming messages.

---

## 2. Inbound Message Resolution Order

When an incoming email is retrieved, `InboundEmailProcessor` resolves its target document using a four-stage hierarchy:

```
 Incoming Message
        │
        ▼
 1. Check In-Reply-To / References Header  ──► Found? ──► Attach to Matched Document
        │ (No)
        ▼
 2. Check Subject Tag [#DOC-NAME]           ──► Found? ──► Attach to Matched Document
        │ (No)
        ▼
 3. Has Account "Append To DocType"?       ──► Yes   ──► Insert Document via DocumentService
        │ (No)                                           (Run as ServiceUserId via IAutomationIdentity)
        ▼
 4. Store as Unlinked Communication        ──► Listed in Admin "/admin/email-accounts"
```

1. **Header Threading**: Checks `In-Reply-To` and `References` headers against known `message_id`s in `frogframe_communications`. If matched, the incoming message is attached to the parent document.
2. **Subject Tag Matching**: If headers are missing or stripped by intermediate mail gateways, inspects the subject line for `[#DOC-NAME]` (e.g., `[#PR-2026-0001]`). If the document exists in the tenant, the message is attached.
3. **Automatic Document Creation (Append To DocType)**: If unthreaded and the email account specifies an `AppendToDocType` (e.g. `Customer`, `Issue`, `Lead`), a new document is created via `DocumentService.InsertAsync`:
   - Runs under the identity of `ServiceUserId` through `IAutomationIdentity`.
   - Maps email properties into document fields using `FieldMap` (e.g. `"subject" -> "title"`, `"sender" -> "customer_name"`, `"content_text" -> "description"`).
   - If the service user lacks `create` permission on the target DocType, the operation fails and an audit error is logged without compromising security.
4. **Unlinked Fallback**: Messages that cannot be resolved are stored as unlinked communications (`reference_doctype IS NULL`), allowing administrators to manually link them to documents.

---

## 3. Security Hardening

Inbound email processing enforces multiple layers of defensive security:

### Auto-Responder Loop Prevention
Incoming messages are inspected for automated response headers:
- `Auto-Submitted` (values other than `no`)
- `X-Auto-Response-Suppress`
- `Precedence: bulk`, `auto_reply`, `junk`, or `list`

Auto-responders and bounce notifications are safely skipped to avoid infinite mail loops.

### HTML Sanitization (XSS Defense)
Raw incoming HTML is sanitized on the server before database storage using `Ganss.Xss.HtmlSanitizer` (AngleSharp):
- Script tags (`<script>`) are stripped.
- Malicious inline event handlers (`onload`, `onerror`, `onclick`) are removed.
- Disallowed URL schemes (such as `javascript:` or raw `data:`) are sanitized.
- Safe formatting (`<p>`, `<b>`, `<i>`, tables, blockquotes) is preserved.

### Attachment & Size Limits
- Total message size is limited to 25 MB (`MaxEmailSizeBytes`). Oversized messages have their body truncated and attachments discarded while recording header metadata.
- Attachments are inspected using magic-byte sniffing via `AttachmentService.SniffContentType`. SVGs, executable binaries, and unknown file types are rejected.

### Access Control & Permission Scoping
- The sender address (`From:`) is strictly informational and is never used to grant authentication or elevated permissions.
- In `CommunicationService`, reading communications via `ListForDocumentAsync(user, docType, docName)` demands read permission on the underlying document via `DocumentService.GetAsync(user, docType, docName)`. Users lacking document permission or row-scope access receive `PermissionDeniedException`.
- IMAP passwords are stored encrypted in PostgreSQL using ASP.NET Data Protection keys (`ISecretProtector`).

---

## 4. Database Schema (`030_communications.sql`)

```sql
-- Migration 030: Inbound email accounts and communication logs
CREATE TABLE IF NOT EXISTS frogframe_email_accounts (
    id UUID PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    email_address TEXT NOT NULL,
    imap_host TEXT NOT NULL,
    imap_port INTEGER NOT NULL DEFAULT 993,
    use_ssl BOOLEAN NOT NULL DEFAULT TRUE,
    username TEXT NOT NULL,
    password_enc TEXT NOT NULL,
    folder TEXT NOT NULL DEFAULT 'INBOX',
    append_to_doctype TEXT,
    field_map JSONB NOT NULL DEFAULT '{}'::jsonb,
    service_user_id TEXT NOT NULL,
    uid_validity BIGINT NOT NULL DEFAULT 0,
    last_uid BIGINT NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    last_polled_at TIMESTAMPTZ,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    modified_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_frogframe_email_accounts_tenant_active 
    ON frogframe_email_accounts (tenant_id, is_active);

CREATE TABLE IF NOT EXISTS frogframe_communications (
    id UUID PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    reference_doctype TEXT,
    reference_name TEXT,
    direction TEXT NOT NULL, -- 'In' or 'Out'
    message_id TEXT NOT NULL,
    in_reply_to TEXT,
    subject TEXT NOT NULL,
    sender TEXT NOT NULL,
    recipients TEXT[] NOT NULL DEFAULT '{}',
    cc TEXT[] NOT NULL DEFAULT '{}',
    content_html TEXT,
    content_text TEXT,
    email_account_id UUID REFERENCES frogframe_email_accounts(id) ON DELETE SET NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_frogframe_communications_tenant_msgid UNIQUE (tenant_id, message_id)
);

CREATE INDEX IF NOT EXISTS ix_frogframe_communications_timeline 
    ON frogframe_communications (tenant_id, reference_doctype, reference_name, occurred_at DESC);

CREATE INDEX IF NOT EXISTS ix_frogframe_communications_unlinked 
    ON frogframe_communications (tenant_id, created_at DESC)
    WHERE reference_doctype IS NULL;
```

---

## 5. UI Features

### Document Form (`/app/{DocType}/{Name}`)
- **Email Action**: Click "Email" in the top action bar to open the email composer drawer.
- **Auto-Subject Marker**: Automatically prefixes the subject line with `[#{Name}] {DocType}` to ensure threading on replies.
- **Activity Feed**: The "History & Emails" tab displays a combined, filterable view of document audits and email messages with directional badges ("Incoming" / "Outgoing"), timestamps, and expandable HTML bodies.
- **Quick Reply**: Clicking "Reply" on an email communication pre-populates recipient, `In-Reply-To`, and quoted history in the composer.

### Admin Dashboard (`/admin/email-accounts`)
- Configure IMAP accounts, ports, SSL, credentials, and append settings.
- **Live Connection Test**: Validates IMAP host, port, credentials, and folder reachability directly from the browser.
- **Unlinked Emails Drawer**: Inspects incoming emails that were not matched to any document, with an interface to link them to an existing document.

---

## 6. Verification & Automated Tests

Workstream D is validated by unit and integration tests in `tests/Framework.Tests/InboundEmailTests.cs`:

1. `InboundEmail_deduplication_on_message_id`: Proves identical message IDs are stored only once.
2. `InboundEmail_thread_matching_via_in_reply_to`: Confirms replies attach to parent document via `In-Reply-To`.
3. `InboundEmail_thread_matching_via_subject_tag`: Verifies `[#DOC-NAME]` subject pattern matching.
4. `InboundEmail_append_to_doctype_creates_document_with_field_mapping`: Verifies automated document creation under service user identity.
5. `InboundEmail_append_to_doctype_fails_when_service_user_lacks_create_permission`: Verifies unauthorized service users are blocked by `DocumentService`.
6. `InboundEmail_skips_auto_submitted_and_bulk_emails`: Confirms auto-responder rejection.
7. `InboundEmail_html_sanitization_strips_scripts_and_event_handlers`: Proves XSS vectors (`<script>`, `onerror`) are stripped from HTML.
8. `InboundEmail_resumes_cleanly_after_interrupted_run`: Verifies state resumption without duplicate processing.
9. `InboundEmail_handles_uid_validity_change_without_duplicates`: Verifies clean rescan upon server `UIDVALIDITY` changes.
10. `InboundEmail_communication_service_enforces_read_access`: Proves unauthorized and cross-tenant users cannot read document communications.
