using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Framework.Actions;
using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Navigation;
using Framework.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Web.AI;

/// <summary>
/// Read-only assistant tools bound to a specific authenticated <see cref="UserContext"/>.
/// <para>
/// Unlike <see cref="AiDesignerTools"/> (which is restricted to System Managers for authoring metadata),
/// <see cref="AiAssistantTools"/> can be used by any authenticated user to search bundled ERP documentation,
/// understand DocType lifecycle and business rules, locate menus/reports, and inspect or diagnose document states.
/// </para>
/// <para>
/// Every tool strictly enforces tenant isolation (<c>user.TenantId</c>), role-based permissions (<see cref="IPermissionService"/>),
/// row-level scopes, and field-level <c>permLevel</c> guards via <see cref="DocumentService"/>.
/// No tool ever creates, modifies, submits, cancels, or deletes data.
/// </para>
/// </summary>
public sealed class AiAssistantTools(
    UserContext user,
    AiKnowledgeIndex knowledge,
    MetadataRegistry registry,
    IPermissionService permissions,
    DocumentService documents,
    INavigationMenuStore? menuStore = null,
    IEnumerable<DeskMenuItem>? deskItems = null,
    ReportService? reports = null,
    IEnumerable<IFormAction>? formActions = null,
    AiErrorCatalog? errorCatalog = null)
{
    private static readonly JsonSerializerOptions Json = new(MetadataJson.Options)
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Creates an <see cref="AiAssistantTools"/> instance for the specified <paramref name="user"/> using services from <paramref name="sp"/>.
    /// </summary>
    public static AiAssistantTools ForUser(IServiceProvider sp, UserContext user) => new(
        user,
        sp.GetRequiredService<AiKnowledgeIndex>(),
        sp.GetRequiredService<MetadataRegistry>(),
        sp.GetRequiredService<IPermissionService>(),
        sp.GetRequiredService<DocumentService>(),
        sp.GetService<INavigationMenuStore>(),
        sp.GetService<IEnumerable<DeskMenuItem>>(),
        sp.GetService<ReportService>(),
        sp.GetService<IEnumerable<IFormAction>>(),
        sp.GetService<AiErrorCatalog>());

    /// <summary>
    /// Returns the list of <see cref="AITool"/> functions that an <see cref="IChatClient"/> agent may invoke.
    /// </summary>
    public IList<AITool> AllTools() =>
    [
        AIFunctionFactory.Create(SearchKnowledge, "search_knowledge",
            "Searches bundled ERP guides, test scenarios, and compiled C# hook/report summaries for business rules, how-to steps, and domain concepts."),
        AIFunctionFactory.Create(ExplainDocType, "explain_doctype",
            "Explains a DocType's purpose, lifecycle (Master Data vs Submittable Transaction, and what Draft/Submitted means), workflow states, custom form buttons, validation rules, and readable fields."),
        AIFunctionFactory.Create(FindNavigationAsync, "find_navigation",
            "Finds accessible menus, pages (such as Organization Tree or Report Templates), DocType lists, and analytical reports available to the current user."),
        AIFunctionFactory.Create(InspectDocumentAsync, "inspect_document",
            "Reads a specific document by DocType and name (enforcing the user's tenant, role, row scope, and field permissions), returning its status, why it is Draft/Submitted, available next actions, and field values."),
        AIFunctionFactory.Create(DiagnoseDocTypeRecordsAsync, "diagnose_doctype_records",
            "Summarizes visible records of a DocType by status (Draft vs Submitted vs Cancelled) and lists recent records to diagnose why a report is empty or which documents are pending submission."),
        AIFunctionFactory.Create(ExplainError, "explain_error",
            "Finds the exact business rule in the application code behind an error, warning, or failed-button message the user saw: the DocType, when it runs (save/submit/cancel/approve/button), the field, and the code condition that triggers it. Call this whenever the user pastes or describes a rejection message."),
    ];

    // ================================================================== 0. Error message → code rule

    public string ExplainError(
        [Description("The message exactly as the user saw it, e.g. 'Only 1 of ITM-LAP-001 available in Gudang Utama Cikarang' or 'Period 2026-01 is closed'")] string message,
        [Description("Optional DocType the user was working on, e.g. 'SalesInvoice'")] string? docType = null)
    {
        if (errorCatalog is null || errorCatalog.Count == 0)
            return Serialize(new
            {
                Found = false,
                Hint = "The rule catalog is not installed (docs/rules/error-catalog.json). Use search_knowledge with the message text instead."
            });

        var matches = errorCatalog.Match(message, docType, maxResults: 3);
        if (matches.Count == 0)
            return Serialize(new
            {
                Found = false,
                Message = message,
                Hint = "No rule in the catalog matches this text. It may come from the framework (required field, permission, workflow) " +
                       "rather than an application hook: use explain_doctype for required fields and workflow conditions, or search_knowledge."
            });

        return Serialize(new
        {
            Found = true,
            Message = message,
            Matches = matches.Select(m => new
            {
                m.MatchType,
                m.Score,
                m.Entry.Kind,
                Meaning = m.Entry.Kind switch
                {
                    "Warning" => "Warning only: the document was still saved/submitted.",
                    "ActionFailure" => "A form button (custom action) could not finish; nothing was changed by that button.",
                    _ => "Validation error: the document was not saved or the step was rejected."
                },
                RuleMessage = m.Entry.Message,
                m.Entry.Field,
                TriggeredWhen = m.Entry.Condition,
                m.Entry.DocTypes,
                When = m.Entry.Event,
                SharedRule = m.Entry.Shared,
                Source = $"{m.Entry.ClassName} ({Path.GetFileName(m.Entry.File)}:{m.Entry.Line})",
                RelatedGuide = m.Entry.Shared
                    ? $"rules/_shared-{m.Entry.ClassName}.md"
                    : m.Entry.DocTypes.Count > 0 ? $"rules/{m.Entry.DocTypes[0]}.md" : null
            }),
            Hint = "TriggeredWhen is C# code: translate it into plain business language for the user and tell them what to change. " +
                   "Use inspect_document on the user's document to confirm which value causes it."
        });
    }

    // ================================================================== 1. Knowledge Base Search

    public string SearchKnowledge(
        [Description("Search keywords or question, e.g. 'AccountingPeriod automatic', 'Trial Balance', 'custom report'")] string query,
        [Description("Maximum number of sections to return (1-8)")] int maxResults = 5)
    {
        var hits = knowledge.Search(query, Math.Clamp(maxResults, 1, 8));
        return Serialize(new
        {
            Query = query,
            Count = hits.Count,
            Results = hits.Select(h => new
            {
                h.Source,
                h.Section,
                h.Kind,
                h.Score,
                h.Content
            })
        });
    }

    // ================================================================== 2. DocType & Lifecycle Explanation

    public string ExplainDocType(
        [Description("DocType name or title, e.g. 'Employee', 'JournalEntry', 'AccountingPeriod'")] string docType)
    {
        var q = docType?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(q))
            return Serialize(new { Error = "DocType name is required." });

        var meta = ResolveDocType(q);
        if (meta is null)
            return Serialize(new { Error = $"DocType '{q}' was not found." });

        bool canRead = meta.Def.IsChild || permissions.Can(user, meta, DocActions.Read);
        var requiredRoles = meta.Def.Permissions
            .Where(p => p.PermLevel == 0 && p.Actions.Contains(DocActions.Read, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Role)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r)
            .ToList();

        if (!canRead)
        {
            return Serialize(new
            {
                meta.Name,
                meta.Title,
                Module = meta.Def.Module,
                CanRead = false,
                RequiredRoles = requiredRoles,
                Message = $"Current user '{user.Id}' does not have Read permission on '{meta.Name}'. Required roles: {string.Join(", ", requiredRoles)}."
            });
        }

        string statusSemantics = meta.Def.IsSingle
            ? "Singleton configuration DocType (one record per tenant). Always editable by authorized roles without submission."
            : !meta.Def.Document.AllowSubmit && meta.Workflow is null
                ? "Master Data / non-submittable DocType (allowSubmit = false). Records always remain at docstatus = 0 (displayed with a 'Draft' / 'Draf' badge in the UI). This means the record is already active and valid across the system, and remains open for authorized users to edit and save changes at any time without needing a Submit step."
                : "Submittable Transaction / Workflow DocType. Records start in Draft (docstatus = 0), where they can be edited and do NOT post to the General Ledger or Stock. Only when Submitted (docstatus = 1) does the document lock and write ledger/stock entries. Submitted documents can only be reversed via Cancel (docstatus = 2) and re-created via Amend.";

        string[] allActions =
        [
            DocActions.Read, DocActions.Create, DocActions.Write,
            DocActions.Submit, DocActions.Cancel, DocActions.Amend,
            DocActions.Delete, DocActions.Report, DocActions.Export
        ];
        var allowedForUser = allActions.Where(a => permissions.Can(user, meta, a)).ToList();

        var customButtons = (formActions ?? [])
            .Where(a => string.Equals(a.DocType, meta.Name, StringComparison.OrdinalIgnoreCase))
            .Select(a => new { a.ActionId, a.Label })
            .ToList();

        var readableFields = meta.Fields
            .Where(f => !f.IsLayout && permissions.CanReadField(user, meta, f))
            .Select(f => new
            {
                f.Name,
                f.Label,
                Type = f.Def.Type,
                RequiredWhen = f.Def.RequiredWhen,
                ReadOnlyWhen = f.Def.ReadOnlyWhen,
                LinkTo = f.Def.LinkTo,
                ChildDocType = f.Def.ChildDocType,
                Options = f.Def.Options is { Count: > 0 } ? f.Def.Options : null,
                Default = f.Def.Default,
                FetchFrom = f.Def.FetchFrom,
                Compute = f.Def.Compute,
                Description = f.Def.Description
            })
            .ToList();

        var workflowInfo = meta.Workflow is null ? null : new
        {
            meta.Workflow.Def.Name,
            meta.Workflow.Def.InitialState,
            States = meta.Workflow.Def.States.Select(s => new { s.Name, s.DocStatus }),
            Transitions = meta.Workflow.Def.Transitions.Select(t => new
            {
                t.From,
                t.Action,
                t.To,
                t.AllowedRoles,
                t.Condition
            })
        };

        return Serialize(new
        {
            meta.Name,
            meta.Title,
            Module = meta.Def.Module,
            Description = meta.Def.Description,
            Url = meta.Def.IsChild ? null : $"/app/{meta.Name}",
            CanRead = true,
            IsSingle = meta.Def.IsSingle,
            IsChild = meta.Def.IsChild,
            AllowSubmit = meta.Def.Document.AllowSubmit,
            AllowCancel = meta.Def.Document.AllowCancel,
            AllowAmend = meta.Def.Document.AllowAmend,
            AllowDelete = meta.Def.Document.AllowDelete,
            StatusSemantics = statusSemantics,
            UserAllowedActions = allowedForUser,
            RequiredRolesForRead = requiredRoles,
            Workflow = workflowInfo,
            CustomFormButtons = customButtons,
            Rules = meta.Def.Rules.Select(r => new { r.Name, r.Expr, r.Message }),
            Fields = readableFields
        });
    }

    // ================================================================== 3. Navigation, Pages & Reports Discovery

    public async Task<string> FindNavigationAsync(
        [Description("Keyword for menu, page, DocType, or report (e.g. 'organization', 'trial balance', 'template', 'invoice')")] string? query = null,
        CancellationToken ct = default)
    {
        var q = query?.Trim() ?? string.Empty;
        var items = new List<NavigationEntry>();

        // 1. Built-in core pages
        items.Add(new("Page", "Home", "Main workspace overview", "/"));
        items.Add(new("Page", "Inbox", "Workflow tasks & notifications", "/inbox"));
        items.Add(new("Page", "Dashboards", "Analytical SQL dashboards & KPI widgets", "/dashboards"));
        items.Add(new("Page", "Reports", "Tabular operational & financial reports (CSV export)", "/reports"));

        if (registry.Current.TryGet("OrganizationUnit", out var orgMeta) && permissions.Can(user, orgMeta, DocActions.Read))
        {
            items.Add(new("Page", "Organization Structure (Org Tree)",
                "Interactive hierarchical supervisory organization & position chart", "/org-tree"));
        }

        if (user.IsAdmin)
        {
            items.Add(new("Admin", "Report Templates (Word / Excel)",
                "Design and generate formatted .docx and .xlsx document/report templates", "/admin/report-templates"));
            items.Add(new("Admin", "Metadata & DocTypes",
                "View and customize DocType schemas and fields", "/admin/metadata"));
            items.Add(new("Admin", "Workflows",
                "Configure approval workflows and state transitions", "/admin/workflows"));
            items.Add(new("Admin", "Dashboard Builder",
                "Create SQL dashboard widgets and layouts", "/admin/dashboards"));
            items.Add(new("Admin", "Saved Query APIs",
                "Publish parameterized read-only SQL endpoints", "/admin/query-apis"));
            items.Add(new("Admin", "Users & Accounts",
                "Manage user accounts, roles, and employee/department attributes", "/admin/users"));
            items.Add(new("Admin", "Roles & Modules",
                "Manage role catalog and navigation modules", "/admin/roles"));
            items.Add(new("Admin", "Navigation Menus",
                "Customize sidebar navigation menus and groups", "/admin/navigation"));
            items.Add(new("Admin", "Excel Data Import",
                "Bulk import documents from Excel (.xlsx) spreadsheets", "/admin/imports"));
        }

        // 2. Custom / seeded navigation menus
        if (menuStore is not null)
        {
            var navMenus = await menuStore.ListAsync(user.TenantId, onlyActive: true, ct);
            foreach (var m in navMenus.Where(m => m.IsVisibleFor(user) && !string.IsNullOrWhiteSpace(m.Href)))
            {
                var href = "/" + m.Href!.TrimStart('/');
                if (!items.Any(i => string.Equals(i.Url, href, StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(new("Menu", m.Title, $"Menu · {m.GroupName ?? "General"}", href));
                }
            }
        }

        // 3. Registered DeskMenuItems
        if (deskItems is not null)
        {
            foreach (var d in deskItems)
            {
                if (d.RequiresReadOf is not null &&
                    (!registry.Current.TryGet(d.RequiresReadOf, out var reqMeta) || !permissions.Can(user, reqMeta, DocActions.Read)))
                {
                    continue;
                }

                var href = "/" + d.Href.TrimStart('/');
                if (!items.Any(i => string.Equals(i.Url, href, StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(new("Desk", d.Title, $"Desk · {d.Group}", href));
                }
            }
        }

        // 4. Accessible Reports
        if (reports is not null)
        {
            foreach (var rep in reports.AvailableFor(user))
            {
                var url = $"/reports/{Uri.EscapeDataString(rep.Name)}";
                items.Add(new("Report", rep.Title, $"{rep.Description} (DocType: {rep.DocType}, ID: {rep.Name})", url));
            }
        }

        // 5. Accessible DocTypes
        foreach (var dt in registry.Current.DocTypes.Values
            .Where(d => !d.Def.IsChild && permissions.Can(user, d, DocActions.Read))
            .OrderBy(d => d.Title))
        {
            var kind = dt.Def.IsSingle ? "SingleDocType" : "DocType";
            var sub = $"{dt.Def.Module ?? "General"} · {(dt.Def.IsSingle ? "Singleton configuration form" : "Document list & form")} ({dt.Name})";
            items.Add(new(kind, dt.Title, sub, $"/app/{dt.Name}"));
        }

        IEnumerable<NavigationEntry> filtered = items;
        if (!string.IsNullOrEmpty(q))
        {
            var tokens = AiKnowledgeIndex.Tokenize(q).ToList();
            filtered = items
                .Select(item =>
                {
                    int score = 0;
                    if (item.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        item.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
                        score += 20;
                    if (item.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                        score += 10;

                    foreach (var t in tokens)
                    {
                        if (item.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 6;
                        if (item.Url.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 5;
                        if (item.Subtitle.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 3;
                    }
                    return (Item: item, Score: score);
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Item);
        }

        var resultList = filtered.Take(20).ToList();
        return Serialize(new
        {
            Query = q,
            Count = resultList.Count,
            Items = resultList
        });
    }

    private sealed record NavigationEntry(string Category, string Title, string Subtitle, string Url);

    // ================================================================== 4. Live Document Inspection

    public async Task<string> InspectDocumentAsync(
        [Description("DocType name, e.g. 'Employee' or 'JournalEntry'")] string docType,
        [Description("Document name/ID, e.g. 'EMP-0001' or 'JV-2026-000001'")] string name,
        CancellationToken ct = default)
    {
        var meta = ResolveDocType(docType);
        if (meta is null || meta.Def.IsChild)
            return Serialize(new { Error = $"Top-level DocType '{docType}' was not found." });

        try
        {
            var doc = await documents.GetAsync(user, meta.Name, name, ct);
            var availableActions = await documents.AvailableActionsAsync(user, doc, ct);
            var visibleCustomActions = (formActions ?? [])
                .Where(a => string.Equals(a.DocType, meta.Name, StringComparison.Ordinal) && a.IsVisible(user, doc))
                .Select(a => new { a.ActionId, a.Label })
                .ToList();

            string statusLabel = doc.DocStatus switch
            {
                DocStatus.Submitted => "Submitted",
                DocStatus.Cancelled => "Cancelled",
                _ => "Draft"
            };

            string statusExplanation = meta.Def.IsSingle
                ? "Singleton configuration document. Changes take effect immediately upon saving."
                : !meta.Def.Document.AllowSubmit && meta.Workflow is null
                    ? $"Master Data record ('{meta.Name}' has allowSubmit = false). The 'Draft' badge (docstatus = 0) is normal for all master records and means the record is active, valid, and open for editing by authorized users."
                    : doc.DocStatus == DocStatus.Draft
                        ? $"Transaction document is currently in Draft (docstatus = 0{(doc.WorkflowState is not null ? $", workflow_state = '{doc.WorkflowState}'" : "")}). It has NOT posted to the General Ledger or Stock yet. It must be Submitted (docstatus = 1) to take financial/inventory effect."
                        : doc.DocStatus == DocStatus.Submitted
                            ? "Transaction document is Submitted (docstatus = 1). Its fields are locked and its General Ledger / Stock postings are active."
                            : "Transaction document is Cancelled (docstatus = 2). Its postings have been reversed.";

            var summarizedData = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, val) in doc.Data)
            {
                if (meta.ByName.TryGetValue(key, out var field) && !permissions.CanReadField(user, meta, field))
                    continue;

                if (val is System.Collections.IList list && val is not string)
                {
                    summarizedData[key] = new
                    {
                        RowCount = list.Count,
                        SampleRows = list.Cast<object?>().Take(5).ToList()
                    };
                }
                else
                {
                    summarizedData[key] = val;
                }
            }

            return Serialize(new
            {
                DocType = meta.Name,
                DocTypeTitle = meta.Title,
                doc.Name,
                Url = meta.Def.IsSingle ? $"/app/{meta.Name}" : $"/app/{meta.Name}/{Uri.EscapeDataString(doc.Name)}",
                DocStatus = (int)doc.DocStatus,
                DocStatusLabel = statusLabel,
                doc.WorkflowState,
                StatusExplanation = statusExplanation,
                doc.Owner,
                doc.Modified,
                doc.ModifiedBy,
                doc.Version,
                doc.AmendedFrom,
                AvailableWorkflowActions = availableActions.Select(a => new { a.Action, a.ToState, a.RequireComment }),
                AvailableCustomButtons = visibleCustomActions,
                Data = summarizedData
            });
        }
        catch (PermissionDeniedException ex)
        {
            return Serialize(new
            {
                DocType = meta.Name,
                Name = name,
                CanRead = false,
                Error = ex.Message
            });
        }
        catch (DocumentNotFoundException)
        {
            return Serialize(new
            {
                DocType = meta.Name,
                Name = name,
                Found = false,
                Error = $"Document '{meta.Name}/{name}' does not exist in tenant '{user.TenantId}'."
            });
        }
    }

    // ================================================================== 5. DocType Record Diagnostics

    public async Task<string> DiagnoseDocTypeRecordsAsync(
        [Description("DocType name, e.g. 'JournalEntry', 'SalesInvoice', 'StockEntry'")] string docType,
        [Description("Optional search keyword")] string? search = null,
        CancellationToken ct = default)
    {
        var meta = ResolveDocType(docType);
        if (meta is null || meta.Def.IsChild || meta.Def.IsSingle)
            return Serialize(new { Error = $"Listable DocType '{docType}' was not found." });

        try
        {
            var list = await documents.ListAsync(user, meta.Name, new ListRequest(Search: search, PageSize: 50), ct);
            int draftCount = 0, submittedCount = 0, cancelledCount = 0;

            foreach (var row in list.Items)
            {
                int ds = row.TryGetValue(StandardFields.DocStatus, out var v) && v is not null
                    ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                if (ds == 1) submittedCount++;
                else if (ds == 2) cancelledCount++;
                else draftCount++;
            }

            string? diagnosticHint = null;
            if (list.Total == 0)
            {
                diagnosticHint = $"No '{meta.Name}' records were found for user '{user.Id}'.";
            }
            else if (meta.Def.Document.AllowSubmit && submittedCount == 0 && draftCount > 0)
            {
                diagnosticHint = $"All {draftCount} visible '{meta.Name}' record(s) are still in Draft (docstatus = 0). None have been Submitted (docstatus = 1) yet. Because only Submitted documents post to the General Ledger and Stock, reports that read the ledger or stock movements (such as Trial Balance, Balance Sheet, Profit and Loss, Stock Balance) will not reflect these draft documents until they are submitted.";
            }

            var sample = list.Items.Take(10).Select(row =>
            {
                var name = row.GetValueOrDefault(StandardFields.Name)?.ToString() ?? "";
                var title = row.GetValueOrDefault("_title")?.ToString();
                int ds = row.TryGetValue(StandardFields.DocStatus, out var v) && v is not null
                    ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                var wfState = row.GetValueOrDefault(StandardFields.WorkflowState)?.ToString();
                var modified = row.GetValueOrDefault(StandardFields.Modified);
                return new
                {
                    Name = name,
                    Title = title,
                    DocStatus = ds,
                    DocStatusLabel = ds switch { 1 => "Submitted", 2 => "Cancelled", _ => "Draft" },
                    WorkflowState = wfState,
                    Modified = modified,
                    Url = $"/app/{meta.Name}/{Uri.EscapeDataString(name)}"
                };
            }).ToList();

            return Serialize(new
            {
                DocType = meta.Name,
                DocTypeTitle = meta.Title,
                TotalVisibleRecords = list.Total,
                SampledCount = list.Items.Count,
                CountsByStatus = new
                {
                    Draft = draftCount,
                    Submitted = submittedCount,
                    Cancelled = cancelledCount
                },
                DiagnosticHint = diagnosticHint,
                RecentRecords = sample
            });
        }
        catch (PermissionDeniedException ex)
        {
            return Serialize(new
            {
                DocType = meta.Name,
                CanRead = false,
                Error = ex.Message
            });
        }
    }

    // ================================================================== Helpers

    private CompiledDocType? ResolveDocType(string nameOrTitle)
    {
        var q = nameOrTitle.Trim();
        if (registry.Current.TryGet(q, out var exact))
            return exact;

        return registry.Current.DocTypes.Values.FirstOrDefault(d =>
            string.Equals(d.Name, q, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Title, q, StringComparison.OrdinalIgnoreCase));
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
