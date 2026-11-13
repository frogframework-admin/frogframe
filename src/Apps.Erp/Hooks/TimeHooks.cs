using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

public sealed class Attendance { }
public sealed class OvertimeRequest { }

/// <summary>Lembur menurut PP 35/2021: pengali (Pasal 31), batas jam (Pasal 26 jo. UU 13/2003 Pasal 78 sebagaimana diubah), upah sejam 1/173 (Pasal 32).</summary>
internal static class Overtime
{
    public const string WorkingDay = "Working day", RestDay = "Rest day or public holiday", ShortestDay = "Public holiday on the shortest working day";
    public const decimal MaxPerWorkingDay = 4, MaxPerWeek = 18;

    /// <summary>Jam dibayar (jam × pengali) dan batas jam per hari untuk jenis hari dan pola kerja 5/6 hari.</summary>
    public static (decimal Weighted, decimal MaxHours) Weigh(string dayType, int workWeekDays, decimal hours)
    {
        static decimal Tiers(decimal h, decimal t2) =>
            Math.Min(h, t2) * 2 + Math.Clamp(h - t2, 0, 1) * 3 + Math.Clamp(h - t2 - 1, 0, 3) * 4;
        return dayType switch
        {
            WorkingDay => (Math.Min(hours, 1) * 1.5m + Math.Max(hours - 1, 0) * 2, MaxPerWorkingDay),
            ShortestDay => (Tiers(hours, 5), 9),
            _ when workWeekDays == 5 => (Tiers(hours, 8), 12),
            _ => (Tiers(hours, 7), 11),
        };
    }

    public static async Task<int> WorkWeekAsync(Masters m, Document emp, CancellationToken ct)
    {
        var shift = emp.Data.Str("shift_type") ?? (await m.SingleAsync("HRSettings", ct)).Str("default_shift_type");
        return await m.StrAsync("ShiftType", shift, "working_days_per_week", ct) == "6" ? 6 : 5;
    }

    /// <summary>
    /// Upah lembur sebulan untuk payslip: Σ jam dibayar lembur Approved × upah sejam. Upah sebulan = pendapatan regular bertanda dasar BPJS
    /// (upah pokok + tunjangan tetap); bila itu kurang dari 75% total pendapatan regular, dipakai 75% total (Pasal 32 ayat 4).
    /// </summary>
    public static async Task<(decimal WeightedHours, decimal HourlyWage, decimal Amount)> PayAsync(Masters m, string employee, string period, Document assignment, CancellationToken ct)
    {
        var approved = (await m.QueryAsync("OvertimeRequest", [new FilterClause("employee", "=", employee), new FilterClause("period", "=", period)], ct, take: 500))
            .Where(o => o.DocStatus == DocStatus.Submitted && o.WorkflowState == Approval.Approved).ToList();
        if (approved.Count == 0) return (0, 0, 0);
        decimal fixedPay = 0, regular = 0;
        foreach (var r in assignment.Data.Rows("earnings"))
        {
            var comp = await m.GetAsync("SalaryComponent", r.Str("salary_component"), ct);
            if (comp is null || !comp.Data.Bool("is_regular")) continue;
            regular += r.Dec("amount");
            if (comp.Data.Bool("in_bpjs_base")) fixedPay += r.Dec("amount");
        }
        var monthly = fixedPay < regular * 0.75m ? regular * 0.75m : fixedPay;
        var hourly = monthly / 173m;
        var weighted = approved.Sum(o => o.Data.Dec("weighted_hours"));
        return (weighted, D.R2(hourly), PayrollCalc.Rupiah(weighted * hourly));
    }

    public static async Task CheckNotPaidAsync(Masters m, string? employee, string period, string path, CancellationToken ct)
    {
        if (employee is null) return;
        var paid = (await m.QueryAsync("Payslip", [new FilterClause("employee", "=", employee), new FilterClause("period", "=", period)], ct, take: 10))
            .FirstOrDefault(p => p.DocStatus == DocStatus.Submitted);
        if (paid is not null) throw Errors.One(path, $"Payslip {paid.Name} for {period} is already submitted; this month is locked");
    }
}

