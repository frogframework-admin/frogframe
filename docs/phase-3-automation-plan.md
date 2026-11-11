# Phase 3: Automation & Integration Work Plan

This engineering specification details the architecture, roadmap, and test requirements for **Phase 3: Automation & Integration**. Phase 3 introduces four core capabilities:
1. **Outbound Webhooks** (HMAC-signed, SSRF-protected event dispatching)
2. **Auto Repeat** (Scheduled recurring document generation)
3. **C# Server Scripting** (Sandboxed Roslyn compilation for DocEvents, APIs, and Cron)
4. **Inbound Email & Communication** (IMAP poller, MailKit integration, and unified document timeline)

All four capabilities build upon existing FrogFrame infrastructural primitives: outbox messaging (`OutboxProcessor`, `IEventHandler`), document lifecycle hooks (`HookRunner`, `DocHookStage`), background scheduler (`ScheduledJobRunner`, `CronSchedule`), and API method registration (`ApiMethodRegistry`).

*Last updated: November 2026*

---

## 1. Execution Sequence & Dependency Order

To minimize development risk, execution proceeds strictly from lowest risk to highest:

| Order | Workstream | Description & Rationale | Dependencies |
|---|---|---|---|
| **0** | **Shared Foundations** | Common event catalog, retry policy, "run-as" identity, secret encryption, and telemetry. | Foundation complete |
| **A** | **Outbound Webhooks** | Smallest surface area; follows established `NotificationRule` patterns with dedicated delivery queue. | Step 0 |
| **B** | **Auto Repeat** | Pure scheduler + `InsertAsync`; runs standard validation without arbitrary user code execution. | Step 0 |
| **C** | **C# Server Scripting (Roslyn)** | Highest architectural risk (user-supplied code in-process); requires strict AST sandbox and resource budgets. | Step 0, Step A (for audit/run logs) |
| **D** | **Inbound Email & Communication** | Replaces legacy `SmtpClient` with MailKit, adds IMAP background poller and thread matching. | Step 0 |

---

## 2. Core Architecture

```
                    +-----------------------+
                    |    DocumentService    |
                    +-----------------------+
                                |
               Lifecycle Events (EventTypes.cs)
                                |
                                v
                    +-----------------------+
                    |    OutboxProcessor    |
                    +-----------------------+
                        /       |        \
                       /        |         \
                      v         v          v
          +---------------+ +--------+ +---------------------+
          | Notifications | | Emails | | WebhookEventHandler |
          +---------------+ +--------+ +---------------------+
                                                 |
                                         (Deduplicated Queue)
                                                 v
                                       frogframe_webhook_deliveries
                                                 |
                                                 v
                                       WebhookDeliveryWorker
                                        (SocketsHttpHandler)
                                                 |
                                           HMAC-SHA256
                                                 v
                                          External Server
```

Key Architectural Principles:
1. **Document Integrity via DocumentService**: Any automation that creates or modifies documents (Auto Repeat, scheduled scripts, inbound email document append) passes through `DocumentService` under a designated user identity. Validation, permissions, row scopes, and audit logging are strictly enforced.
2. **Decoupled Delivery Queues**: Event handlers for external integrations write work to their own dedicated database tables (`ON CONFLICT DO NOTHING`) rather than performing HTTP or network I/O inside the outbox handler loop.
3. **Execution Context**: Server Script DocEvents run inside `HookRunner` in the same database transaction, enabling synchronous validation and rejection. Webhooks read outbox events asynchronously, ensuring third-party downtime never blocks internal transactions.

---

## 3. Step 0: Shared Foundations

The shared foundations must be completed before starting Workstreams A–D:

### 3.1 Event Catalog (`EventTypes.cs`)
- Class: `Framework.Core.Jobs.EventTypes`
- Standard constants:
  - `DocumentInserted = "document.inserted"`
  - `DocumentUpdated = "document.updated"`
  - `DocumentDeleted = "document.deleted"`
  - `DocumentAmended = "document.amended"`
  - `WorkflowTransitioned = "workflow.transitioned"`
  - Helper: `DocumentAction(string action) => $"document.{action.Trim().ToLowerInvariant()}"`
- All string literals in `DocumentService.cs` are replaced with these constants.
- Action names are normalized to lowercase (e.g. `document.submit`), aligning with `NotificationRuleJobs`.

### 3.2 Standardized Retry Policy (`RetryPolicy.cs`)
- Class: `Framework.Core.Jobs.RetryPolicy`
- Configuration: Exponential backoff with `MaxAttempts = 5` and interval $2^n$ seconds (1s, 2s, 4s, 8s, 16s).
- Methods: `DateTimeOffset GetNextAttempt(int attempt, DateTimeOffset now)` and `bool ShouldRetry(int attempt)`.

### 3.3 Store & Migration Pattern
- Follows existing `NotificationRule` patterns:
  - Interface and in-memory store in `Framework.Core`.
  - PostgreSQL store in `Framework.Persistence.PostgreSql`.
  - Registered via `services.Replace(...)` in `PostgresStores.cs`.
  - Migrations starting from `027_*.sql` in `Framework.Persistence.PostgreSql/Migrations`.

### 3.4 "Run-As" Automation Identity (`IAutomationIdentity`)
- Interface: `Framework.Security.IAutomationIdentity`
- Method: `Task<UserContext> ForUserAsync(string tenantId, string userId, CancellationToken ct = default)`
- Loads the account via `IUserAccountRepository`, rejects inactive accounts, and reconstructs the full `UserContext` with matching roles, department, and tenant isolation.
- Background automations are prohibited from using `UserContext.System(...)` for document mutations.

