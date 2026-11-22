using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Attendance codes (ONT, LI, EO, LI/EO, NCIN, NCOUT, ABS, TO-ND, TO-D, DO, INV), daily records for days without
/// punches, and the monthly recap used by payroll (docs/guides/attendance-codes.md).
/// </summary>
sealed partial class Tests
{
    [Test]
    public Task Attendance_classifier_rules()
    {
        var day = new DateOnly(2026, 11, 9); // a Monday
        DateTime At(int h, int m = 0, int addDays = 0) => day.AddDays(addDays).ToDateTime(new TimeOnly(h, m));
        var work = new AttendanceDayInput
        {
            ScheduleType = "WORK", HasShift = true, ScheduledStart = At(8), ScheduledEnd = At(17), LateGraceMinutes = 10, EarlyOutGraceMinutes = 5,
            Now = At(23, 0, 3),
        };
        AttendanceDayResult R(AttendanceDayInput i) => AttendanceClassifier.Classify(i);

        Assert.Equal("ONT", R(work with { CheckIn = At(8, 10), CheckOut = At(16, 55) }).Code, "within both grace periods");
        var li = R(work with { CheckIn = At(8, 25), CheckOut = At(17) });
        Assert.True(li.Code == "LI" && li.LateMinutes == 15 && li.Final, "late 25 min − 10 grace = 15");
        var lo = R(work with { CheckIn = At(8), CheckOut = At(16, 30) });
        Assert.True(lo.Code == "EO" && lo.EarlyOutMinutes == 25, "left 30 min early − 5 grace = 25");
        var both = R(work with { CheckIn = At(8, 40), CheckOut = At(16) });
        Assert.True(both.Code == "LI/EO" && both.LateMinutes == 30 && both.EarlyOutMinutes == 55, "late and early out on the same day");
        Assert.Equal("NCIN", R(work with { CheckOut = At(17) }).Code, "clock-out only");
        var ncout = R(work with { CheckIn = At(8, 20) });
        Assert.True(ncout.Code == "NCOUT" && ncout.LateMinutes == 10, "clock-in only; lateness still recorded");
        Assert.Equal("ABS", R(work).Code, "working day without punches");
        Assert.Equal("ONT", R(work with { CurrentStatus = "Work from home" }).Code, "work from home entered by HR");

        var running = work with { Now = At(19) }; // before 17:00 + 4 h
        Assert.True(R(running).Code is null && !R(running).Final, "absence not decided before the deadline");
        Assert.True(R(running with { CheckIn = At(8) }).Code is null, "no clock-out yet: waiting");
        Assert.Equal("ABS", R(work with { Now = At(21) }).Code, "decided at 17:00 + 4 h");

        Assert.Equal("TO-ND", R(work with { LeaveIsPaid = true, LeaveType = "Annual" }).Code, "paid leave");
        Assert.Equal("TO-D", R(work with { LeaveIsPaid = false, LeaveType = "Unpaid" }).Code, "leave type not 'Paid': salary deducted");
        Assert.Equal("TO-D", R(work with { LeaveIsPaid = false, CheckIn = At(8), CheckOut = At(17) }).Code, "full-day leave wins over punches");
        Assert.Equal("ONT", R(work with { LeaveIsPaid = true, LeaveIsHalfDay = true, CheckIn = At(13), CheckOut = At(17) }).Code, "half-day leave: lateness not counted");
        Assert.Equal("TO-ND", R(work with { LeaveIsPaid = true, LeaveIsHalfDay = true }).Code, "half-day leave without punches");

        var rest = work with { ScheduleType = "REST" };
        Assert.Equal("DO", R(rest).Code, "scheduled rest day");
        Assert.Equal("DO", R(work with { IsPublicHoliday = true }).Code, "public holiday on a working schedule");
        Assert.Equal("INV", R(rest with { CheckIn = At(9), CheckOut = At(12) }).Code, "worked on a day off without overtime");
        Assert.Equal("DO", R(rest with { CheckIn = At(9), CheckOut = At(12), HasApprovedOvertime = true }).Code, "worked on a day off with approved overtime");

        Assert.Equal("INV", R(work with { CheckIn = At(3), CheckOut = At(17) }).Code, "clock-in more than 4 h before the shift");
        Assert.Equal("INV", R(work with { CheckIn = At(18), CheckOut = At(19) }).Code, "clock-in after the shift end");
        Assert.Equal("INV", R(work with { CheckIn = At(8), CheckOut = At(17), FlaggedPunches = "Check-in 08:00: outside location radius" }).Code, "flagged mobile punch");

        // Night shift 22:00–06:00: clock-in 22:20, clock-out 06:00 next day.
        var night = work with { ScheduledStart = At(22), ScheduledEnd = At(6, 0, 1), CheckIn = At(22, 20), CheckOut = At(6, 0, 1) };
        Assert.True(R(night) is { Code: "LI", LateMinutes: 10 }, "cross-midnight shift");
        Assert.True(R(work with { HasShift = false, ScheduledStart = null, ScheduledEnd = null }).Code is null, "no shift: no code");

        Assert.Equal("Absent", AttendanceCodes.StatusFor("ABS", "Present"), "ABS keeps payroll's Absent status");
        Assert.Equal("Work from home", AttendanceCodes.StatusFor("LI", "Work from home"), "present codes keep WFH");
        Assert.Equal("Day off", AttendanceCodes.StatusFor("DO", "Present"), "DO status");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Attendance_days_and_monthly_recap_from_schedule_punches_and_leave()
    {
        await using var provider = BuildErpProvider();
        var (docs, store, m) = await MatrixSetupAsync(provider);
        var sys = UserContext.System("acme");
        var hr = MatrixUser("HR-USER", "HR", "Employee");
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?>
        {
            ["shift_name"] = "Office 08-17", ["start_time"] = "08:00:00", ["end_time"] = "17:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 8,
            ["working_days_per_week"] = "5", ["late_grace_minutes"] = 10, ["early_out_grace_minutes"] = 5, ["is_night_shift"] = false,
        });
        foreach (var e in new[] { "STAFF-A", "STAFF-B" })
        {
            var emp = await docs.GetAsync(sys, "Employee", e);
            await docs.UpdateAsync(sys, "Employee", e, emp.Version, new Dictionary<string, object?> { ["shift_type"] = "Office 08-17" });
        }
        await docs.InsertAsync(sys, "LeaveType", new Dictionary<string, object?> { ["leave_type_name"] = "Unpaid", ["is_annual_quota"] = false, ["is_paid"] = false, ["max_consecutive_days"] = 30 });

        var monday = MatrixToday.AddDays(-14);
        while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(-1);
        var zone = FieldTypeSettings.DefaultTimeZone;
        string Utc(DateOnly d, int h, int min = 0)
        {
            var local = d.ToDateTime(new TimeOnly(h, min));
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime().ToString(DatetimeFieldType.Format, System.Globalization.CultureInfo.InvariantCulture);
        }
        async Task<Document> Leave(string emp, string type, DateOnly d)
        {
            var l = await docs.InsertAsync(hr, "LeaveApplication", new Dictionary<string, object?>
                { ["employee"] = emp, ["leave_type"] = type, ["from_date"] = D.Iso(d), ["to_date"] = D.Iso(d), ["reason"] = "Away" });
            l = await docs.RunActionAsync(hr, "LeaveApplication", l.Name, "Submit for approval", l.Version, null);
            return await docs.RunActionAsync(hr, "LeaveApplication", l.Name, "Approve (HR)", l.Version, "OK");
        }
        async Task<Document> Punch(string emp, DateOnly d, (int H, int M)? @in, (int H, int M)? @out, string? status = null)
        {
            var data = new Dictionary<string, object?> { ["employee"] = emp, ["attendance_date"] = D.Iso(d), ["source"] = "Machine" };
            if (@in is { } i) data["check_in"] = Utc(d, i.H, i.M);
            if (@out is { } o) data["check_out"] = Utc(d, o.H, o.M);
            if (status is not null) data["status"] = status;
            return await docs.InsertAsync(hr, "Attendance", data);
        }

        await Leave("STAFF-A", "Unpaid", monday.AddDays(4));            // Friday
        await Leave("STAFF-B", "Annual", monday);                       // Monday
        var li = await Punch("STAFF-A", monday, (8, 25), (17, 0));
        Assert.True(li.Data.Str("attendance_code") == "LI" && li.Data.Dec("late_minutes") == 15 && li.Data.Bool("code_final"), "LI with 15 minutes");
        var lo = await Punch("STAFF-A", monday.AddDays(1), (7, 55), (16, 30));
        Assert.True(lo.Data.Str("attendance_code") == "EO" && lo.Data.Dec("early_out_minutes") == 25, "EO with 25 minutes");
        Assert.Equal("NCOUT", (await Punch("STAFF-A", monday.AddDays(2), (8, 30), null)).Data.Str("attendance_code"), "NCOUT");
        Assert.Equal("ONT", (await Punch("STAFF-B", monday.AddDays(1), (7, 50), (17, 5))).Data.Str("attendance_code"), "ONT");
        Assert.Equal("INV", (await Punch("STAFF-B", monday.AddDays(2), (3, 0), (17, 0))).Data.Str("attendance_code"), "INV outside the shift window");
        Assert.Equal("INV", (await Punch("STAFF-B", monday.AddDays(5), (9, 0), (12, 0))).Data.Str("attendance_code"), "INV on Saturday without overtime");
        var manual = await docs.InsertAsync(hr, "Attendance", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-B", ["attendance_date"] = D.Iso(monday.AddDays(3)), ["status"] = "Work from home", ["code_manual"] = true, ["attendance_code"] = "LI",
        });
        Assert.True(manual.Data.Str("attendance_code") == "LI" && manual.Data.Str("status") == "Work from home", "code and status set by HR are kept");

