namespace Apps.Erp.Hooks;

/// <summary>
/// Daily attendance codes used for the monthly recap and payroll (docs/guides/attendance-codes.md).
/// </summary>
public static class AttendanceCodes
{
    public const string OnTime = "ONT";
    public const string LateIn = "LI";
    public const string EarlyOut = "LO";
    public const string LateInEarlyOut = "LI/LO";
    public const string NoClockIn = "NCIN";
    public const string NoClockOut = "NCOUT";
    public const string Absent = "ABS";
    public const string TimeOffPaid = "TO-ND";
    public const string TimeOffDeduction = "TO-D";
    public const string DayOff = "DO";
    public const string Invalid = "INV";

    public static readonly IReadOnlyList<string> All =
        [OnTime, LateIn, EarlyOut, LateInEarlyOut, NoClockIn, NoClockOut, Absent, TimeOffPaid, TimeOffDeduction, DayOff, Invalid];

    /// <summary>Attendance.status that goes with a code (payroll's "deduct absence" counts status Absent).</summary>
    public static string StatusFor(string code, string? currentStatus) => code switch
    {
        Absent => "Absent",
        TimeOffPaid or TimeOffDeduction => "On leave",
        DayOff => "Day off",
        _ => currentStatus is "Work from home" or "Half day" ? currentStatus : "Present",
    };

    public static bool IsLate(string? code) => code is LateIn or LateInEarlyOut;
    public static bool IsEarlyOut(string? code) => code is EarlyOut or LateInEarlyOut;
}

/// <summary>Everything the classification of one employee-day needs. Times are local (application time zone).</summary>
public sealed record AttendanceDayInput
{
    /// <summary>EmployeeDailySchedule.schedule_type (WORK, REST, LEAVE, HOLIDAY); null when no schedule was generated.</summary>
    public string? ScheduleType { get; init; }
    public bool IsPublicHoliday { get; init; }
    /// <summary>True when a shift applies (schedule WORK, or no schedule but the employee/default shift).</summary>
    public bool HasShift { get; init; }
    public DateTime? ScheduledStart { get; init; }
    public DateTime? ScheduledEnd { get; init; }
    public DateTime? CheckIn { get; init; }
    public DateTime? CheckOut { get; init; }
    public int LateGraceMinutes { get; init; }
    public int EarlyOutGraceMinutes { get; init; }
    /// <summary>ESS punches flagged for review (outside geofence, mock location, other device…).</summary>
    public string? FlaggedPunches { get; init; }
    /// <summary>Approved leave covering the date: null = none; true = paid leave type; false = unpaid (salary deducted).</summary>
    public bool? LeaveIsPaid { get; init; }
    public bool LeaveIsHalfDay { get; init; }
    public string? LeaveType { get; init; }
    /// <summary>Approved overtime request on this date (work on a day off is then allowed).</summary>
    public bool HasApprovedOvertime { get; init; }
    /// <summary>Status entered by HR: "Work from home" without punches counts as present.</summary>
    public string? CurrentStatus { get; init; }
    public DateTime Now { get; init; }
    /// <summary>Hours after the scheduled end before a missing clock-in/out becomes final (NCIN, NCOUT, ABS).</summary>
    public int FinalizeAfterHours { get; init; } = 4;
    /// <summary>A clock-in more than this many hours before the scheduled start is outside the shift window.</summary>
    public int WindowHours { get; init; } = 4;
}

/// <param name="Code">Null while the day is not decided yet (shift in progress, incomplete punches before the deadline).</param>
/// <param name="Final">False while a later punch, leave approval or HR entry may still change the code automatically.</param>
public sealed record AttendanceDayResult(string? Code, int LateMinutes, int EarlyOutMinutes, bool Final, string Reason);

