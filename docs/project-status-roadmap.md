# FrogFramework: Project Status Report & Feature Roadmap

This document summarizes the current development status of FrogFramework, including completed features, testing coverage, backlog items (gap analysis against modern enterprise platform capabilities), and the upcoming development roadmap.

*Last updated: November 2026*

---

## 1. Current Project Health

- **Compilation Status (`dotnet build`):** `0 Errors`, `0 Critical Warnings` across all projects (`Framework.Core`, `Framework.Persistence.PostgreSql`, `Framework.Web`, `Apps.Sample`, `Framework.Tests`).
- **Unit & Integration Test Status:** **249 Passed, 0 Failed** (34 tests decorated with `PgTest` are skipped when the local PostgreSQL server is inactive).
- **Database & Migration Status:** 35 PostgreSQL migrations registered and verified (`001_initial.sql` through `035_translations.sql`).
- **Git Repository Status:** Clean working tree.

---

## 2. Completed Features

### A. Core Foundation & Production Hardening (P0 - 100% Complete)
- **Multi-Node Cluster Synchronization:** Real-time cross-node cache invalidation and presence tracking using PostgreSQL `LISTEN/NOTIFY` on channel `frogframe_cluster` without requiring Redis ([`guides/multi-node.md`](guides/multi-node.md)).
- **S3-Compatible Object Storage:** Offloads file and attachment binary contents to SeaweedFS (or AWS S3/MinIO/Ceph RGW) while maintaining metadata, ownership, and ACLs in PostgreSQL ([`guides/file-and-image-storage.md`](guides/file-and-image-storage.md)).
- **Postgres Distributed Lock & Scheduler:** Atomic distributed locking based on PostgreSQL advisory locks to prevent duplicate execution of multi-tenant background cron jobs.
- **Data Protection & Encryption:** Persistent XML key ring storage in PostgreSQL database (`019_data_protection_keys.sql`).
- **Security & Pipeline Hardening:** Rate limiting protection, anti-tamper forms, IDOR mitigation, SQL injection sanitization, and security header middleware (CSP, HSTS, X-Frame-Options).
- **Relational Integrity (Link Checking):** Automatic referential integrity validation preventing deletion of parent documents still referenced by other documents or child tables.
- **Observability:** OpenTelemetry Tracing, activity metrics (Meters & Activities), and centralized Error Logging (`020_error_logs.sql`).
- **Backup & Disaster Recovery:** Comprehensive DR runbooks and k6 load testing baselines ([`backup-disaster-recovery.md`](backup-disaster-recovery.md)).
- **Automated CI & Documentation Pipeline:** GitHub Actions workflows for continuous integration (`ci.yml` - build, vulnerability check, in-memory & PostgreSQL test suite) and automated DocFX website publishing (`deploy-docs.yml`).

### B. Modern UI/UX & Design
- **DocType Layout Palette & Subtabs:** Visual layout designer palette supporting *Tab (top)*, *Tab in section (subtabs)*, *Section*, and *Column* breaks with drag-and-drop field grouping ([`guides/doctype-and-workflows.md`](guides/doctype-and-workflows.md)).
- **Multi-Language (i18n):** Bilingual UI (English & Indonesian) with runtime language switcher, auto-fallback, and tenant-specific database overrides (`035_translations.sql`, [`guides/multi-language.md`](guides/multi-language.md)).
- **Corporate Blue Theme & Full Dark Mode:** Seamless light and dark mode support with synchronized theme CSS variables.
- **Layout & Navigation:** Sticky topbar, role-based dynamic navigation, collapsible sidebar, breadcrumbs, row-range footer, and modern wave visual accents.
- **Admin Drawers:** Standard and double-width (`admin-drawer-2x`) drawer components for configuring complex forms without page transitions.

### C. Pilot Features & Productivity
1. **Multi-File Attachments per Document (P1-1):**
   - Multi-file uploads, MIME type inspection (magic byte sniffing), secure PostgreSQL / S3 storage, and ACL enforcement.
2. **Email Outbox & Template Engine (P1-2):**
   - Idempotent email outbox queue, retry backoff, dead-letter queue, and dynamic email templating.
3. **Forgot & Reset Password (P1-3):**
   - Self-service password reset workflow via secure, single-use, auto-expiring email token links.
4. **Awesomebar / Global Search (`Ctrl+K` / `Alt+Q`):**
   - Centralized search palette indexing DocTypes, administrative pages, action shortcuts, and workspace modules.