/// <summary>Absensi: satu per karyawan per tanggal; jam kerja & keterlambatan dari shift; tautan ke cuti yang disetujui; terkunci setelah payslip bulan itu submit.</summary>
public sealed class AttendanceHook(IDocumentStore store) : IBeforeValidate<Attendance>, IBeforeDelete<Attendance>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await m.RequireAsync("Employee", d.Str("employee"), "employee", "Employee", ct);
        var date = d.Date("attendance_date") ?? throw Errors.One("attendance_date", "The date is required");
        await Overtime.CheckNotPaidAsync(m, emp.Name, $"{date:yyyy-MM}", "attendance_date", ct);
        var dup = (await m.QueryAsync("Attendance", [new FilterClause("employee", "=", emp.Name), new FilterClause("attendance_date", "=", D.Iso(date))], ct, take: 5))
            .FirstOrDefault(x => x.Name != c.Doc.Name);
        if (dup is not null) throw Errors.One("attendance_date", $"{emp.Name} already has attendance {dup.Name} on {D.Iso(date)}");

        d.SetIfEmpty("shift_type", emp.Data.Str("shift_type") ?? (await m.SingleAsync("HRSettings", ct)).Str("default_shift_type"));
        var shift = await m.GetAsync("ShiftType", d.Str("shift_type"), ct);
        if (d.TryGetValue("check_in", out var ci) && ci is not null && DateTime.TryParse(Convert.ToString(ci, System.Globalization.CultureInfo.InvariantCulture), out var inAt))
        {
            if (d.TryGetValue("check_out", out var co) && co is not null && DateTime.TryParse(Convert.ToString(co, System.Globalization.CultureInfo.InvariantCulture), out var outAt) && outAt > inAt)
                d["working_hours"] = Math.Round((decimal)(outAt - inAt).TotalHours - (shift?.Data.Dec("break_minutes") ?? 0) / 60m, 2);
            if (shift?.Data.Str("start_time") is { } st && TimeOnly.TryParse(st, out var start))
            {
                var late = (int)(TimeOnly.FromDateTime(inAt) - start).TotalMinutes - (int)(shift.Data.Dec("late_grace_minutes"));
                d["late_minutes"] = (long)(late > 0 && late < 12 * 60 ? late : 0); // shift malam lintas hari: selisih besar = tidak terlambat
            }
        }

        var leave = (await m.QueryAsync("LeaveApplication", [new FilterClause("employee", "=", emp.Name)], ct, take: 500))
            .FirstOrDefault(l => l.WorkflowState == Approval.Approved && l.Data.Date("from_date") <= date && l.Data.Date("to_date") >= date);
        d["leave_application"] = leave?.Name;
        if (leave is not null && d.Str("status") is "Present" or "Absent")
            c.Messages.Add($"Approved leave {leave.Name} covers this date; consider status 'On leave'");
    }

    public async Task BeforeDeleteAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.Data.Date("attendance_date") is { } date)
            await Overtime.CheckNotPaidAsync(new Masters(store, c.Doc.TenantId), c.Doc.Data.Str("employee"), $"{date:yyyy-MM}", "attendance_date", ct);
    }
}

/// <summary>Pengajuan lembur: jam dibayar dihitung dengan pengali PP 35/2021; batas 4 jam per hari kerja, batas jam hari istirahat, dan 18 jam per minggu.</summary>
public sealed class OvertimeRequestHook(IDocumentStore store) : IBeforeValidate<OvertimeRequest>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await m.RequireAsync("Employee", d.Str("employee"), "employee", "Employee", ct);
        var date = d.Date("overtime_date") ?? throw Errors.One("overtime_date", "The date is required");
        var period = $"{date:yyyy-MM}";
        d["period"] = period;
        if (c.Doc.DocStatus == DocStatus.Draft) await Overtime.CheckNotPaidAsync(m, emp.Name, period, "overtime_date", ct);

        var week = await Overtime.WorkWeekAsync(m, emp, ct);
        d["work_week_days"] = (long)week;
        var dayType = d.Str("day_type") ?? Overtime.WorkingDay;
        if (dayType == Overtime.ShortestDay && week != 6)
            throw Errors.One("day_type", "'Public holiday on the shortest working day' only applies to a 6-day work week");
        var hours = d.Dec("hours");
        var (weighted, max) = Overtime.Weigh(dayType, week, hours);
        if (hours > max) throw Errors.One("hours", $"At most {max:0.#} overtime hours on a {dayType.ToLowerInvariant()} ({week}-day work week)");
        d["weighted_hours"] = weighted;

        // Batas 18 jam per minggu (Senin–Minggu), dihitung dari pengajuan lain yang tidak ditolak/dibatalkan.
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var others = (await m.QueryAsync("OvertimeRequest", [new FilterClause("employee", "=", emp.Name)], ct, take: 1000))
            .Where(o => o.Name != c.Doc.Name && o.DocStatus != DocStatus.Cancelled && o.WorkflowState is not ("Rejected" or "Cancelled")
                        && o.Data.Date("overtime_date") is { } od && od >= monday && od <= monday.AddDays(6))
            .Sum(o => o.Data.Dec("hours"));
        if (others + hours > Overtime.MaxPerWeek)
            throw Errors.One("hours", $"Overtime would total {others + hours:0.#} hours in the week of {D.Iso(monday)}; the limit is {Overtime.MaxPerWeek:0} hours");
    }
}
