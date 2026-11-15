using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Saldo cuti per karyawan per jenis cuti untuk alokasi yang berlaku pada tanggal tertentu.</summary>
public sealed class LeaveBalanceReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-leave-balance";
    public string Title => "Leave Balance";
    public string Description => "Per employee and leave type: entitlement of the allocation valid on the date, leave taken (approved), pending approval, and remaining balance.";
    public string DocType => "LeaveAllocation";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("as_of", "As of date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var allocations = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(a => a.DocStatus != DocStatus.Cancelled && a.Data.Date("from_date") <= asOf && a.Data.Date("to_date") >= asOf).ToList();
        IReadOnlyList<Document> applications;
        try { applications = await documents.QueryForReportAsync(user, "LeaveApplication", [], ct: ct); }
        catch (PermissionDeniedException) { applications = []; }

        var rows = new List<Dictionary<string, object?>>();
        foreach (var a in allocations.OrderBy(a => a.Data.Str("employee"), StringComparer.Ordinal).ThenBy(a => a.Data.Str("leave_type")))
        {
            var emp = a.Data.Str("employee"); var type = a.Data.Str("leave_type");
            DateOnly start = a.Data.Date("from_date")!.Value, end = a.Data.Date("to_date")!.Value;
            var mine = applications.Where(x => x.Data.Str("employee") == emp && x.Data.Str("leave_type") == type
                                               && x.Data.Date("from_date") >= start && x.Data.Date("from_date") <= end && x.DocStatus != DocStatus.Cancelled).ToList();
            var taken = (decimal)mine.Where(x => x.WorkflowState == Approval.Approved).Sum(x => x.Data.Dec("total_days"));
            var pending = (decimal)mine.Where(x => x.WorkflowState == "Pending Approval").Sum(x => x.Data.Dec("total_days"));
            var total = a.Data.Dec("total_leaves");
            rows.Add(Rpt.Row(("employee", emp), ("employee_name", a.Data.Str("employee_name")), ("leave_type", type),
                ("period", $"{D.Iso(start)} – {D.Iso(end)}"), ("total_leaves", total), ("taken", taken), ("pending", pending),
                ("balance", total - taken), ("available", total - taken - pending)));
        }
        return new ReportResult(
        [
            new("employee", "Employee"), new("employee_name", "Name"), new("leave_type", "Leave type"), new("period", "Allocation period"),
            new("total_leaves", "Entitlement (days)", "Float"), new("taken", "Taken", "Float"), new("pending", "Pending approval", "Float"),
            new("balance", "Balance", "Float"), new("available", "Available (after pending)", "Float"),
        ], rows);
    }
}
