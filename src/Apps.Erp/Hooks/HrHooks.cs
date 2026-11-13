using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

internal static class Hr
{
    public const string Left = "Left";

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().Date);

    /// <summary>Pemangku aktif sebuah posisi (sumber kebenaran: Employee.position, bukan Position.current_employee).</summary>
    public static async Task<Document?> HolderAsync(Masters m, string? position, string? except, CancellationToken ct)
    {
        if (position is null) return null;
        return (await m.QueryAsync("Employee", [new FilterClause("position", "=", position)], ct, take: 20))
            .FirstOrDefault(e => e.Name != except && e.Data.Str("status") != Left);
    }

    /// <summary>Atasan: pemangku posisi "melapor ke"; bila kosong, pimpinan unit; bila dia sendiri pimpinan, pimpinan unit induk.</summary>
    public static async Task<string?> ManagerForAsync(Masters m, string self, string? position, string? orgUnit, CancellationToken ct)
    {
        var pos = await m.GetAsync("Position", position, ct);
        if (await HolderAsync(m, pos?.Data.Str("reports_to_position"), self, ct) is { } boss) return boss.Name;
        for (var unit = await m.GetAsync("OrganizationUnit", orgUnit, ct); unit is not null; unit = await m.GetAsync("OrganizationUnit", unit.Data.Str("parent_org_unit"), ct))
            if (await HolderAsync(m, unit.Data.Str("manager_position"), self, ct) is { } head) return head.Name;
        return null;
    }

    public static bool Overlaps(DateOnly? aFrom, DateOnly? aTo, DateOnly? bFrom, DateOnly? bTo) =>
        (aFrom ?? DateOnly.MinValue) <= (bTo ?? DateOnly.MaxValue) && (bFrom ?? DateOnly.MinValue) <= (aTo ?? DateOnly.MaxValue);
}

