using Framework.Security;

namespace Apps.Erp;

/// <summary>
/// ERP role catalog: granular roles per module, and the former broad roles (HR, Payroll, Finance, Sales, Purchasing,
/// Stock) kept as <see cref="RoleGroups">role groups</see>. A user who still holds "Finance" gets every
/// Finance and Accounting role (same access as before the split); a user with only "Finance-Payable" gets just that.
/// DocType permissions and workflow transitions name the granular roles. See docs/guides/roles.md.
/// </summary>
public static class ErpRoles
{
    // Cross-module roles (unchanged).
    public const string Employee = "Employee";
    public const string Manager = "Manager";

    // HR
    public const string HrAdmin = "HR-Admin";
    public const string HrRecruitment = "HR-Recruitment";
    public const string HrEmployeeManagement = "HR-Employee-Management";
    public const string HrTimeAttendance = "HR-Time-Attendance";
    public const string HrTalent = "HR-Talent";
    public const string HrPayroll = "HR-Payroll";
    public const string HrComben = "HR-Comben";

    // CRM
    public const string CrmAdmin = "CRM-Admin";
    public const string CrmLeadManagement = "CRM-Lead-Management";
    public const string CrmSales = "CRM-Sales";
    public const string CrmMarketing = "CRM-Marketing";

    // Finance
    public const string FinanceAdmin = "Finance-Admin";
    public const string FinanceCashier = "Finance-Cashier";
    public const string FinanceReceivable = "Finance-Receivable";
    public const string FinancePayable = "Finance-Payable";
    public const string FinanceTreasury = "Finance-Treasury";
    public const string FinanceTax = "Finance-Tax";

    // Accounting
    public const string AccountingAdmin = "Accounting-Admin";
    public const string AccountingGeneralLedger = "Accounting-General-Ledger";
    public const string AccountingFixedAssets = "Accounting-Fixed-Assets";
    public const string AccountingBudgetCosting = "Accounting-Budget-Costing";
    public const string AccountingClosing = "Accounting-Closing";
    public const string AccountingAudit = "Accounting-Audit";

    // Procurement
    public const string ProcurementAdmin = "Procurement-Admin";
    public const string ProcurementBuyer = "Procurement-Buyer";
    public const string ProcurementReceiving = "Procurement-Receiving";

    // Stock
    public const string StockAdmin = "Stock-Admin";
    public const string StockKeeper = "Stock-Keeper";
    public const string StockOpname = "Stock-Opname";

    // Former broad roles, now groups.
    public const string HrGroup = "HR";
    public const string PayrollGroup = "Payroll";
    public const string FinanceGroup = "Finance";
    public const string SalesGroup = "Sales";
    public const string PurchasingGroup = "Purchasing";
    public const string StockGroup = "Stock";