5. **Data Grid Column Preferences (P1-5):**
   - Per-user customizable table columns (visibility toggle, column reordering, width persistence in `023_user_preferences.sql`).
6. **Background Job Monitor & Error Log Dashboard:**
   - Real-time diagnostic viewer at `/admin/jobs` and `/admin/error-logs` with stack trace drawer inspection.
7. **Single DocType (Platform Feature):**
   - Singleton document schema per tenant for system and module configuration (e.g. `AccountingSettings`).
   - Dedicated APIs (`GetSingleAsync`, `SetSingleAsync`), compiler validation (workflow rejection), and UI adaptations (direct form routing, breadcrumbs, search quick action suppression).
8. **Extended Field Types (21 Types):**
   - Rich input components including `TableMultiSelect`, `Autocomplete`, `Color`, `Geolocation`, `Icon`, `Barcode`, `Signature`, `Duration`, `Heading`, and layout breaks.

### D. Automation & Extensibility (Phase 3 - 100% Complete)
1. **Customization Layer (Custom Fields & Property Setters):**
   - Non-destructive overlay compilation (`frogframe_customizations`, `033_customizations.sql`) allowing administrators to customize application DocTypes without losing changes during upstream deployments.
2. **Client Scripts & Monaco Code Editor ([`server-scripts.md`](server-scripts.md)):**
   - Interactive form scripts (`Load`, `Change`, `Validate`) executed with sandboxed `IScriptForm` API, real-time Monaco editor autocomplete, AST diagnostic markers, and snippet support (`034_client_scripts.sql`).
3. **C# Server Scripting via Roslyn Sandbox ([`server-scripts.md`](server-scripts.md)):**
   - Sandboxed C# Roslyn compilation with strict AST analyzer rejecting forbidden namespaces (`System.IO`, `System.Net`, `System.Reflection`), statement budget rewriter preventing infinite loops, collectible `AssemblyLoadContext`, and runtime triggers for DocEvents, Custom APIs, and Scheduled Jobs (`029_server_scripts.sql`, `031_server_script_allowed_roles.sql`).
4. **Outbound Webhooks ([`webhooks.md`](webhooks.md)):**
   - HMAC-SHA256 request payload signatures, SSRF IP and DNS-rebinding protection callbacks, deduplicated background delivery queue, exponential retry backoff, and delivery logs (`027_webhooks.sql`).
5. **Auto Repeat ([`auto-repeat.md`](auto-repeat.md)):**
   - Recurring document automation with `AllowAutoRepeat` and `NoCopy` metadata, hourly scheduled job with catch-up logic, and zero duplicates under concurrency (`028_auto_repeat.sql`).
6. **Inbound Email & Communication Timeline ([`communications.md`](communications.md)):**
   - MailKit/MimeKit integration for SMTP and IMAP polling, 4-stage thread matching (`In-Reply-To`, `References`, `[#DOC-NAME]`, `AppendToDocType`), server-side HTML sanitization, document email composer, and unified communication history timeline (`030_communications.sql`).

---

## 3. Next Platform Milestones (High-Priority Gaps)

> [!IMPORTANT]
> The architectural blueprint and Frappe parity analysis are documented in [High-Priority Platform Gaps](high-priority-gaps.md). The two primary upcoming initiatives are:

### Option 1: Gap 1 — Customization Layer (Custom Fields & Property Setters Overlay)
- **Database Schema (`031_customizations.sql`)**: `frogframe_customizations` table (`tenant_id`, `doctype`, `kind`, `target`, `value jsonb`, `version`, `updated_at`, `updated_by`).
- **Overlay Compilation in `Compiler.cs`**: `Base DocTypeDef (file/app) -> Apply Tenant Customizations -> CompiledDocType`. Base files update independently during deploy without overwriting tenant custom fields.
- **Custom Field Engine**: Mandatory `custom_` prefix to prevent naming collisions; `InsertAfter` ordering to position custom fields in form layouts.
- **Property Setter Engine**: Overrides field and DocType properties (`Label`, `Visible`, `RequiredWhen`, `Options`, `InListView`, `PermLevel`, `ReadOnly`).
- **Conflict & Orphan Detection in `MetadataDiff`**: Flags when upstream updates delete or rename targeted fields.
- **UI & Admin**: Admin interface at `/admin/customizations` and overlay editing mode in `DocTypeEditor.razor`.