/// <summary>
/// Karyawan. Model Posisi: unit, profil jabatan, grade, lokasi, cost center diturunkan dari posisi; satu pemangku aktif per posisi.
/// Atasan dari hierarki posisi/unit; approver cuti & klaim default = atasan. Keluar: tanggal keluar dari Employee Separation, posisi dikosongkan.
/// Perubahan posisi/status disinkronkan ke Position lewat event (Position menghitung pemangkunya sendiri).
/// </summary>
public sealed class EmployeeHook(IDocumentStore store) : IBeforeValidate<Employee>, IAfterSave<Employee>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var me = d.Str("employee_number") ?? c.Doc.Name;
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var prev = c.Previous?.Data;

        if (d.Str("status") == Hr.Left)
        {
            if (d.Str("separation_date") is null)
            {
                var sep = (await m.QueryAsync("EmployeeSeparation", [new FilterClause("employee", "=", me)], ct, take: 10))
                    .FirstOrDefault(s => s.DocStatus == DocStatus.Submitted);
                if (sep is null && !c.User.IsAdmin) errors.Add("status", "The Left status is applied through Employee Separation");
                d["separation_date"] = sep?.Data.Str("last_working_date");
            }
            d["position"] = null;
        }
        else if (prev?.Str("status") == Hr.Left) d["separation_date"] = null;

        var positionChanged = prev?.Str("position") != d.Str("position");
        if (d.Str("position") is { } positionName)
        {
            var pos = await m.RequireAsync("Position", positionName, "position", "Position", ct);
            if (pos.Data.Str("status") is "Closed" or "Frozen") errors.Add("position", $"Position {pos.Name} is {pos.Data.Str("status")}");
            if (await Hr.HolderAsync(m, pos.Name, me, ct) is { } holder) errors.Add("position", $"Position {pos.Name} is already filled by {holder.Name}");
            d["organization_unit"] = pos.Data.Str("organization_unit");
            d["job_profile"] = pos.Data.Str("job_profile");
            if (positionChanged) { d["grade"] = pos.Data.Str("grade"); d["cost_center"] = pos.Data.Str("cost_center"); d["location"] = pos.Data.Str("location"); }
        }
        d.SetIfEmpty("grade", await m.StrAsync("JobProfile", d.Str("job_profile"), "default_grade", ct));
        d.SetIfEmpty("cost_center", await m.StrAsync("OrganizationUnit", d.Str("organization_unit"), "cost_center", ct) ?? company.Default("default_cost_center"));
        d.SetIfEmpty("location", await m.StrAsync("OrganizationUnit", d.Str("organization_unit"), "location", ct));

        var oldBoss = prev?.Str("reports_to");
        if (d.Str("reports_to") is null || (positionChanged && d.Str("reports_to") == oldBoss))
            d["reports_to"] = await Hr.ManagerForAsync(m, me, d.Str("position"), d.Str("organization_unit"), ct);
        foreach (var approver in new[] { "leave_approver", "expense_approver" })
            if (d.Str(approver) is null || (oldBoss is not null && d.Str(approver) == oldBoss && d.Str("reports_to") != oldBoss))
                d[approver] = d.Str("reports_to");

        var hs = await m.SingleAsync("HRSettings", ct);
        d.SetIfEmpty("holiday_list", hs.Str("default_holiday_list") ?? company.Default("default_holiday_list"));
        d.SetIfEmpty("shift_type", hs.Str("default_shift_type"));
        d.SetIfEmpty("leave_policy", hs.Str("default_leave_policy"));
        if (d.Date("probation_end_date") is { } pe && d.Date("hire_date") is { } hd && pe < hd)
            errors.Add("probation_end_date", "The probation end date is before the date of joining");
        if (d.Str("reports_to") == me) errors.Add("reports_to", "An employee cannot report to themselves");
        errors.ThrowIfAny();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var oldPos = c.Previous?.Data.Str("position"); var newPos = c.Doc.Data.Str("position");
        if (oldPos != newPos || c.Previous?.Data.Str("status") != c.Doc.Data.Str("status"))
            ErpEvents.Emit(c, ErpEvents.PositionSync, new() { ["old_position"] = oldPos, ["new_position"] = newPos });
        // Karyawan baru tanpa akun: handler membuat akun user bila HRSettings.auto_create_user dicentang.
        if (c.Previous is null && c.Doc.Data.Str("user_id") is null && c.Doc.Data.Str("work_email") is not null)
            ErpEvents.Emit(c, ErpEvents.EmployeeUser, new());
        return Task.CompletedTask;
    }
}

/// <summary>Posisi: pemangku & status dihitung dari Employee; grade/cost center default dari profil jabatan/unit.</summary>
public sealed class PositionHook(IDocumentStore store) : IBeforeValidate<Position>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var job = await m.RequireAsync("JobProfile", d.Str("job_profile"), "job_profile", "Job profile", ct);
        d.SetIfEmpty("grade", job.Data.Str("default_grade"));
        d.SetIfEmpty("cost_center", await m.StrAsync("OrganizationUnit", d.Str("organization_unit"), "cost_center", ct));
        if (c.Previous is null && job.Data.Bool("is_manager_job")) d["is_manager_position"] = true;

        var holder = await Hr.HolderAsync(m, c.Doc.Name, null, ct);
        d["current_employee"] = holder?.Name;
        var status = d.Str("status");
        if (holder is not null && status is "Closed" or "Frozen")
            throw Errors.One("status", $"The position is still filled by {holder.Name}; move the employee first");
        if (holder is not null) d["status"] = "Filled";
        else if (status is null or "Filled") d["status"] = "Vacant";
    }
}

/// <summary>Unit organisasi: pergantian posisi pimpinan memicu pembaruan atasan karyawan unit (lewat event).</summary>
public sealed class OrganizationUnitHook : IAfterSave<OrganizationUnit>
{
    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var oldPos = c.Previous?.Data.Str("manager_position"); var newPos = c.Doc.Data.Str("manager_position");
        if (c.Previous is not null && oldPos != newPos)
            ErpEvents.Emit(c, ErpEvents.OrgManagerChanged, new() { ["old_position"] = oldPos, ["new_position"] = newPos });
        return Task.CompletedTask;
    }
}

