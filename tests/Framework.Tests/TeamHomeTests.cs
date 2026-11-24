using Apps.Erp;
using Apps.Erp.Home;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Home page "My team today" and "Who is away": direct reports' status, approved time off, day off, HR scope.</summary>
sealed partial class Tests
{
    [Test]
    public async Task Team_home_lists_direct_reports_time_off_and_day_off()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var team = sp.GetRequiredService<TeamHomeService>();

        // A fixed Wednesday at least a week ahead, so the weekly off (Saturday/Sunday) does not interfere.
        var day = MatrixToday.AddDays(7);
        while (day.DayOfWeek != DayOfWeek.Wednesday) day = day.AddDays(1);

        await LeaveForAsync(docs, "MGR-A", day, day.AddDays(1), approve: true);       // SPONSOR's report, on leave
        await LeaveForAsync(docs, "PM-B", day.AddDays(2), day.AddDays(2), approve: true); // upcoming
        await LeaveForAsync(docs, "STAFF-A", day, day, approve: false);              // pending: not shown
        await docs.InsertAsync(sys, "EmployeeDailySchedule", new Dictionary<string, object?>
        {
            ["employee"] = "PLT-A", ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["work_date"] = D.Iso(day),
            ["schedule_type"] = "REST", ["day_category"] = "Scheduled Rest Day", ["source_type"] = "Manual",
        });

        var sponsor = new UserContext("sponsor", "SPONSOR", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = "SPONSOR" });
        var o = await team.OverviewAsync(sponsor, default, day);
        Assert.Equal(3, o.DirectReports.Count, "SPONSOR has three direct reports");
        Assert.Equal("On leave", o.DirectReports.Single(r => r.Employee == "MGR-A").Status, "MGR-A on approved leave");
        Assert.Equal("Day off", o.DirectReports.Single(r => r.Employee == "PLT-A").Status, "PLT-A has a rest day");
        Assert.Equal("Not checked in", o.DirectReports.Single(r => r.Employee == "PM-B").Status, "PM-B works but has not clocked in");
        Assert.True(!o.CompanyWide, "team scope for a manager");
        Assert.True(o.AwayToday.Any(a => a.Employee == "MGR-A" && a.Kind == "Time off") && o.AwayToday.Any(a => a.Employee == "PLT-A" && a.Kind == "Day off"),
            "away today: time off and day off");
        Assert.True(o.UpcomingTimeOff.Any(a => a.Employee == "PM-B"), "upcoming time off of the team");
        Assert.True(o.AwayToday.All(a => a.Employee != "STAFF-A"), "not a direct report, and the leave is pending");

        var mgrA = new UserContext("mgr-a", "MGR-A", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = "MGR-A" });
        var oa = await team.OverviewAsync(mgrA, default, day);
        Assert.Equal("STAFF-A", oa.DirectReports.Single().Employee, "MGR-A's only direct report");
        Assert.Equal("Not checked in", oa.DirectReports.Single().Status, "pending leave does not count as leave");

        var staff = new UserContext("staff-b", "STAFF-B", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = "STAFF-B" });
        Assert.True((await team.OverviewAsync(staff, default, day)).IsEmpty, "no team, not HR: panel hidden");

        var hr = new UserContext("hr", "HR", "acme", new HashSet<string> { ErpRoles.HrEmployeeManagement }, new Dictionary<string, string>());
        var oh = await team.OverviewAsync(hr, default, day);
        Assert.True(oh.CompanyWide && oh.DirectReports.Count == 0, "HR sees the company, no team list");
        Assert.True(oh.AwayToday.Any(a => a.Employee == "MGR-A") && oh.AwayToday.Any(a => a.Employee == "PLT-A"), "HR sees time off and day off");
        Assert.True(oh.AwayToday.All(a => a.Employee != "STAFF-A"), "pending leave hidden for HR too");
    }
}