### Option 2: Gap 3 — Document Collaboration & Activity Timeline
- **Discussions & Comments (`frogframe_comments`)**: Rich comments with `@mentions` triggering in-app notifications and email outbox alerts.
- **Assignments & ToDo (`frogframe_assignments`)**: Document assignments with due dates, integrated into "My Tasks / Inbox".
- **Per-Document Row Sharing (`frogframe_doc_shares`)**: Ad-hoc access grants (`can_read`, `can_write`, `can_share`) integrated directly into `PermissionService.ScopeFor`.
- **Document Tagging**: GIN-indexed `tags text[]` with tag filter pills on list pages and forms.
- **Document Followers**: Follow/unfollow records with automated notification digests on status changes.

| Code | Module / Feature | Description | Urgency |
|---|---|---|---|
| **P1-4** | **Comments & @mention Timeline** | Internal collaboration thread directly beneath the document form (interleaved discussion and audit trail) with `@username` tagging and notification triggers. | High |
| **P1-5** | **Export List & Bulk Actions** | Export tabular data to Excel (`.xlsx`) and `.csv` files (respecting user permissions and active filter scopes), plus bulk operations: **Bulk Submit**, **Bulk Cancel**, and **Bulk Edit Field** (extending existing Bulk Delete). | High |
| **P1-7** | **Assign To / ToDo & "My Tasks" Widget** | Document delegation to specific staff/users, due date tracking, and a dedicated "My Tasks" dashboard widget for monitoring pending assignments. | Medium |
| **P1-8** | **Saved Filters / Custom Views** | User-defined saved search filter combinations per DocType, persisting named presets via the established `IUserPreferenceStore` foundation. | Medium |

### Priority 2: Platform Scalability & Extensibility (P2)

- **Custom Fields / Customize Form per Tenant:** Add or reorder DocType fields dynamically from the web interface per tenant without altering underlying JSON metadata files.
- **Webhooks:** Outbox-driven document event payload dispatcher to external webhook URLs, featuring HMAC signatures and automated exponential backoff retries.
- **Ad-Hoc Report Builder:** Non-technical user interface to build, sort, filter, and group document data into customizable and savable reports.
- **Dynamic Row-Level Security (User Permissions per Record):** Granular document-level access permissions that can be shared with specific users or roles directly from the UI.
- **Advanced Authentication (SSO & 2FA):** Google and Microsoft Entra ID (OIDC) single sign-on integration, combined with TOTP app-based two-factor authentication (2FA).
- **Generic Document Rename:** Document identifier renaming engine that automatically cascades updates to all Link foreign keys in child tables and related documents.
- **Ad-Hoc Background Jobs (`FrogFrame.enqueue`):** Asynchronous heavy task queue with live progress bar feedback in the web interface.
- **Database Role Segregation:** Separation of database migration owner role from runtime application connection role for append-only audit enforcement.
- **Dynamic Workflows:** Visual state machine transition editor with conditional approvals.

### Priority 3: Specialized Views & Value-Add Enhancements (P3)

- **Alternate Views:**
  - **Kanban Board:** Workflow stage column cards with drag-and-drop state progression.
  - **Calendar View:** Date-based document tracking (due dates, delivery schedules, event appointments).
  - **Tree View:** Hierarchical nested structure navigation (Chart of Accounts, Cost Centers, Department trees).
- **Visual Print Format Builder & Letter Head UI:** Interactive letterhead designer and visual print format template layout editor.
- **Social Features:** Color-coded document tags, personal bookmarks/favorites, and Follow Document subscriptions with automated update notifications.
- **Dynamic Multi-Language (i18n):** Runtime UI localization switching between English and Indonesian.

---

## 4. Recommended Implementation Sequence

Recommended execution sequence for the upcoming development cycles:

1. **P1-5 — Export List to Excel/CSV & Bulk Actions:**
   - Provide **Export to Excel (`.xlsx`)** and **Export to CSV** actions on the `ListPage.razor` toolbar.
   - Expand list bulk actions: Bulk Submit, Bulk Cancel, and Bulk Edit Field.
2. **P1-4 — Comments & @mention Timeline:**
   - Embed an activity and discussion panel directly below `FormPage.razor`.
   - Deliver automated in-app notifications when a user is tagged with `@username`.
3. **P1-8 — Saved Filters / Custom Views:**
   - Utilize `IUserPreferenceStore` to persist and quickly load named search filter presets per DocType.
4. **P1-7 — Assign To / ToDo & "My Tasks" Widget:**
   - Enable user task assignment, due dates, and a "My Tasks" dashboard widget for pending assignments.
5. **Platform Scalability (P2) & Specialized Views (P3):**
   - Progress into Tenant Custom Fields, Webhooks, SSO/2FA, and Alternate Views (Kanban, Calendar, Tree).
