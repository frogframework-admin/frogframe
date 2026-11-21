using Apps.Erp;
using Apps.Erp.Ess;
using Apps.Erp.Home;
using Apps.Erp.Hooks;
using Framework.Assignments;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Preferences;
using Framework.Security;
using Framework.Validation;
using Framework.Web;
using Framework.Web.Home;
using Framework.Web.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Home page (/): time of day in the application time zone, panel and quick-action registry, the approval queue with
/// inline workflow actions, and the ERP panels (my requests, company info, setup checklist, web check-in rule).
/// </summary>
sealed partial class Tests
{
    private sealed class HomeFixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; // a container without TZ
    }

    private static ServiceProvider BuildHomeProvider()
    {
        var erpMetadataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Apps.Erp", "Metadata"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddFrogFramework(o => o.LoadFrom(erpMetadataPath));
        services.AddFrogFrameworkIdentity();
        services.AddErpHooks();
        services.TryAddSingleton<IUserPreferenceStore, InMemoryUserPreferenceStore>();
        return services.BuildServiceProvider();
    }

    [Test]
    public Task Home_greeting_has_four_periods_and_dates_follow_the_application_time_zone()
    {
        Assert.Equal("Good evening", HomeClock.GreetingKey(3), "03:00");
        Assert.Equal("Good morning", HomeClock.GreetingKey(4), "04:00");
        Assert.Equal("Good morning", HomeClock.GreetingKey(10), "10:59");
        Assert.Equal("Good afternoon", HomeClock.GreetingKey(11), "11:00 (siang)");
        Assert.Equal("Good afternoon", HomeClock.GreetingKey(14), "14:59 (siang)");
        Assert.Equal("Good late afternoon", HomeClock.GreetingKey(15), "15:00 (sore)");
        Assert.Equal("Good late afternoon", HomeClock.GreetingKey(17), "17:59 (sore)");
        Assert.Equal("Good evening", HomeClock.GreetingKey(18), "18:00 (malam)");

        // 20:30 UTC on 21 Nov is 03:30 on 22 Nov in Jakarta: the server clock (UTC) must not decide "today".
        var clock = new HomeFixedClock(new DateTimeOffset(2026, 11, 21, 20, 30, 0, TimeSpan.Zero));
        var zone = FieldTypeSettings.DefaultTimeZone;
        var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        Assert.Equal(expected, HomeClock.Today(clock), "home page today");
        if (zone.BaseUtcOffset == TimeSpan.FromHours(7))
            Assert.Equal(new DateOnly(2026, 11, 22), expected, "Asia/Jakarta is UTC+7");
        var assignments = new AssignmentService(new InMemoryAssignmentStore(), null!, null!, null!, null!, clock);
        Assert.Equal(expected, assignments.Today, "My Tasks 'due today' / 'overdue' use the application time zone too");

        var now = clock.GetUtcNow();
        Assert.Equal("fresh", HomeClock.AgeTone(now.AddHours(-5), now), "waiting 5 hours");
        Assert.Equal("aging", HomeClock.AgeTone(now.AddDays(-2), now), "waiting 2 days");
        Assert.Equal("stale", HomeClock.AgeTone(now.AddDays(-4), now), "waiting 4 days");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Home_registry_orders_panels_and_shows_quick_actions_only_to_users_who_may_create()
    {
        await using var provider = BuildHomeProvider();
        var services = new ServiceCollection();
        services.AddHomeQuickAction("LeaveApplication", "Apply for leave", "calendar-off", 10);
        services.AddHomeQuickAction("JournalEntry", "Journal entry", "book-open", 20);
        services.AddHomeQuickAction(new HomeQuickAction("payables", "Payables desk", "/app/PurchaseInvoice", Roles: ["Finance-Payable"], Order: 30));
        services.AddHomeQuickAction("NoSuchDocType", "Ghost", "plus", 40);
        services.AddHomePanel<Microsoft.AspNetCore.Components.LayoutComponentBase>("late", HomeSlot.Main, 20);
        services.AddHomePanel<Microsoft.AspNetCore.Components.LayoutComponentBase>("early", HomeSlot.Main, 10);
        services.AddHomePanel<Microsoft.AspNetCore.Components.LayoutComponentBase>("admins", HomeSlot.Main, 5, u => u.IsAdmin);
        services.AddHomePanel<Microsoft.AspNetCore.Components.LayoutComponentBase>("side", HomeSlot.Side, 50);
        services.AddHomePanel<Microsoft.AspNetCore.Components.LayoutComponentBase>("side", HomeSlot.Side, 1); // same id: replaces
        await using var local = services.BuildServiceProvider();
        var registry = new HomeRegistry(local.GetServices<HomePanel>(), local.GetServices<HomeQuickAction>(),
            provider.GetRequiredService<IMetadataProvider>(), provider.GetRequiredService<IPermissionService>());

        var staff = MatrixUser("STAFF-A");
        var admin = UserContext.System("acme");
        Assert.Equal("early,late", string.Join(",", registry.PanelsFor(staff, HomeSlot.Main).Select(p => p.Id)), "order, and admin-only panel hidden");
        Assert.Equal("admins,early,late", string.Join(",", registry.PanelsFor(admin, HomeSlot.Main).Select(p => p.Id)), "admin sees the admin panel first");
        Assert.Equal(1, registry.PanelsFor(staff, HomeSlot.Side).Single().Order, "a later registration with the same id wins");

        var staffActions = registry.QuickActionsFor(staff).Select(a => a.Id).ToList();
        Assert.Equal("LeaveApplication", string.Join(",", staffActions), "an employee may apply for leave, not post journals or open the payables desk");
        Assert.Equal("/app/LeaveApplication/new", registry.QuickActionsFor(staff)[0].Href, "doc type shortcut opens a new form");
        var adminActions = registry.QuickActionsFor(admin).Select(a => a.Id).ToList();
        Assert.Equal("LeaveApplication,JournalEntry,payables", string.Join(",", adminActions), "admin: all existing DocTypes and role shortcuts; unknown DocType never shown");
        Assert.Equal(2, registry.QuickActionsFor(admin, max: 2).Count, "max");
    }

    [Test]
    public async Task Home_approval_queue_lists_decisions_with_comment_rule_and_runs_actions()
    {
        await using var provider = BuildHomeProvider();
        var (docs, _, _) = await MatrixSetupAsync(provider);
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var feed = new HomeFeed(docs, provider);

        var monday = MatrixToday.AddDays(7);
        while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(1); // a working day whatever day the test runs
        var draft = await docs.InsertAsync(staff, "LeaveApplication", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["leave_type"] = "Annual", ["from_date"] = D.Iso(monday), ["to_date"] = D.Iso(monday.AddDays(1)), ["reason"] = "Family",
        });
        Assert.True((await feed.ApprovalQueueAsync(staff)).All(i => i.Doc.Name != draft.Name), "the requester's own draft is not a decision");

        var leave = await docs.RunActionAsync(staff, "LeaveApplication", draft.Name, "Submit for approval", draft.Version, null);
        Assert.Equal("Pending Approval", leave.WorkflowState, "submitted");
        Assert.Equal("MGR-A", leave.Data.Str("leave_approver"), "routed to the manager");
        Assert.True((await feed.ApprovalQueueAsync(staff)).All(i => i.Doc.Name != leave.Name), "the requester can only withdraw: not listed under 'Needs your action'");

        var item = (await feed.ApprovalQueueAsync(manager)).Single(i => i.Doc.Name == leave.Name);
        Assert.Equal("HR", item.Group, "grouped by module");
        Assert.Equal("STAFF-A", item.Requester, "requester from employee_name");
        var approve = item.Actions.Single(a => a.Action == "Approve");
        var reject = item.Actions.Single(a => a.Action == "Reject");
        Assert.True(!approve.RequireComment && reject.RequireComment, "Reject needs a reason, Approve does not");
        Assert.Equal("Approved", approve.ToState, "target state");

        await Assert.Throws<ValidationFailedException>(() => feed.RunAsync(manager, item, "Reject", "  "), "blank reason rejected by the server");
        var done = await feed.RunAsync(manager, item, "Approve", null);
        Assert.Equal("Approved", done.WorkflowState, "approved from the home page");
        Assert.True((await feed.ApprovalQueueAsync(manager)).All(i => i.Doc.Name != leave.Name), "gone from the queue");
        await Assert.Throws<Exception>(() => feed.RunAsync(manager, item, "Approve", null), "stale row: the stored document moved on");
    }

    [Test]
    public async Task Home_erp_panels_show_my_requests_team_leave_next_holiday_and_setup_steps()
    {
        await using var provider = BuildHomeProvider();
        var (docs, store, _) = await MatrixSetupAsync(provider);
        var sys = UserContext.System("acme");
        await docs.InsertAsync(sys, "HolidayList", new Dictionary<string, object?>
        {
            ["holiday_list_name"] = "HL-ACME", ["from_date"] = $"{MatrixToday.Year}-01-01", ["to_date"] = $"{MatrixToday.Year + 1}-12-31", ["weekly_off"] = "None",
            ["holidays"] = new List<Dictionary<string, object?>>
            {
                new() { ["holiday_date"] = D.Iso(MatrixToday.AddDays(-3)), ["description"] = "Past day", ["holiday_type"] = "Public holiday" },
                new() { ["holiday_date"] = D.Iso(MatrixToday.AddDays(2)), ["description"] = "Saturday", ["holiday_type"] = "Weekend" },
                new() { ["holiday_date"] = D.Iso(MatrixToday.AddDays(10)), ["description"] = "Founders Day", ["holiday_type"] = "Company holiday" },
            },
        });
        var company = await docs.GetAsync(sys, "Company", "ACME");
        await docs.UpdateAsync(sys, "Company", "ACME", company.Version, new Dictionary<string, object?> { ["default_holiday_list"] = "HL-ACME" });
        await LeaveForAsync(docs, "STAFF-A", MatrixToday, MatrixToday.AddDays(4), approve: true); // five days always include working days

        var home = new ErpHomeService(store, provider.GetRequiredService<IMetadataProvider>(), TimeProvider.System, provider);
        var info = await home.CompanyInfoAsync(MatrixUser("MGR-A"));
        Assert.Equal("Founders Day", info.NextHolidayName, "next holiday skips past days and weekends");
        Assert.Equal(MatrixToday.AddDays(10), info.NextHolidayDate, "date");
        Assert.Equal("STAFF-A", string.Join(",", info.TeamOnLeaveToday), "approved leave of a colleague in the same unit");
        Assert.Equal(0, (await home.CompanyInfoAsync(MatrixUser("STAFF-A"))).TeamOnLeaveToday.Count, "own leave is not listed");

        var mine = await home.MyRequestsAsync(MatrixUser("STAFF-A"));
        var leave = mine.Single(r => r.DocType == "LeaveApplication");
        Assert.True(leave.Summary.StartsWith("Annual", StringComparison.Ordinal), $"summary: {leave.Summary}");
        Assert.Equal(0, (await home.MyRequestsAsync(UserContext.System("acme"))).Count, "no employee link: no requests panel data");
        var homeNoEss = new ErpHomeService(store, provider.GetRequiredService<IMetadataProvider>(), TimeProvider.System, new ServiceCollection().BuildServiceProvider());
        Assert.True(await homeNoEss.TodayAsync(MatrixUser("STAFF-A")) is null, "without the ESS service the day panel stays hidden instead of failing");

        var steps = (await home.SetupStepsAsync(sys)).ToDictionary(s => s.Id);
        Assert.True(steps["company"].Done, "company exists");
        Assert.True(steps["holidays"].Done, "holiday list covering today");
        Assert.True(!steps["fiscal-year"].Done, "no fiscal year yet");
        Assert.True(!steps["restore-drill"].Done && steps["restore-drill"].Manual, "restore drill is ticked by hand");
        await home.SetRestoreDrillAsync(sys, true);
        Assert.True((await home.SetupStepsAsync(sys)).Single(s => s.Id == "restore-drill").Done, "tick persisted in user preferences");

        var profile = new EssEmployeeProfile { Employee = "E1", FullName = "E1", AttendanceMode = EssAttendanceMode.Anywhere };
        Assert.True(ErpHomeService.WebPunchAllowed(profile, new EssAttendancePolicy { BindDevice = false }).Allowed, "anywhere, no device binding, no photo");
        Assert.True(!ErpHomeService.WebPunchAllowed(profile, new EssAttendancePolicy()).Allowed, "device binding is on by default: use the app");
        Assert.True(!ErpHomeService.WebPunchAllowed(profile with { AttendanceMode = EssAttendanceMode.AssignedLocation }, new EssAttendancePolicy { BindDevice = false }).Allowed, "location must be verified");
        Assert.True(!ErpHomeService.WebPunchAllowed(profile, new EssAttendancePolicy { BindDevice = false, RequirePhoto = true }).Allowed, "selfie required");
    }
}
