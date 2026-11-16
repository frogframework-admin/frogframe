# AI Assistant & Knowledge Base (End-User ERP Q&A Copilot)

While the [AI Designer](ai-designer.md) assists System Managers in drafting DocTypes, workflows, and SQL queries, the **AI Assistant** is designed for **all authenticated ERP users** (`Finance`, `Purchasing`, `Stock`, `HR`, `Payroll`, `Sales`, `Manager`, `Employee`, and `System Manager`) to answer operational questions, explain business rules, locate menus/reports, and diagnose document or report states directly inside the application.

---

## 1. Core User Question Patterns

Analysis of real ERP user interactions shows four primary categories of questions:

| Category | Example User Question | Underlying Source Required at Runtime |
|---|---|---|
| **1. Navigation & Discovery** | *"Where is the organization structure page?"*<br>*"Where do I create Word/Excel report templates?"* | `INavigationMenuStore`, `IEnumerable<DeskMenuItem>`, `ReportService`, and `MetadataRegistry` routes filtered by the user's roles. |
| **2. SOP & How-To Guides** | *"How do I create a custom report?"*<br>*"What is the sequence for testing initial stock and invoices?"* | Bundled Markdown guides (`docs/ERP_README.md`, `docs/ERP_TEST_SCENARIOS.md`, `docs/guides/*.md`). |
| **3. Business Rules & Semantics** | *"Is Accounting Period generated automatically?"*<br>*"Why does `EMP-0001` show a `Draft` badge?"* | `MetadataRegistry` (`allowSubmit: false` master data vs submittable transactions, field `compute` formulas, `rules`, workflows) + compiled C# XML doc comments (`Apps.Erp.xml`). |
| **4. Live Data & Diagnostics** | *"Why is my Trial Balance report showing all zeros?"*<br>*"What is the status of `JV-2026-000001`?"* | Read-only `DocumentService` queries (`docstatus = 0` Draft vs `1` Submitted, available workflow transitions, and custom `IFormAction` buttons) scoped to the user's tenant and permissions. |

---

## 2. How Knowledge Survives Compilation (`dotnet publish` / Docker)

In a production or Docker deployment (`FROM mcr.microsoft.com/dotnet/aspnet:10.0`), raw C# source files (`*.cs`) and the repository root `docs/` folder are not present in the container image. To ensure the AI Assistant has complete knowledge after compilation, FrogFrame packages five runtime knowledge sources:

```mermaid
flowchart TB
    subgraph BuildTime["Build & Publish Time (dotnet publish)"]
        MD["docs/*.md & docs/guides/*.md"] -->|"Content CopyToPublishDirectory"| KB["/app/KnowledgeBase/**/*.md"]
        CS["src/Apps.Erp/**/*.cs\n(/// <summary> comments)"] -->|"GenerateDocumentationFile=true"| XML["/app/Apps.Erp.xml"]
        JSON["src/Apps.Erp/Metadata/*.json"] -->|"Included in ContentRoot"| META["/app/Metadata/*.json"]
    end

    subgraph Runtime["Runtime Memory & Services (Container /app)"]
        KB --> Index["AiKnowledgeIndex\n(In-Memory Section & XML Index)"]
        XML --> Index
        META --> Reg["MetadataRegistry\n(DocTypes, Fields, Rules, Workflows)"]
        Nav["INavigationMenuStore + DeskMenuItem + ReportService"]
        Doc["DocumentService + IPermissionService + IFormAction"]
    end

    Index --> Tools["AiAssistantTools (Read-Only, Tenant & Role Scoped)"]
    Reg --> Tools
    Nav --> Tools
    Doc --> Tools
```

1. **Bundled Markdown Documentation (`KnowledgeBase/`)**:
   - `Apps.Erp.csproj` copies `docs/*.md` and `docs/guides/*.md` into `KnowledgeBase/` inside both build and publish outputs (`CopyToOutputDirectory` and `CopyToPublishDirectory`).
2. **Compiled C# XML Documentation (`Apps.Erp.xml`)**:
   - `Apps.Erp.csproj` enables `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. Every `/// <summary>` docstring on ERP hooks (e.g., `AccountingPeriodRulesHook`, `PayrollHooks`, `StockHooks`), form actions, and reports is compiled into `Apps.Erp.xml` alongside `Apps.Erp.dll`.
3. **In-Memory Metadata (`MetadataRegistry`)**:
   - All 146 DocTypes and 8 Workflows are compiled into `MetadataRegistry.Current` at startup, exposing field definitions, `allowSubmit` flags, `requiredWhen` expressions, `compute` formulas, validation rules, and workflow transitions.
