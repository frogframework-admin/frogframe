# C# Server Scripts (Roslyn Security Sandbox)

FrogFrame provides runtime extensibility through **Server Scripts** written in C# and dynamically compiled using Microsoft Roslyn (`Microsoft.CodeAnalysis.CSharp`). Similar to Frappe's Server Scripts, this feature allows administrators and developers to extend business logic, validate complex workflows, publish custom RPC endpoints, and run scheduled tasks without recompiling or redeploying the core application.

---

## 1. Script Types

| Type | Description | Trigger Mechanism |
|---|---|---|
| **DocEvent** | Attached to a DocType lifecycle hook stage. | Invoked within `HookRunner` during document mutations (`BeforeValidate`, `BeforeInsert`, `AfterSave`, `BeforeTransition`, `BeforeDelete`, `AfterDelete`, `OnCancel`, `OnAmend`). |
| **Api** | Custom RPC method callable by authenticated API clients or UI. | Invoked via `POST /api/method/{api_method}` and listed under `GET /api/method`. |
| **Scheduled** | Background job executed on a recurring cron interval. | Evaluated by `ServerScriptScheduledJob` via `ScheduledJobRunner` and `CronSchedule`. |

---

## 2. Sandbox Security Architecture

Server scripts run inside the application process. .NET has no in-process security boundary (no Code Access Security, no AppDomain isolation), so the layers below are defence in depth, not an absolute boundary. Only System Managers can author scripts, and scripts are **switched off by default outside Development**.

### Layer 0: Kill switch
`Scripting:Enabled` (appsettings or environment variable `Scripting__Enabled`) turns all scripts on or off. When the key is not set, scripts run only in the Development environment. While switched off, DocEvent scripts are skipped, Api scripts return 404, and scheduled scripts do not run. The value is read on every execution, so a configuration reload takes effect without a restart.

### Layer 1: Structural checks
Before anything is compiled, the user code must:
- contain no `using` directives and no preprocessor directives (`#line`, `#pragma`, `#define`, ...);
- contain no type or member declarations;
- form exactly one block, so it cannot close the generated method and declare its own members.

### Layer 2: Semantic allowlist (`ScriptSecurityAnalyzer`)
Every symbol and every expression type in the script is resolved through the Roslyn semantic model and must be on an explicit allowlist. Checking resolved symbols, not identifier text, closes the bypasses of a name-based denylist (unicode escapes such as `File`, verbatim identifiers such as `@File`, reflection through `typeof(...)` or `GetType()`).

