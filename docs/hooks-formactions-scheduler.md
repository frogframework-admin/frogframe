# Architecture Guide: Document Hooks, Form Actions & Background Scheduler

> **Status**: Implemented & Verified (79 tests passing)  
> **Target Framework**: .NET 10, ASP.NET Core, Blazor Server  
> **Design Goal**: Frappe-Parity with Enterprise .NET Security & Concurrency Standards  

This document details three foundational extensibility capabilities in **DocFrame**:
1. **Document Lifecycle Hooks Expansion & Safe Helper APIs**: Extended document lifecycle stages (`BeforeDelete`, `AfterDelete`, `OnCancel`, `OnAmend`) with strongly-typed context helpers.
2. **Custom Form Actions (`IFormAction<TDoc>`)**: Server-side executed toolbar action buttons with granular permission verification.
3. **Background Scheduler (`AddScheduledJob<T>`)**: Distributed cron execution engine utilizing PostgreSQL advisory locks running under system identity (`UserContext.System`).

---

## 1. Security & Architectural Principles (Safe Frappe Parity)

While Frappe Framework offers flexible metadata hooks, certain design patterns introduce concurrency and security risks in multi-tenant environments. DocFrame resolves these challenges:
- **No Client JavaScript Metadata Execution**: DocFrame never evaluates untrusted JavaScript strings stored in the database on client browsers (mitigating Stored XSS). All custom logic runs in compiled C# or via sandboxed AST interpreters.
- **No Arbitrary Raw SQL (`frappe.db.sql`)**: Hooks and actions cannot execute unbounded raw SQL strings that bypass tenant isolation or introduce table locks. Data operations occur through tenant-scoped `DocumentService` or read-only `IReportingQueryExecutor`.
- **No Fragile Monkey-Patching**: Rather than dynamically overriding class implementations at runtime, extensions register cleanly via .NET Dependency Injection (DI).
- **Layered Access Control**: Form action buttons evaluate the authenticated caller's `UserContext`, while scheduled jobs execute under `UserContext.System` with full audit trail logging.

---

## 2. Document Lifecycle Hooks

### 2.1 Lifecycle Stages (`DocHookStage`)

```csharp
public enum DocHookStage
{
    BeforeValidate,
    BeforeSave,
    AfterSave,
    BeforeSubmit,
    OnSubmit,
    BeforeCancel,
    OnCancel,
    BeforeDelete,
    AfterDelete,
    OnAmend
}
```

### 2.2 Implementing a Document Hook

Hooks implement `IDocumentHook` and register into the DI container:

```csharp
public class PurchaseOrderValidationHook : IDocumentHook
{
    public string DocType => "Purchase Order";

    public async Task ExecuteAsync(DocumentHookContext ctx, CancellationToken ct)
    {
        if (ctx.Stage == DocHookStage.BeforeSubmit)
        {
            var grandTotal = ctx.Document.GetDecimal("grand_total");
            if (grandTotal <= 0)
            {
                ctx.Fail("Grand total must be greater than zero before submitting.");
            }
        }
    }
}
```

---

## 3. Custom Form Actions

Custom actions appear as action buttons on the document form view in the Blazor UI:

```csharp
public class SendForApprovalAction : IFormAction
{
    public string DocType => "Purchase Order";
    public string Label => "Send for Approval";
    public string RequiredRole => "Purchaser";

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        // Business logic executed on server
        return FormActionResult.Success("Document forwarded for approval.");
    }
}
```

---

## 4. Background Scheduler

Scheduled jobs execute periodically across cluster nodes without duplicates:

```csharp
public class DailySyncJob : IScheduledJob
{
    public string Name => "daily_sap_sync";
    public string CronExpression => "0 2 * * *"; // Every day at 2:00 AM

    public async Task ExecuteAsync(JobExecutionContext ctx, CancellationToken ct)
    {
        // Executes with UserContext.System
        ctx.Logger.LogInformation("Starting SAP data synchronization...");
    }
}
```
Register the job in `Program.cs`:
```csharp
builder.Services.AddScheduledJob<DailySyncJob>();
```