/// <summary>Perubahan jabatan (Workday Change Job): diterapkan ke Employee saat tanggal efektif (langsung bila sudah lewat, else job harian).</summary>
public sealed class JobChangeHook(IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<JobChange>, IAfterSave<JobChange>, IOnCancel<JobChange>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        if (d.Date("effective_date") is { } eff && emp.Data.Date("hire_date") is { } hire && eff < hire)
            errors.Add("effective_date", "The effective date is before the employee's date of joining");
        if (d.Str("new_position") is { } np)
        {
            var pos = await m.RequireAsync("Position", np, "new_position", "Position", ct);
            if (pos.Data.Str("status") is "Closed" or "Frozen") errors.Add("new_position", $"Position {np} is {pos.Data.Str("status")}");
            if (await Hr.HolderAsync(m, np, emp.Name, ct) is { } holder) errors.Add("new_position", $"Position {np} is still filled by {holder.Name}");
            d.SetIfEmpty("new_organization_unit", pos.Data.Str("organization_unit"));
            d.SetIfEmpty("new_job_profile", pos.Data.Str("job_profile"));
            d.SetIfEmpty("new_grade", pos.Data.Str("grade"));
        }
        errors.ThrowIfAny();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft
            && c.Doc.Data.Date("effective_date") <= Hr.Today(clock))
            ErpEvents.Emit(c, ErpEvents.JobChangeApply, new());
        return Task.CompletedTask;
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var emp = await new Masters(store, c.Doc.TenantId).GetAsync("Employee", c.Doc.Data.Str("employee"), ct);
        if (emp?.Data.Str("last_job_change") == c.Doc.Name)
            throw Errors.One("name", "This change has already been applied to the employee; create a new Job Change to reverse it");
    }
}

/// <summary>Pemberhentian (Workday Terminate): checklist default; diterapkan pada hari kerja terakhir (Employee → Keluar, user dinonaktifkan).</summary>
public sealed class EmployeeSeparationHook(IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<EmployeeSeparation>, IAfterSave<EmployeeSeparation>, IOnCancel<EmployeeSeparation>
{
    private static readonly (string Task, string Who)[] DefaultChecklist =
    [
        ("Work handover", "Manager"), ("Return company assets (laptop, access card, vehicle)", "General Affairs"),
        ("Revoke system & email access", "IT"), ("Compute final pay & deductions, settle advances", "Finance"),
        ("Exit interview & employment certificate", "HR"), ("Report to BPJS Employment & Health", "HR"),
    ];

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        if (d.Date("last_working_date") is { } last && emp.Data.Date("hire_date") is { } hire && last < hire)
            throw Errors.One("last_working_date", "The last working date is before the date of joining");
        var other = (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name)], ct, take: 10))
            .FirstOrDefault(s => s.Name != c.Doc.Name && s.DocStatus == DocStatus.Submitted);
        if (other is not null) throw Errors.One("employee", $"Separation {other.Name} already exists");
        if (d.Rows("checklist").Count == 0)
            d["checklist"] = DefaultChecklist.Select(x => new Dictionary<string, object?> { ["task"] = x.Task, ["responsible"] = x.Who, ["is_done"] = false }).ToList();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft
            && c.Doc.Data.Date("last_working_date") <= Hr.Today(clock))
            ErpEvents.Emit(c, ErpEvents.SeparationApply, new());
        return Task.CompletedTask;
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var emp = await new Masters(store, c.Doc.TenantId).GetAsync("Employee", c.Doc.Data.Str("employee"), ct);
        if (emp?.Data.Str("status") == Hr.Left)
            throw Errors.One("name", "The separation has already been applied; reactivate the employee through HR (status) before cancelling");
    }
}