4. **Live Navigation & Report Catalogs**:
   - `INavigationMenuStore`, `DeskMenuItem`, and `ReportService` hold every route and report available in the application.
5. **Permission-Checked Live Documents (`DocumentService`)**:
   - Live document state (`docstatus`, `workflow_state`, available transitions, and record counts) is queried through `DocumentService` under the calling user's `UserContext`.

---

## 3. Phased Implementation Roadmap

### Phase 1: Runtime Knowledge Packaging & In-Memory Indexing (`AiKnowledgeIndex`) — *Completed*
- [x] Configure `src/Apps.Erp/Apps.Erp.csproj` to emit `Apps.Erp.xml` (`GenerateDocumentationFile`) and bundle `docs/*.md` + `docs/guides/*.md` into `KnowledgeBase/`.
- [x] Implement `AiKnowledgeIndex` (`src/Framework.Web/AI/AiKnowledgeIndex.cs`):
  - Splits Markdown files by heading (`#`, `##`, `###`) into searchable chunks.
  - Parses compiled XML documentation files (`Apps.Erp.xml`, `Framework.*.xml`) to index hook, action, and report business rule summaries.
  - Provides fast, zero-dependency in-memory BM25/keyword scoring (`Search(query, maxResults)`).
- [x] Register `AiKnowledgeIndex` as a singleton in `Hosting.AddFrogFramework`.

### Phase 2: Read-Only Tenant-Scoped User Tools (`AiAssistantTools`) — *Completed*
- [x] Implement `AiAssistantTools` (`src/Framework.Web/AI/AiAssistantTools.cs`) bound to the calling user's `UserContext`:
  - `search_knowledge(query)`: Searches bundled Markdown guides, test scenarios, and C# XML hook documentation.
  - `explain_doctype(docType)`: Explains a DocType's purpose, lifecycle (`allowSubmit: false` Master Data vs submittable Transaction where Draft `docstatus = 0` means unposted), workflow states, custom form actions, rules, and readable fields.
  - `find_navigation(query)`: Finds accessible menus, desk pages (e.g., `/org-tree`, `/admin/report-templates`), DocType lists, and reports (`/reports/...`) visible to the user.
  - `inspect_document(docType, name)`: Reads a specific document via `DocumentService.GetAsync` (respecting tenant, role, row scope, and field `permLevel`), returning its status, next available workflow actions, and custom buttons.
  - `diagnose_doctype_records(docType, search)`: Summarizes record counts by `docstatus` (`Draft`, `Submitted`, `Cancelled`) and lists recent documents to answer diagnostic questions such as *"Why is Trial Balance zero?"* (because `JournalEntry` records are still in `Draft`).

### Phase 3: Context-Aware Conversational Service (`AiAssistantService`) — *Completed*
- [x] Create `AiAssistantService` on top of `IAiChatClientProvider` and `AiAssistantTools`.
- [x] Automatically attach **Active Page Context** (`currentUrl`, detected `docType` and `documentName` or `reportName`, authenticated roles, and department) so questions like *"Why is this draft?"* or *"Why is this report empty?"* work without typing document IDs. Query strings are excluded from context.
- [x] Enforce a shared per-user hourly AI request limit and a configurable conversation turn budget (`AI:MaxConversationTurns`, default `12`).

### Phase 4: Blazor Copilot Drawer UI (`AiAssistantDrawer.razor`) — *Completed*
- [x] Add an **"Ask AI"** button in the `DeskLayout` top bar and Awesomebar (`Ctrl+K`).
- [x] Build a non-blocking slide-over chat drawer using Blazor Blueprint's chat message and bubble components. The layout-level drawer preserves the conversation across page navigation and refreshes page context with each question.
- [x] Render Markdown answers as sanitized HTML so model-generated links remain clickable without allowing unsafe markup.

---

## 4. Security & Guardrails

1. **Strictly Read-Only**: `AiAssistantTools` never inserts, updates, submits, cancels, or deletes documents. The assistant guides the user and provides direct links, while the user performs actions in the UI.
2. **Tenant Isolation**: Every tool call executes within `user.TenantId`. Cross-tenant data is unreachable.
3. **RBAC, Row Scope & Field `permLevel`**:
   - DocType explanations check `IPermissionService.Can(user, meta, DocActions.Read)` and filter out fields where `!CanReadField(user, meta, field)` (protecting `permLevel >= 1` sensitive HR/Payroll fields).
   - Document inspections and list diagnostics go exclusively through `DocumentService.GetAsync` and `DocumentService.ListAsync`, which enforce both role permissions and row-level scopes (`match:employee=employee`, `match:sales_rep=employee`, etc.).