        var r = await AttendanceDays.EnsureAsync(docs, store, "acme", monday, monday.AddDays(6), recalculateAll: true, TimeProvider.System, null, default, employee: "STAFF-A");
        Assert.True(r.Created == 4 && r.Failed == 0, $"Thursday (ABS), Friday (leave), Saturday and Sunday (DO) created: {r}");
        var thu = (await m.QueryAsync("Attendance", [new("employee", "=", "STAFF-A"), new("attendance_date", "=", D.Iso(monday.AddDays(3)))], default)).Single();
        Assert.True(thu.Data.Str("attendance_code") == "ABS" && thu.Data.Str("status") == "Absent" && thu.Data.Str("source") == "System", "ABS record for a day without punches");

        await AttendanceDays.EnsureAsync(docs, store, "acme", monday, monday.AddDays(6), recalculateAll: true, TimeProvider.System, null, default, employee: "STAFF-B");
        var rows = (await AttendanceDays.RecapAsync(store, "acme", monday, monday.AddDays(6), default)).ToDictionary(x => x.Employee);
        var a = rows["STAFF-A"];
        Assert.Equal("LI1 EO1 NCOUT1 ABS1 TO-D1 DO2", $"LI{a.Count("LI")} EO{a.Count("EO")} NCOUT{a.Count("NCOUT")} ABS{a.Count("ABS")} TO-D{a.Count("TO-D")} DO{a.Count("DO")}", "STAFF-A week");
        Assert.True(a is { LateDays: 1, LateMinutes: 15, EarlyOutDays: 1, EarlyOutMinutes: 25, UnclassifiedDays: 0 }, "minutes and open days");
        var b = rows["STAFF-B"];
        Assert.Equal("TO-ND1 ONT1 INV2 LI1 DO1", $"TO-ND{b.Count("TO-ND")} ONT{b.Count("ONT")} INV{b.Count("INV")} LI{b.Count("LI")} DO{b.Count("DO")}",
            "STAFF-B week: the code set by HR is kept by Recalculate");
        Assert.True(b.Count("ABS") == 1, "Friday without punches");
    }
}