/// <summary>Kontrak: tidak tumpang tindih; total PKWT termasuk perpanjangan maksimal 60 bulan (PP 35/2021, verifikasi); status dihitung saat dibuka.</summary>
public sealed class EmploymentContractHook(IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<EmploymentContract>, IOnLoad<EmploymentContract>
{
    public const int MaxPkwtMonths = 60;

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var start = d.Date("start_date"); var end = d.Date("end_date");
        foreach (var o in (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name)], ct, take: 100))
                 .Where(o => o.Name != c.Doc.Name && o.DocStatus == DocStatus.Submitted))
            if (Hr.Overlaps(start, end, o.Data.Date("start_date"), o.Data.Date("end_date")))
                throw Errors.One("start_date", $"Overlaps with contract {o.Name}");

        if (d.Str("contract_type") == "PKWT" && start is not null && end is not null)
        {
            var months = AssetBook.MonthsBetween(start.Value, end.Value.AddDays(1));
            for (var prev = await m.GetAsync(c.Doc.DocType, d.Str("renewal_of"), ct); prev is not null; prev = await m.GetAsync(c.Doc.DocType, prev.Data.Str("renewal_of"), ct))
                if (prev.Data.Str("contract_type") == "PKWT" && prev.Data.Date("start_date") is { } ps && prev.Data.Date("end_date") is { } pe)
                    months += AssetBook.MonthsBetween(ps, pe.AddDays(1));
            if (months > MaxPkwtMonths) throw Errors.One("end_date", $"Total fixed-term (PKWT) duration including renewals is {months} months, exceeding {MaxPkwtMonths} months");
        }
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted) return;
        var renewed = (await new Masters(store, c.Doc.TenantId).QueryAsync(c.Doc.DocType, [new FilterClause("renewal_of", "=", c.Doc.Name)], ct, take: 5))
            .Any(x => x.DocStatus == DocStatus.Submitted);
        c.Doc.Data["status"] = renewed ? "Renewed" : c.Doc.Data.Date("end_date") is { } e && e < Hr.Today(clock) ? "Expired" : "Active";
    }
}

public sealed class LeaveAllocationHook(IDocumentStore store) : IBeforeValidate<LeaveAllocation>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var other = (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name), new FilterClause("leave_type", "=", d.Str("leave_type"))], ct, take: 100))
            .FirstOrDefault(o => o.Name != c.Doc.Name && o.DocStatus != DocStatus.Cancelled
                && Hr.Overlaps(d.Date("from_date"), d.Date("to_date"), o.Data.Date("from_date"), o.Data.Date("to_date")));
        if (other is not null) throw Errors.One("from_date", $"Overlaps with allocation {other.Name}");
    }
}

/// <summary>Perhitungan cuti: hari kerja (libur mingguan & kalender libur), saldo alokasi, tumpang tindih.</summary>
internal static class LeaveMath
{
    public static async Task<HashSet<DayOfWeek>> WeeklyOffAsync(Masters m, Document emp, Document? holidayList, CancellationToken ct)
    {
        var shift = await m.GetAsync("ShiftType", emp.Data.Str("shift_type"), ct);
        var rule = shift?.Data.Str("working_days_per_week") switch
        {
            "6" => "Sunday",
            "5" => "Saturday & Sunday",
            _ => holidayList?.Data.Str("weekly_off") ?? "Saturday & Sunday",
        };
        return rule switch
        {
            "Sunday" => [DayOfWeek.Sunday],
            "None" => [],
            _ => [DayOfWeek.Saturday, DayOfWeek.Sunday],
        };
    }

    public static async Task<decimal> WorkingDaysAsync(Masters m, Document emp, DateOnly from, DateOnly to, bool includeHolidays, CancellationToken ct)
    {
        var hl = await m.GetAsync("HolidayList", emp.Data.Str("holiday_list"), ct);
        var off = await WeeklyOffAsync(m, emp, hl, ct);
        var holidays = hl?.Data.Rows("holidays").Select(h => h.Date("holiday_date")).OfType<DateOnly>().ToHashSet() ?? [];
        decimal days = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
            if (includeHolidays || (!off.Contains(day.DayOfWeek) && !holidays.Contains(day))) days++;
        return days;
    }