### 3.5 Secret Storage
- Sensitive credentials (webhook signing secrets, IMAP passwords) are encrypted using ASP.NET Core Data Protection under the purpose `FrogFrame.Automation.Secrets`.
- Plaintext secrets are never returned in UI or API responses; forms display masked status with a "Change Secret" action.

---

## 4. Workstream A: Outbound Webhooks

- **Database Migrations (`027_webhooks.sql`)**:
  - `frogframe_webhooks`: `id`, `tenant_id`, `name`, `doctype`, `events text[]`, `condition`, `url`, `headers jsonb`, `secret_enc`, `fields text[]`, `is_active`, `created_at`, `modified_at`.
  - `frogframe_webhook_deliveries`: `id uuid`, `tenant_id`, `webhook_id`, `event_id uuid`, `event_type`, `doc_name`, `payload jsonb`, `status`, `attempts`, `next_attempt_at`, `lease_until`, `last_status_code`, `last_error`, `response_snippet`, `duration_ms`, `sent_at`.
  - `UNIQUE (event_id, webhook_id)` guarantees idempotency.
- **SSRF Protection**:
  - Outbound HTTP requests disallow loopback (`127.0.0.1`), RFC 1918 private subnets, link-local metadata addresses (`169.254.169.254`), and IPv6 equivalents.
  - DNS resolution is verified at connection time via `SocketsHttpHandler.ConnectCallback` to prevent DNS rebinding attacks.
- **Payload Signing**:
  - Header: `X-FrogFrame-Signature: sha256={HMAC-SHA256(secret, "{timestamp}.{body}")}`.
  - Additional headers: `X-FrogFrame-Event`, `X-FrogFrame-Delivery`, `X-FrogFrame-Timestamp`.

---

## 5. Workstream B: Auto Repeat

- **Database Migrations (`028_auto_repeat.sql`)**:
  - `frogframe_auto_repeats`: Document recurrence rule, frequency (`Daily`, `Weekly`, `Monthly`, `Quarterly`, `Yearly`), schedule anchors, `run_as_user_id`, and notification settings.
  - `frogframe_auto_repeat_runs`: Log of runs with `UNIQUE (auto_repeat_id, scheduled_for)` to eliminate duplicate documents.
- **Metadata Support**:
  - `DocTypeDef.AllowAutoRepeat`: Controls whether a DocType supports automatic recurrence.
  - `FieldDef.NoCopy`: Excludes sensitive/transient fields (posting dates, payment status, serial numbers) from being copied to recurring documents.
- **Execution & Catch-Up**:
  - `AutoRepeatJob` runs hourly via `ScheduledJobRunner`.
  - Copies source document data, strips system fields and child table IDs, and calls `ctx.Docs.InsertAsync(runAsUser, ...)`.
  - Catches up missed periods up to a maximum of 12 runs per cycle.

---

## 6. Workstream C: C# Server Scripting (Roslyn)

- **Dedicated Assemblies**:
  - `Framework.Scripting.Abstractions`: Minimal interfaces exposed to user scripts (`IScriptContext`, `IScriptDocs`, `IScriptEmail`). Contains zero internal dependencies.
  - `Framework.Scripting`: Compiler, AST analyzer, syntax rewriter, and isolated collectible runtime host.
- **Database Migrations (`029_server_scripts.sql`)**:
  - `frogframe_server_scripts`, `frogframe_server_script_versions`, and `frogframe_server_script_runs`.
- **Security Sandbox**:
  - Compilation-time AST / semantic validation rejects all file I/O, process spawning, reflection, threading, unsafe code, and dynamic types.
  - Syntax rewriter injects statement budget ticks (`__b.Tick()`) into every loop and lambda to strictly terminate infinite loops and excessive resource allocation.
  - Script assemblies are emitted to memory and loaded into collectible `AssemblyLoadContext` instances to ensure zero memory leaks upon script update.

---

## 7. Workstream D: Inbound Email & Communication

- **MailKit Migration**: Replaces obsolete `SmtpClient` with modern MailKit for reliable MIME handling.
- **Database Migrations (`030_communications.sql`)**:
  - `frogframe_email_accounts`: IMAP configuration, mailbox credentials, polling intervals, and target DocType mapping.
  - `frogframe_communications`: Inbound and outbound message records linked directly to parent documents (`reference_doctype`, `reference_name`).
- **Thread Matching & Inbound Processing**:
  - Matches replies via `In-Reply-To`, `References`, or `[#DOC-NAME]` subject tags.
  - Appends new records to target DocTypes (e.g. Support Tickets) under an authorized service user.
  - HTML content is sanitized on the server before storage to prevent XSS.

---

## 8. Definition of Done & Quality Gate

Phase 3 is complete when:
1. All unit and integration test suites pass in both In-Memory and PostgreSQL environments.
2. Webhook delivery includes verified HMAC signatures and blocks all SSRF attempts.
3. Auto Repeat generates recurrence documents with 0 duplicates during overlapping job triggers.
4. Server Script sandbox blocks 100% of illegal namespace access and enforces statement budgets.
5. All UI labels, code comments, and documentation strictly follow English standards.
6. Local Docker container is redeployed and verified via `./redeploy.ps1`.

