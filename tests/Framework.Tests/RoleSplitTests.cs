using Apps.Erp;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Granular ERP roles: the former broad roles are groups, DocType permissions and workflows name the granular roles,
/// and the segregation of duties holds. See docs/guides/roles.md.
/// </summary>
sealed partial class Tests
{
    private static UserContext RoleUser(params string[] assigned) =>
        new("u-" + string.Join("-", assigned), "Role user", "acme", RoleGroups.Expand(assigned), new Dictionary<string, string>());

    [Test]
    public Task Role_groups_expand_both_ways_without_chaining()
    {
        ErpRoles.Register();
        var legacy = RoleGroups.Expand(["Finance", "Employee"]);
        Assert.True(ErpRoles.Groups["Finance"].All(legacy.Contains), "a former broad role grants all of its granular roles");
        Assert.True(legacy.Contains("Finance") && legacy.Contains("Employee"), "assigned roles kept");

        var payable = RoleGroups.Expand([ErpRoles.FinancePayable]);
        Assert.True(payable.Contains("Finance"), "a member counts as the group for menus, dashboards and notifications");
        Assert.True(!payable.Contains(ErpRoles.FinanceTreasury) && !payable.Contains(ErpRoles.AccountingAdmin), "a member never gets its siblings");
        Assert.Equal(2, payable.Count, "Finance-Payable + Finance only");

        Assert.True(RoleGroups.Expand(["hr"]).Contains(ErpRoles.HrTimeAttendance), "group names are case-insensitive");
        Assert.True(!RoleGroups.Expand(["HR"]).Contains(ErpRoles.HrPayroll) && !RoleGroups.Expand(["HR"]).Contains(ErpRoles.HrComben),
            "HR did not include payroll before and does not now");
        Assert.True(RoleGroups.Expand(["Payroll"]).Contains(ErpRoles.HrComben), "Payroll covers payroll and compensation");
        Assert.Equal("System Manager", string.Join(",", RoleGroups.Expand([UserContext.SystemManager])), "other roles unchanged");

        var defaults = RoleDefaults.CreateDefaultRoles("acme", DateTimeOffset.UnixEpoch);
        Assert.True(ErpRoles.All.All(r => defaults.Any(d => d.Name == r.Name && !string.IsNullOrEmpty(d.Description))), "all ERP roles seeded with descriptions");
        Assert.Equal(defaults.Count, defaults.Select(d => d.Id).Distinct().Count(), "unique role ids");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Role_split_erp_metadata_names_granular_roles_only()
    {
        await using var provider = BuildErpProvider();
        var snapshot = provider.GetRequiredService<IMetadataProvider>().Current;
        Assert.Equal("", string.Join("; ", ErpRoles.FindGroupUses(snapshot)), "no DocType permission or workflow transition names a group");

        var known = ErpRoles.All.Select(r => r.Name).Concat([ErpRoles.Employee, ErpRoles.Manager, UserContext.SystemManager]).ToHashSet();
        var used = snapshot.DocTypes.Values.SelectMany(d => d.Def.Permissions.Select(p => p.Role))
            .Concat(snapshot.Workflows.Values.SelectMany(w => w.Def.Transitions.SelectMany(t => t.AllowedRoles))).ToHashSet();
        Assert.Equal("", string.Join(", ", used.Where(r => !known.Contains(r)).Order()), "every role in metadata is a known role");
        Assert.Equal("", string.Join(", ", ErpRoles.All.Select(r => r.Name).Where(r => !used.Contains(r))), "every granular role is used");

        var writeActions = new[] { DocActions.Create, DocActions.Write, DocActions.Delete, DocActions.Submit, DocActions.Cancel };
        var auditWrites = snapshot.DocTypes.Values
            .Where(d => d.Def.Permissions.Any(p => p.Role == ErpRoles.AccountingAudit && p.Actions.Any(writeActions.Contains)))
            .Select(d => d.Name).ToList();
        Assert.Equal("", string.Join(", ", auditWrites), "Accounting-Audit never writes");
    }

    [Test]
    public async Task Role_split_enforces_segregation_of_duties()
    {
        await using var provider = BuildErpProvider();
        var perms = provider.GetRequiredService<IPermissionService>();
        var meta = provider.GetRequiredService<IMetadataProvider>().Current;
        bool Can(string docType, string action, params string[] roles) => perms.Can(RoleUser(roles), meta.Get(docType), action);

        // Finance: who records, who pays, who audits.
        Assert.True(Can("PurchaseInvoice", DocActions.Submit, ErpRoles.FinancePayable), "Payable submits supplier invoices");
        Assert.True(!Can("PaymentEntry", DocActions.Create, ErpRoles.FinancePayable) && Can("PaymentEntry", DocActions.Read, ErpRoles.FinancePayable),
            "Payable cannot create payments, only read them");
        Assert.True(Can("PaymentEntry", DocActions.Submit, ErpRoles.FinanceTreasury) && Can("PaymentRun", DocActions.Submit, ErpRoles.FinanceTreasury),
            "Treasury pays and runs payment runs");
        Assert.True(!Can("PurchaseInvoice", DocActions.Create, ErpRoles.FinanceTreasury), "Treasury cannot record supplier invoices");
        Assert.True(Can("SalesInvoice", DocActions.Submit, ErpRoles.FinanceReceivable) && !Can("JournalEntry", DocActions.Create, ErpRoles.FinanceReceivable),
            "Receivable invoices customers but does not post journals");
        Assert.True(Can("JournalEntry", DocActions.Submit, ErpRoles.AccountingGeneralLedger) && !Can("PeriodClosingVoucher", DocActions.Create, ErpRoles.AccountingGeneralLedger),
            "GL posts journals but does not close periods");
        Assert.True(Can("PeriodClosingVoucher", DocActions.Submit, ErpRoles.AccountingClosing) && Can("AccountingPeriod", DocActions.Write, ErpRoles.AccountingClosing),
            "Closing locks periods");
        Assert.True(Can("JournalEntry", DocActions.Export, ErpRoles.AccountingAudit) && !Can("JournalEntry", DocActions.Write, ErpRoles.AccountingAudit),
            "Audit exports but never writes");
        Assert.True(Can("TaxInvoice", DocActions.Submit, ErpRoles.FinanceTax) && Can("Asset", DocActions.Submit, ErpRoles.AccountingFixedAssets),
            "tax and fixed assets have their owners");

        // Payroll vs compensation.
        Assert.True(Can("SalaryAssignment", DocActions.Write, ErpRoles.HrComben) && !Can("PayrollRun", DocActions.Create, ErpRoles.HrComben),
            "Comben sets pay but does not run payroll");
        Assert.True(Can("PayrollRun", DocActions.Submit, ErpRoles.HrPayroll) && !Can("SalaryAssignment", DocActions.Write, ErpRoles.HrPayroll),
            "Payroll runs payroll but does not set pay");
        Assert.True(!Can("PayrollRun", DocActions.Create, "Finance") && Can("PayrollRun", DocActions.Read, "Finance"),
            "former Finance holders read payroll but no longer run it (deliberate)");

        // HR personal data (permission level 1).
        var employee = meta.Get("Employee");
        var dob = employee.Fields.Single(f => f.Def.Name == "date_of_birth");
        Assert.True(perms.CanReadField(RoleUser(ErpRoles.HrEmployeeManagement), employee, dob), "employee management reads personal data");
        Assert.True(perms.Can(RoleUser(ErpRoles.HrRecruitment), employee, DocActions.Read) && !perms.CanReadField(RoleUser(ErpRoles.HrRecruitment), employee, dob),
            "recruitment reads employees but not their personal data");
        Assert.True(!Can("Employee", DocActions.Write, ErpRoles.HrTimeAttendance), "time & attendance cannot change employee records");

        // Supplier bank accounts: only Finance-Admin changes them.
        var vendor = meta.Get("Vendor");
        var bank = vendor.Fields.Single(f => f.Def.Name == "bank_accounts");
        Assert.True(Can("Vendor", DocActions.Write, ErpRoles.ProcurementAdmin) && !perms.CanWriteField(RoleUser(ErpRoles.ProcurementAdmin), vendor, bank)
            && perms.CanReadField(RoleUser(ErpRoles.ProcurementAdmin), vendor, bank), "procurement edits suppliers, sees but cannot change bank accounts");
        Assert.True(perms.CanWriteField(RoleUser(ErpRoles.FinanceAdmin), vendor, bank), "Finance-Admin changes supplier bank accounts");
        Assert.True(!perms.CanReadField(RoleUser(ErpRoles.Employee), vendor, bank), "employees do not see supplier bank accounts");

        // Stock: moving vs counting; procurement no longer posts stock.
        Assert.True(Can("StockEntry", DocActions.Submit, ErpRoles.StockKeeper) && !Can("StockReconciliation", DocActions.Create, ErpRoles.StockKeeper), "keeper moves, does not count");
        Assert.True(!Can("StockEntry", DocActions.Create, "Purchasing") && Can("PurchaseOrder", DocActions.Submit, "Purchasing"),
            "former Purchasing holders order but no longer post stock (deliberate)");

        // Former broad roles keep the rest of their rights.
        foreach (var (docType, action) in new[] { ("JournalEntry", DocActions.Submit), ("PaymentEntry", DocActions.Submit), ("PurchaseInvoice", DocActions.Submit),
                     ("SalesInvoice", DocActions.Submit), ("Budget", DocActions.Submit), ("Asset", DocActions.Submit), ("TaxInvoice", DocActions.Submit) })
            Assert.True(Can(docType, action, "Finance"), $"Finance still can {action} {docType}");
        Assert.True(Can("LeaveApplication", DocActions.Write, "HR") && Can("Employee", DocActions.Write, "HR") && Can("JobOffer", DocActions.Write, "HR"), "HR keeps its rights");
        Assert.True(Can("Quotation", DocActions.Submit, "Sales") && Can("Lead", DocActions.Create, "Sales"), "Sales keeps its rights");

        // Workflow transitions and form actions follow the granular roles.
        var wf = meta.Workflows.Values.Single(w => w.Def.DocType == "LeaveApplication").Def;
        Assert.True(wf.Transitions.Any(t => t.AllowedRoles.Contains(ErpRoles.HrTimeAttendance)), "leave approval by HR-Time-Attendance");
        var now = DateTimeOffset.UnixEpoch;
        var doc = new Document { TenantId = "acme", DocType = "PayrollRun", Name = "PR-1", Owner = "system", ModifiedBy = "system", Created = now, Modified = now, Version = 1, SchemaVersion = 1, DocStatus = DocStatus.Draft };
        var createPayslips = new PayrollRunCreatePayslipsAction(provider.GetRequiredService<Framework.Persistence.IDocumentStore>());
        Assert.True(createPayslips.IsVisible(RoleUser(ErpRoles.HrPayroll), doc) && !createPayslips.IsVisible(RoleUser(ErpRoles.FinancePayable), doc),
            "'Create payslips' for HR-Payroll only");

        // Conflicts are reported.
        Assert.Equal(1, ErpRoles.ConflictsOf([ErpRoles.FinancePayable, ErpRoles.FinanceTreasury]).Count, "Payable + Treasury conflict");
        Assert.Equal(0, ErpRoles.ConflictsOf([ErpRoles.FinancePayable, ErpRoles.FinanceReceivable]).Count, "Payable + Receivable fine");
        Assert.True(ErpRoles.ConflictsOf([ErpRoles.AccountingAudit, ErpRoles.AccountingGeneralLedger]).Count > 0, "auditor with a write role");
    }
}