    /// <summary>Granular roles with a short description of what they own.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        (HrAdmin, "HR setup: HR settings, job families and profiles, grades, organisation unit types, leave types and policies, holiday lists, locations."),
        (HrRecruitment, "Recruitment: job requisitions, applicants (including personal data), interviews and job offers."),
        (HrEmployeeManagement, "Employee records (including personal data), contracts, job changes, separations, positions and organisation units."),
        (HrTimeAttendance, "Time and attendance: attendance, leave allocations and applications, overtime, shifts, rosters and schedules."),
        (HrTalent, "Talent: appraisals, appraisal cycles and training events."),
        (HrPayroll, "Payroll: payroll runs, payslips and payroll settings. Should not also hold HR-Comben."),
        (HrComben, "Compensation and benefits: salary components and salary assignments; reads grade pay ranges."),
        (CrmAdmin, "CRM setup: CRM settings, sales stages, territories, industries, lead sources and lost reasons."),
        (CrmLeadManagement, "Leads and activities; converts leads into customers, contacts and opportunities."),
        (CrmSales, "Opportunities, quotations, sales orders, customer contracts, customers and contacts."),
        (CrmMarketing, "Campaigns and leads from campaigns."),
        (FinanceAdmin, "Finance setup and master data: finance settings, payment terms, expense claim types, currencies, items, suppliers and their bank accounts."),
        (FinanceCashier, "Cash and bank payments and receipts (Payment Entry)."),
        (FinanceReceivable, "Sales invoices, credit notes, customer receipts and payment reconciliation."),
        (FinancePayable, "Supplier invoices, debit notes, expense claims, employee advances and commission statements. Does not create payments."),
        (FinanceTreasury, "Bank accounts, bank statements and reconciliation, payment runs, payments, exchange rates."),
        (FinanceTax, "Tax codes, tax invoices (Coretax) and withholding certificates."),
        (AccountingAdmin, "Chart of accounts, fiscal years, accounting dimensions, accounting settings, company; the ledger accounts in payroll, salary component and stock settings."),
        (AccountingGeneralLedger, "Manual journal entries."),
        (AccountingFixedAssets, "Fixed assets, asset categories, depreciation runs and disposals."),
        (AccountingBudgetCosting, "Budgets, cost centers and projects."),
        (AccountingClosing, "Accounting periods (locks) and period closing vouchers."),
        (AccountingAudit, "Read, report and export on Finance and Accounting documents. No changes."),
        (ProcurementAdmin, "Procurement master data: suppliers, supplier groups, items, price lists, payment terms."),
        (ProcurementBuyer, "Purchase orders; turns approved purchase requests into orders."),
        (ProcurementReceiving, "Goods receipts against purchase orders."),
        (StockAdmin, "Warehouses and stock settings."),
        (StockKeeper, "Stock entries (receipts, issues, transfers) and goods receipts."),
        (StockOpname, "Physical stock counts (stock reconciliation)."),
    ];

    /// <summary>Former broad roles and the granular roles they now stand for.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        [HrGroup] = [HrAdmin, HrRecruitment, HrEmployeeManagement, HrTimeAttendance, HrTalent],
        [PayrollGroup] = [HrPayroll, HrComben],
        [FinanceGroup] =
        [
            FinanceAdmin, FinanceCashier, FinanceReceivable, FinancePayable, FinanceTreasury, FinanceTax,
            AccountingAdmin, AccountingGeneralLedger, AccountingFixedAssets, AccountingBudgetCosting, AccountingClosing, AccountingAudit,
        ],
        [SalesGroup] = [CrmAdmin, CrmLeadManagement, CrmSales, CrmMarketing],
        [PurchasingGroup] = [ProcurementAdmin, ProcurementBuyer, ProcurementReceiving],
        [StockGroup] = [StockAdmin, StockKeeper, StockOpname],
    };

    /// <summary>
    /// Role pairs that should not be held by one person (segregation of duties). Shown as a warning on the user page;
    /// not enforced, because small teams sometimes have no choice.
    /// </summary>
    public static readonly IReadOnlyList<(string A, string B, string Why)> Conflicts =
    [
        (HrComben, HrPayroll, "sets pay and runs payroll"),
        (FinancePayable, FinanceTreasury, "records supplier invoices and pays them"),
        (FinancePayable, FinanceCashier, "records supplier invoices and pays them"),
        (FinancePayable, FinanceAdmin, "records supplier invoices and changes supplier bank accounts"),
        (AccountingGeneralLedger, AccountingClosing, "posts journals and locks the periods they are in"),
        (ProcurementBuyer, ProcurementReceiving, "orders goods and confirms their receipt"),
        (StockKeeper, StockOpname, "moves stock and counts it"),
    ];

    /// <summary>Registers the roles (added to every tenant's role list) and the groups. Idempotent.</summary>
    public static void Register()
    {
        RoleDefaults.RegisterAppRoles(All);
        foreach (var (group, members) in Groups) RoleGroups.Register(group, members);
        foreach (var (a, b, why) in Conflicts) RoleGroups.RegisterConflict(a, b, why);
        foreach (var r in Groups[FinanceGroup].Where(r => r != AccountingAudit))
            RoleGroups.RegisterConflict(AccountingAudit, r, "the auditor can change what they audit");
    }

    /// <summary>
    /// DocType permissions and workflow transitions of the active metadata that still name a group (for example a
    /// DocType an administrator changed in the designer before the split). A group there gives every member the group's
    /// full rights, so these are logged at startup and should be changed to granular roles.
    /// </summary>
    public static IReadOnlyList<string> FindGroupUses(Framework.Metadata.MetadataSnapshot snapshot)
    {
        var uses = new List<string>();
        foreach (var d in snapshot.DocTypes.Values.OrderBy(d => d.Name))
        {
            var groups = d.Def.Permissions.Select(p => p.Role).Where(Groups.ContainsKey).Distinct().ToList();
            if (groups.Count > 0) uses.Add($"DocType {d.Name}: {string.Join(", ", groups)}");
        }
        foreach (var w in snapshot.Workflows.Values.OrderBy(w => w.Def.Name))
        {
            var groups = w.Def.Transitions.SelectMany(t => t.AllowedRoles).Where(Groups.ContainsKey).Distinct().ToList();
            if (groups.Count > 0) uses.Add($"Workflow {w.Def.Name}: {string.Join(", ", groups)}");
        }
        return uses;
    }

    /// <summary>Conflicting pairs among a user's effective roles (see <see cref="Conflicts"/>; Accounting-Audit with any Finance or Accounting role that can change data).</summary>
    public static IReadOnlyList<string> ConflictsOf(IEnumerable<string> assignedRoles)
    {
        Register();
        return RoleGroups.FindConflicts(assignedRoles);
    }
}
