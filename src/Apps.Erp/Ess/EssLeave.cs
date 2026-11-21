using Apps.Erp.Hooks;
using Framework.Documents;

namespace Apps.Erp.Ess;

/// <summary>Leave balance of one leave type for one employee (ESS leave endpoints and the home page).</summary>
public sealed record EssLeaveBalance(string LeaveType, bool IsPaid, bool IsAnnualQuota, bool RequiresAttachment, decimal MaxConsecutiveDays,
    decimal? Allocated, decimal? Used, decimal? Pending, decimal? Balance, string? ValidFrom, string? ValidTo);

internal static class EssLeave
{
    /// <summary>
    /// Active leave types with the employee's balance in the allocation valid today (quota types only): allocated,
    /// used (submitted applications), pending (applications waiting for approval) and balance = allocated − used.
    /// Shared by the ESS API and the home page.
    /// </summary>
    internal static async Task<List<EssLeaveBalance>> BalancesAsync(Masters m, string emp, DateOnly today, CancellationToken ct)
    {
        var apps = (await m.QueryAsync("LeaveApplication", [new("employee", "=", emp)], ct, take: 1000)).Where(LeaveMath.Active).ToList();
        var allocations = (await m.QueryAsync("LeaveAllocation", [new("employee", "=", emp)], ct, take: 200))
            .Where(a => a.DocStatus == DocStatus.Submitted).ToList();
        var result = new List<EssLeaveBalance>();
        foreach (var type in await m.QueryAsync("LeaveType", [], ct, take: 200))
        {
            if (type.Data.ContainsKey("is_active") && type.Data["is_active"] is false) continue;
            var quota = type.Data.Bool("is_annual_quota");
            var alloc = quota ? allocations.FirstOrDefault(a => a.Data.Str("leave_type") == type.Name
                                                                && a.Data.Date("from_date") <= today && today <= a.Data.Date("to_date")) : null;
            decimal? allocated = null, used = null, pending = null, balance = null;
            if (alloc is not null)
            {
                var from = alloc.Data.Date("from_date"); var to = alloc.Data.Date("to_date");
                var inWindow = apps.Where(a => a.Data.Str("leave_type") == type.Name && Hr.Overlaps(from, to, a.Data.Date("from_date"), a.Data.Date("to_date"))).ToList();
                allocated = alloc.Data.Dec("total_leaves");
                used = inWindow.Where(a => a.DocStatus == DocStatus.Submitted).Sum(a => a.Data.Dec("total_days"));
                pending = inWindow.Where(a => a.DocStatus == DocStatus.Draft && a.WorkflowState is { } s && s.StartsWith("Pending", StringComparison.Ordinal))
                    .Sum(a => a.Data.Dec("total_days"));
                balance = allocated - used;
            }
            result.Add(new EssLeaveBalance(type.Name, type.Data.Bool("is_paid"), quota, type.Data.Bool("requires_attachment"),
                type.Data.Dec("max_consecutive_days"), allocated, used, pending, balance, alloc?.Data.Str("from_date"), alloc?.Data.Str("to_date")));
        }
        return result;
    }
}
