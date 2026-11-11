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

Server Scripts execute untrusted tenant code in a sandboxed environment with multi-layered security gates:

### Layer 1: Restricted Assembly References
The Roslyn compiler is initialized with a minimal allowlist of core assemblies:
- `System.Private.CoreLib` (primitive types, strings, math)
- `System.Collections` / `System.Linq`
- `System.Text.RegularExpressions`
- `System.Threading.Tasks`
- `Framework.Scripting.Abstractions` (minimal script facade)

Assemblies providing system I/O, process management, low-level networking, and dynamic reflection are omitted. Any attempt to use them fails resolution at compile time.

### Layer 2: AST Security Analyzer
Before compilation, `ScriptSecurityAnalyzer` walks the syntax tree (`CSharpSyntaxWalker`) and strictly rejects:
- **Forbidden Namespaces**: `System.IO`, `System.Diagnostics`, `System.Reflection`, `System.Threading`, `System.Net`, `System.Runtime.InteropServices`, `Microsoft.Win32`, etc.
- **Dangerous Types**: `Process`, `File`, `Directory`, `Path`, `Environment`, `AppDomain`, `Assembly`, `Type`, `Activator`, `Thread`, `Task.Run`.
- **Unsafe Language Constructs**: `unsafe` blocks, `fixed` pointers, `stackalloc`, pointer dereferencing (`*`, `->`).
- **Dynamic Typing**: The `dynamic` keyword is forbidden to prevent runtime reflection bypasses.

### Layer 3: Statement Budget Rewriter
To prevent CPU exhaustion and infinite loops (`while (true) {}`), `ScriptBudgetRewriter` rewrites loop bodies and blocks by injecting:
```csharp
__budget.Tick();
```
If statement execution exceeds `MaxStatements` (default: 50,000 statements), a `ScriptBudgetExceededException` is thrown immediately, terminating execution.

### Layer 4: Execution Timeout & Collectible Memory
- **Timeout**: Every script execution is bound to a linked `CancellationToken` with a strict **5-second timeout**.
- **Collectible ALC**: Script assemblies are compiled into an isolated `AssemblyLoadContext` configured with `isCollectible: true`. When a script is modified, saved, or deleted, the previous ALC is immediately unloaded, preventing memory leaks in long-running processes.

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
    throw new Framework.Documents.ValidationFailedException([
        new Framework.Documents.FieldError("justification", "Justification is required for purchases over Rp 50,000,000.")
    ]);
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
- **Execution Run History**: Displays execution duration in milliseconds, status (`Success`, `BudgetExceeded`, `Timeout`, `SecurityViolation`), and full error traces.