    public static bool Active(Document a) => a.DocStatus != DocStatus.Cancelled && a.WorkflowState is not ("Rejected" or "Cancelled");
}

/// <summary>Pengajuan cuti: dihitung & divalidasi saat simpan dan sekali lagi saat diajukan (saldo bisa berubah di antaranya).</summary>
public sealed class LeaveApplicationHook(IDocumentStore store)
    : IBeforeValidate<LeaveApplication>, IBeforeTransition<LeaveApplication>
{
    private async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var type = await m.RequireAsync("LeaveType", d.Str("leave_type"), "leave_type", "Leave type", ct);
        var from = d.Date("from_date"); var to = d.Date("to_date");
        if (from is null || to is null || to < from) return; // dicek metadata

        var gender = type.Data.Str("applicable_gender");
        if (gender is "Female" or "Male" && emp.Data.Str("gender") != gender) errors.Add("leave_type", $"{type.Name} is only for {gender.ToLowerInvariant()} employees");
        var days = await LeaveMath.WorkingDaysAsync(m, emp, from.Value, to.Value, type.Data.Bool("include_holidays"), ct);
        if (d.Bool("half_day")) days = Math.Min(days, 0.5m);
        if (days <= 0) errors.Add("from_date", "There are no working days in this date range");
        var max = type.Data.Dec("max_consecutive_days");
        if (max > 0 && days > max) errors.Add("to_date", $"At most {max:0.#} consecutive days for {type.Name}");
        d["total_days"] = days;

        var mine = (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name)], ct, take: 1000))
            .Where(a => a.Name != c.Doc.Name && LeaveMath.Active(a)).ToList();
        if (mine.FirstOrDefault(a => Hr.Overlaps(from, to, a.Data.Date("from_date"), a.Data.Date("to_date"))) is { } clash)
            errors.Add("from_date", $"Overlaps with application {clash.Name}");

        if (type.Data.Bool("is_annual_quota"))
        {
            var alloc = (await m.QueryAsync("LeaveAllocation", [new FilterClause("employee", "=", emp.Name), new FilterClause("leave_type", "=", type.Name)], ct, take: 50))
                .FirstOrDefault(a => a.DocStatus == DocStatus.Submitted && a.Data.Date("from_date") <= from && to <= a.Data.Date("to_date"));
            if (alloc is null) errors.Add("leave_type", $"There is no {type.Name} allocation for this period");
            else
            {
                var aFrom = alloc.Data.Date("from_date"); var aTo = alloc.Data.Date("to_date");
                var used = mine.Where(a => a.DocStatus == DocStatus.Submitted && a.Data.Str("leave_type") == type.Name
                                           && Hr.Overlaps(aFrom, aTo, a.Data.Date("from_date"), a.Data.Date("to_date")))
                    .Sum(a => a.Data.Dec("total_days"));
                var balance = alloc.Data.Dec("total_leaves") - used;
                d["balance_before"] = balance;
                if (days > balance) errors.Add("to_date", $"Only {balance:0.#} days of {type.Name} remain");
            }
        }
        errors.ThrowIfAny();
    }

    public Task BeforeValidateAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);

    public Task BeforeTransitionAsync(HookContext c, CancellationToken ct) =>
        c.ToState == "Pending Approval" || c.ToState == "Approved" ? CheckAsync(c, ct) : Task.CompletedTask;
}