public static class AttendanceClassifier
{
    /// <summary>
    /// Order of the rules (first match wins):
    /// <list type="number">
    /// <item>approved full-day leave → TO-ND (paid leave type) or TO-D (leave type not "Paid");</item>
    /// <item>day off (schedule REST or HOLIDAY, or public holiday): no punches → DO; punches with approved overtime → DO;
    /// punches without approved overtime → INV;</item>
    /// <item>no shift → no code (nothing to compare with);</item>
    /// <item>flagged ESS punches → INV; punches outside the shift window → INV;</item>
    /// <item>no punches → ABS once final (scheduled end + <see cref="AttendanceDayInput.FinalizeAfterHours"/>), or TO-x
    /// for half-day leave; "Work from home" entered by HR → ONT;</item>
    /// <item>only a clock-out → NCIN; only a clock-in → NCOUT (both once final);</item>
    /// <item>both → late minutes (after the late grace) and early-out minutes (after the early-out grace) give LI, LO,
    /// LI/LO or ONT. With half-day leave lateness and early leaving are not counted.</item>
    /// </list>
    /// </summary>
    public static AttendanceDayResult Classify(AttendanceDayInput d)
    {
        var hasIn = d.CheckIn is not null;
        var hasOut = d.CheckOut is not null;
        var punched = hasIn || hasOut;
        var finalAt = (d.ScheduledEnd ?? d.ScheduledStart?.AddHours(9))?.AddHours(d.FinalizeAfterHours);
        var final = finalAt is null || d.Now >= finalAt;

        if (d.LeaveIsPaid is { } paid && !d.LeaveIsHalfDay)
            return new(paid ? AttendanceCodes.TimeOffPaid : AttendanceCodes.TimeOffDeduction, 0, 0, true,
                $"Approved leave{(d.LeaveType is null ? "" : $" ({d.LeaveType})")}{(paid ? "" : ", salary deducted")}");

        var dayOff = d.ScheduleType is "REST" or "HOLIDAY" || d.IsPublicHoliday;
        if (dayOff)
        {
            var what = d.IsPublicHoliday || d.ScheduleType == "HOLIDAY" ? "holiday" : "scheduled rest day";
            if (!punched) return new(AttendanceCodes.DayOff, 0, 0, true, $"Day off ({what})");
            return d.HasApprovedOvertime
                ? new(AttendanceCodes.DayOff, 0, 0, true, $"Worked on a {what} with approved overtime")
                : new(AttendanceCodes.Invalid, 0, 0, true, $"Clocked on a {what} without approved overtime");
        }

        if (d.ScheduleType == "LEAVE" && !punched)
            return new(AttendanceCodes.TimeOffPaid, 0, 0, true, "Leave in the daily schedule");

        if (!d.HasShift || d.ScheduledStart is not { } start)
            return new(null, 0, 0, false, "No shift for this date");
        var end = d.ScheduledEnd ?? start.AddHours(9);

        if (!string.IsNullOrWhiteSpace(d.FlaggedPunches))
            return new(AttendanceCodes.Invalid, 0, 0, true, "Flagged mobile punch: " + d.FlaggedPunches.Split('\n')[0]);

        if ((d.CheckIn is { } ci && (ci < start.AddHours(-d.WindowHours) || ci >= end))
            || (d.CheckOut is { } co && co <= start))
            return new(AttendanceCodes.Invalid, 0, 0, true, "Clocked outside the shift window");

        if (!punched)
        {
            if (d.LeaveIsPaid is { } halfPaid)
                return new(halfPaid ? AttendanceCodes.TimeOffPaid : AttendanceCodes.TimeOffDeduction, 0, 0, true, "Half-day leave, no clock-in");
            if (d.CurrentStatus == "Work from home")
                return new(AttendanceCodes.OnTime, 0, 0, true, "Work from home entered by HR");
            return final ? new(AttendanceCodes.Absent, 0, 0, true, "Working day without clock-in or clock-out")
                         : new(null, 0, 0, false, "Shift not finished yet");
        }

        if (!hasIn) return final ? new(AttendanceCodes.NoClockIn, 0, 0, true, "Clock-out without clock-in") : new(null, 0, 0, false, "Waiting for clock-in");
        if (!hasOut)
        {
            var lateSoFar = LateMinutes(d, start);
            return final ? new(AttendanceCodes.NoClockOut, lateSoFar, 0, true, "Clock-in without clock-out")
                         : new(null, lateSoFar, 0, false, "Waiting for clock-out");
        }

        var late = d.LeaveIsHalfDay ? 0 : LateMinutes(d, start);
        var early = d.LeaveIsHalfDay ? 0 : EarlyOutMinutes(d, end);
        var code = (late > 0, early > 0) switch
        {
            (true, true) => AttendanceCodes.LateInEarlyOut,
            (true, false) => AttendanceCodes.LateIn,
            (false, true) => AttendanceCodes.EarlyOut,
            _ => AttendanceCodes.OnTime,
        };
        var reason = code switch
        {
            AttendanceCodes.LateInEarlyOut => $"Late {late} min, left {early} min early",
            AttendanceCodes.LateIn => $"Late {late} min",
            AttendanceCodes.EarlyOut => $"Left {early} min early",
            _ => d.LeaveIsHalfDay ? "Half-day leave; worked part of the shift" : "On time",
        };
        return new(code, late, early, true, reason);
    }

    /// <summary>Minutes after the scheduled start minus the grace period (as before: 15 minutes late with 10 grace = 5).</summary>
    private static int LateMinutes(AttendanceDayInput d, DateTime start)
    {
        if (d.CheckIn is not { } ci) return 0;
        var late = (int)(ci - start).TotalMinutes - d.LateGraceMinutes;
        return late > 0 && late < 12 * 60 ? late : 0;
    }

    private static int EarlyOutMinutes(AttendanceDayInput d, DateTime end)
    {
        if (d.CheckOut is not { } co) return 0;
        var early = (int)(end - co).TotalMinutes - d.EarlyOutGraceMinutes;
        return early > 0 && early < 12 * 60 ? early : 0;
    }
}
