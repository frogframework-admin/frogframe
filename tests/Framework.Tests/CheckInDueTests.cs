using System.Globalization;
using Apps.Erp;
using Apps.Erp.Home;
using Framework.Metadata;
using Framework.Persistence;

/// <summary>1:1 cadence rules and the manager's "check-ins due" list.</summary>
sealed partial class Tests
{
    private sealed class CheckInClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string CheckInAt(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 2, 0, 0, TimeSpan.Zero).ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);

    [Test]
    public Task Check_in_cadence_rules()
    {
        var today = new DateOnly(2026, 11, 25);
        Assert.True(CheckInRules.IsDue(today, null, null), "never had a 1:1 → due");
        Assert.True(!CheckInRules.IsDue(today, today.AddDays(-10), null), "1:1 ten days ago → not due");
        Assert.True(CheckInRules.IsDue(today, today.AddDays(-31), null), "last 1:1 31 days ago → due");
        Assert.True(!CheckInRules.IsDue(today, today.AddDays(-31), today.AddDays(7)), "scheduled in 7 days → not due");
        Assert.True(CheckInRules.IsDue(today, today.AddDays(-31), today.AddDays(20)), "scheduled only after 14 days → still due");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Check_ins_due_lists_only_direct_reports_without_a_recent_one()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, store, _) = await MatrixSetupAsync(sp);
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var service = new CheckInDueService(store, new CheckInClock(new DateTimeOffset(2026, 11, 25, 3, 0, 0, TimeSpan.Zero)));

        var due = await service.DueAsync(manager);
        Assert.Equal(1, due.Count, "one direct report (STAFF-A)");
        Assert.True(due[0].LastCheckIn is null, "no 1:1 yet");

        await docs.InsertAsync(staff, "OneOnOne", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["meeting_at"] = CheckInAt(2026, 11, 20), ["status"] = "Completed",
            ["action_items"] = new List<Dictionary<string, object?>> { new() { ["action"] = "Send the form", ["due_date"] = "2026-11-22", ["status"] = "Open" } },
        });
        due = await service.DueAsync(manager);
        Assert.Equal(0, due.Count, "a 1:1 five days ago is enough");

        var someoneElse = await service.DueAsync(MatrixUser("STAFF-B"));
        Assert.Equal(0, someoneElse.Count, "a report with no direct reports sees nothing");
    }
}