/// <summary>Penerapan event HR ke Employee, dipakai handler event (langsung) dan job harian (tanggal efektif di masa depan).</summary>
internal static class HrApply
{
    public static async Task JobChangeAsync(SystemDocs sys, Document jc, CancellationToken ct)
    {
        var d = jc.Data;
        if (jc.DocStatus != DocStatus.Submitted || d.Str("employee") is not { } empName) return;
        var emp = await sys.TryGetAsync("Employee", empName, ct);
        if (emp is null) return;
        var eff = d.Date("effective_date");
        var lastDate = emp.Data.Date("last_job_change_date"); var lastName = emp.Data.Str("last_job_change");
        // Idempoten & berurutan: jangan terapkan ulang, jangan biarkan perubahan lama menimpa yang lebih baru.
        if (lastName == jc.Name || (lastDate is not null && eff is not null && (eff < lastDate || (eff == lastDate && string.CompareOrdinal(jc.Name, lastName) < 0))))
            return;
        var patch = new Dictionary<string, object?> { ["last_job_change"] = jc.Name, ["last_job_change_date"] = d.Str("effective_date") };
        foreach (var f in new[] { "organization_unit", "position", "job_profile", "grade", "location", "cost_center", "reports_to" })
            if (d.Str($"new_{f}") is { } v) patch[f] = v;
        if (d.Str("new_employment_type") is { } et) patch["employment_type"] = et;
        await sys.UpdateAsync("Employee", empName, patch, ct);
    }

    public static async Task SeparationAsync(SystemDocs sys, IUserAccountRepository? accounts, Document sep, CancellationToken ct)
    {
        if (sep.DocStatus != DocStatus.Submitted || sep.Data.Str("employee") is not { } empName) return;
        var emp = await sys.TryGetAsync("Employee", empName, ct);
        if (emp is null) return;
        if (emp.Data.Str("status") != Hr.Left)
            await sys.UpdateAsync("Employee", empName, new Dictionary<string, object?> { ["status"] = Hr.Left }, ct);
        if (accounts is not null && emp.Data.Str("user_id") is { } userId && await accounts.FindByIdAsync(userId, ct) is { IsActive: true } account)
        {
            account.IsActive = false;
            await accounts.UpdateAsync(account, ct);
        }
    }

    /// <summary>
    /// Akun user untuk karyawan baru (HRSettings.auto_create_user). Username & id = email kantor (huruf kecil), role Employee,
    /// atribut employee = nomor induk, department = unit organisasi. Tanpa password: karyawan memakai alur lupa password /
    /// reset oleh admin. Email yang sudah punya akun di tenant yang sama ditautkan saja (bila belum terhubung ke karyawan lain).
    /// Mengembalikan id akun, atau null bila tidak bisa dibuat/ditautkan.
    /// </summary>
    public static async Task<string?> EnsureUserAsync(IServiceProvider services, string tenant, Document emp, string email, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetService<IUserAccountRepository>();
        if (repo is null) return null;
        var userName = email.Trim().ToLowerInvariant();
        var normalizedEmail = userName.ToUpperInvariant();
        if (await repo.FindByNormalizedEmailAsync(normalizedEmail, ct) is { } existing)
        {
            if (existing.TenantId != tenant) return null; // email dipakai tenant lain: jangan ditautkan
            if (existing.EmployeeId is null)
            {
                existing.EmployeeId = emp.Name;
                existing.Department ??= emp.Data.Str("organization_unit");
                await repo.UpdateAsync(existing, ct);
            }
            return existing.EmployeeId == emp.Name ? existing.Id : null;
        }

        var account = new UserAccount
        {
            Id = userName, UserName = userName, Email = userName, FullName = emp.Data.Str("full_name") ?? emp.Name, TenantId = tenant,
            Department = emp.Data.Str("organization_unit"), EmployeeId = emp.Name, Roles = ["Employee"], CreatedAt = DateTimeOffset.UtcNow,
        };
        if (scope.ServiceProvider.GetService<UserManager<UserAccount>>() is { } manager)
        {
            var result = await manager.CreateAsync(account); // normalisasi & validasi Identity; tanpa password
            if (!result.Succeeded)
                throw new InvalidOperationException($"Creating the user for {emp.Name} failed: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
        else
        {
            account.NormalizedUserName = normalizedEmail;
            account.NormalizedEmail = normalizedEmail;
            await repo.CreateAsync(account, ct);
        }
        return account.Id;
    }
}