Allowed:
- primitives, `string`, `decimal`, `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `Math`, `Convert`, `Random`, nullable types, tuples, anonymous types;
- `System.Collections.Generic` collections and interfaces, `System.Linq.Enumerable` (method syntax), `StringBuilder`;
- `Func<>`, `Action<>`, `Predicate<>`, `Comparison<>`, `Converter<>` delegates;
- common exception types (`Exception`, `ArgumentException`, `InvalidOperationException`, ...);
- `Task` / `ValueTask` for `await` only (`CompletedTask`, `FromResult`, `WhenAll`, `WhenAny`, `Delay`);
- `CancellationToken` (`IsCancellationRequested`, `ThrowIfCancellationRequested`), `CultureInfo` (`InvariantCulture`, `GetCultureInfo`), `NumberStyles`, `DateTimeStyles`;
- everything in `Framework.Scripting.Abstractions` except `ScriptBudget`.

Rejected (examples): `System.Type`, reflection, `GetType()`, `System.IO`, `System.Net`, `System.Diagnostics`, `Environment`, `Console`, `Activator`, threads, timers, `Task.Run`, `Task.Factory`, `Parallel`, `CancellationToken.Register`, `Regex` (ReDoS), `dynamic`, pointers, `unsafe`, `stackalloc`, attributes, local functions, `goto`, `lock`, LINQ query syntax, events, and identifiers starting with `__` (reserved for the wrapper).

To allow another BCL type, add its metadata name to `AllowedTypes` in `ScriptSecurityAnalyzer` and check that none of its members can reach the file system, network, reflection or background threads.

### Layer 3: Execution budget (`ScriptBudgetRewriter` + `ScriptBudget`)
`__budget.Tick();` is injected at the start of every loop body, lambda body and catch block. Each tick enforces:
- the statement limit (50,000 for DocEvent and Api, 500,000 for Scheduled);
- the wall-clock deadline (5 s DocEvent, 10 s Api, 60 s Scheduled);
- cancellation;
- the remaining stack, so unbounded recursion through a delegate stops with an exception instead of a process-killing `StackOverflowException`;
- an approximate allocation limit (256 MB).

Once tripped, every later tick throws again, so a `try/catch` in the script cannot swallow it. Code that never ticks (a single long-running BCL call) cannot be interrupted in-process; the caller stops waiting one second after the deadline. For full CPU and memory isolation, run Api and Scheduled scripts in a separate worker process (see the Phase 3 plan, step C10).

### Layer 4: Permissions
- **DocEvent** scripts run as the user who triggered the event. After each script the document is compared with its state before the script; changing a field the user may not write (field `PermLevel`, standard fields, unknown fields) fails with `PermissionDeniedException` and the changes are undone. This covers `Doc["x"] = ...`, `Doc.Data["x"] = ...` and edits to child-table rows.
- **Api** scripts can be called only by the roles in **Allowed Roles**. An empty list means System Managers / Administrators only.
- **Scheduled** scripts run as an explicit **Run As User**, which is required. There is no implicit Administrator.
- `Docs.*` calls always go through `DocumentService` as the script's user, so permissions, row scope, validation and hooks apply.

### Layer 5: Collectible memory
Script assemblies are loaded into an `AssemblyLoadContext` with `isCollectible: true`. When a script changes, the previous context is unloaded.

---

## 3. Script Globals & Execution Context

Inside a server script, user code operates against a narrow facade:

| Global Variable | Type | Description |
|---|---|---|
| `Doc` | `ScriptDoc` | Current document being inserted, updated, or validated (DocEvent). Allows indexer access `Doc["field"]` and helpers like `Doc.GetDecimal("total")`. |
| `OldDoc` | `ScriptDoc?` | Previous document state prior to the transaction (DocEvent). |
| `User` | `ScriptUser` | Calling user's identity (`Id`, `Email`, `Department`, `Roles`, `IsInRole(role)`). |
| `Docs` | `IScriptDocs` | Safe document CRUD operations (`GetAsync`, `InsertAsync`, `UpdateAsync`, `DeleteAsync`, `RunActionAsync`). Enforces tenant isolation and the caller's permissions. |
| `Log` | `IScriptLog` | `Log.Info(msg)`, `Log.Warn(msg)`, `Log.Error(msg)` for structured audit logging. |
| `Email` | `IScriptEmail` | `Email.SendAsync(to, subject, bodyText, bodyHtml)` for queuing outbound emails. |
| `Values` | `IDictionary<string, object?>` | Input arguments passed to an API method. |
| `Result` / `ctx.Result` | `object?` | Return payload sent back to the API caller. |

---

## 4. Code Examples

### Example 1: DocEvent (Auto-Calculating Totals & Approval)
```csharp
// Script Type: DocEvent
// DocType: PurchaseRequest
// Hook: BeforeValidate

var qty = Doc.GetDecimal("qty");
var rate = Doc.GetDecimal("rate");
var total = qty * rate;

Doc["total"] = total;

if (total > 50000000m && string.IsNullOrWhiteSpace(Doc.GetString("justification")))
{
    // ScriptValidationException is turned into a normal field validation error.
    throw new ScriptValidationException("Justification is required for purchases over Rp 50,000,000.", "justification");
}

Log.Info($"Validated PR total: {total:C}");
```

### Example 2: API Method (Custom Discount RPC)
```csharp
// Script Type: Api
// API Method Name: calculate_bulk_discount
// Endpoint: POST /api/method/calculate_bulk_discount

var subtotal = Values.ContainsKey("subtotal") ? Convert.ToDecimal(Values["subtotal"]) : 0m;
var tier = Values.ContainsKey("tier") ? Values["tier"]?.ToString() : "Standard";

decimal discountPct = tier switch
{
    "Platinum" => 0.15m,
    "Gold" => 0.10m,
    "Silver" => 0.05m,
    _ => 0.0m
};

var discountAmount = subtotal * discountPct;
ctx.Result = new Dictionary<string, object>
{
    ["subtotal"] = subtotal,
    ["discount_percent"] = discountPct * 100,
    ["discount_amount"] = discountAmount,
    ["net_total"] = subtotal - discountAmount
};
```

---

## 5. Administration UI (`/admin/server-scripts`)

System Managers can configure scripts under **Settings &rarr; System &rarr; Server Scripts**:
- **Code Editor**: Clean monospace code input with script type switching.
- **Validate / Compile**: Tests compilation and security rules immediately, returning line-numbered diagnostics without saving.
- **Save & Publish**: Bumps the version number, creates a historical version snapshot, and invalidates in-memory caches.
- **Execution Run History**: Displays execution duration in milliseconds, status (`Success`, `Failed`, `BudgetExceeded`, `Timeout`, `SecurityViolation`), and full error traces.
- **Allowed Roles** (Api scripts) and **Run As User** (Scheduled scripts, required) are set in the editor.
- A banner shows when scripts are switched off by `Scripting:Enabled`.

> **Upgrading from the first sandbox version:** scripts that use `Regex`, `System.Threading`, framework types such as `Framework.Documents.ValidationFailedException`, local functions or LINQ query syntax are now rejected. Open each script and press **Validate** before enabling scripts in production.
