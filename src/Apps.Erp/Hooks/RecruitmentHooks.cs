using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class JobRequisition { }
public sealed class JobApplicant { }
public sealed class JobOffer { }

/// <summary>Requisition: data jabatan dari posisi (harus Vacant saat draft); status, jumlah pelamar & yang diterima dihitung saat dibuka.</summary>
public sealed class JobRequisitionHook(IDocumentStore store) : IBeforeValidate<JobRequisition>, IOnLoad<JobRequisition>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("Position", d.Str("position"), ct) is { } pos)
        {
            if (c.Doc.DocStatus == DocStatus.Draft && pos.Data.Str("status") != "Vacant")
                throw Errors.One("position", $"Position {pos.Name} is {pos.Data.Str("status")}, not Vacant");
            d.SetIfEmpty("job_profile", pos.Data.Str("job_profile"));
            d.SetIfEmpty("organization_unit", pos.Data.Str("organization_unit"));
            d.SetIfEmpty("grade", pos.Data.Str("grade"));
            d.SetIfEmpty("company", pos.Data.Str("company"));
        }
        if (await m.GetAsync("Grade", d.Str("grade"), ct) is { } grade && d.Dec("salary_max") > 0)
        {
            var max = grade.Data.Rows("pay_ranges").Where(r => r.Str("company") is null || r.Str("company") == d.Str("company")).Select(r => r.Dec("max_salary")).DefaultIfEmpty(0).Max();
            if (max > 0 && d.Dec("salary_max") > max) c.Messages.Add($"The salary range exceeds the maximum of grade {grade.Name} ({max:N0})");
        }
        if (c.Doc.DocStatus == DocStatus.Draft) d["requisition_status"] = "Draft";
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var applicants = await new Masters(store, c.Doc.TenantId).QueryAsync("JobApplicant", [new FilterClause("job_requisition", "=", c.Doc.Name)], ct, take: 5000);
        var hired = applicants.Count(a => a.Data.Str("status") == "Hired");
        d["applicant_count"] = (long)applicants.Count;
        d["hired_count"] = (long)hired;
        d["requisition_status"] = c.Doc.DocStatus switch
        {
            DocStatus.Draft => "Draft",
            DocStatus.Cancelled => "Cancelled",
            _ => hired >= d.Dec("openings") ? "Filled" : "Open",
        };
    }
}

/// <summary>Pelamar: requisition harus terbuka; email unik per requisition; status Hired hanya lewat aksi 'Create employee'.</summary>
public sealed class JobApplicantHook(IDocumentStore store) : IBeforeValidate<JobApplicant>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var req = await m.RequireAsync("JobRequisition", d.Str("job_requisition"), "job_requisition", "Job requisition", ct);
        if (c.Previous is null && req.DocStatus != DocStatus.Submitted) throw Errors.One("job_requisition", $"{req.Name} is not open");
        var email = d.Str("email")?.Trim().ToLowerInvariant();
        d["email"] = email;
        var dup = (await m.QueryAsync("JobApplicant", [new FilterClause("job_requisition", "=", req.Name), new FilterClause("email", "=", email)], ct, take: 5))
            .FirstOrDefault(x => x.Name != c.Doc.Name);
        if (dup is not null) throw Errors.One("email", $"{email} already applied to {req.Name} ({dup.Name})");
        if (d.Str("status") == "Hired" && d.Str("employee") is null && !c.User.IsAdmin)
            throw Errors.One("status", "'Hired' is set by 'Create employee' on an accepted job offer");
    }
}

/// <summary>Penawaran: data jabatan dari requisition; satu penawaran aktif per pelamar; karyawan hasilnya dibaca saat dibuka.</summary>
public sealed class JobOfferHook(IDocumentStore store) : IBeforeValidate<JobOffer>, IOnLoad<JobOffer>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var applicant = await m.RequireAsync("JobApplicant", d.Str("job_applicant"), "job_applicant", "Applicant", ct);
        if (c.Doc.DocStatus == DocStatus.Draft && applicant.Data.Str("status") is "Rejected" or "Withdrawn" or "Hired")
            throw Errors.One("job_applicant", $"{applicant.Name} is {applicant.Data.Str("status")}");
        if (await m.GetAsync("JobRequisition", applicant.Data.Str("job_requisition"), ct) is { } req)
        {
            d.SetIfEmpty("position", req.Data.Str("position"));
            d.SetIfEmpty("job_profile", req.Data.Str("job_profile"));
            d.SetIfEmpty("organization_unit", req.Data.Str("organization_unit"));
            d.SetIfEmpty("grade", req.Data.Str("grade"));
            d.SetIfEmpty("employment_type", req.Data.Str("employment_type"));
            d.SetIfEmpty("proposed_start_date", req.Data.Str("target_start_date"));
        }
        var other = (await m.QueryAsync("JobOffer", [new FilterClause("job_applicant", "=", applicant.Name)], ct, take: 20))
            .FirstOrDefault(o => o.Name != c.Doc.Name && o.DocStatus != DocStatus.Cancelled && o.WorkflowState is not ("Declined" or "Expired" or "Cancelled"));
        if (other is not null) throw Errors.One("job_applicant", $"{applicant.Name} already has an active offer {other.Name}");
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct) =>
        c.Doc.Data["employee"] = await new Masters(store, c.Doc.TenantId).StrAsync("JobApplicant", c.Doc.Data.Str("job_applicant"), "employee", ct);
}

/// <summary>
/// Aksi "Create employee" (Workday Hire): dari penawaran Accepted dibuat Employee (data pelamar + jabatan penawaran), lalu Salary Assignment
/// gaji pokok bila komponennya diatur di Payroll Settings, dan pelamar ditandai Hired. Employee dibuat sebagai user HR (izin berlaku).
/// </summary>
public sealed class JobOfferCreateEmployeeAction(IDocumentStore store) : IFormAction<JobOffer>
{
    public string ActionId => "erp.joboffer.create_employee";
    public string Label => "Create employee";
    public string Icon => "user-plus";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc) => doc.WorkflowState == "Accepted" && (user.HasRole("HR") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var m = new Masters(store, ctx.Doc.TenantId);
        var o = ctx.Doc.Data;
        var applicant = await m.GetAsync("JobApplicant", o.Str("job_applicant"), ct);
        if (applicant is null) return FormActionResult.Fail("The applicant no longer exists");
        if (applicant.Data.Str("employee") is { } already) return FormActionResult.NavigateTo($"/app/Employee/{already}", $"Already hired as {already}");
        var number = o.Str("employee_number");
        if (number is null) return FormActionResult.Fail("The offer has no employee number");
        var a = applicant.Data;
        if (a.Str("gender") is null || a.Str("date_of_birth") is null) return FormActionResult.Fail($"Fill gender and date of birth on applicant {applicant.Name} first");
        var req = await m.GetAsync("JobRequisition", a.Str("job_requisition"), ct);

        try
        {
            var emp = await ctx.Docs.InsertAsync(ctx.User, "Employee", new Dictionary<string, object?>
            {
                ["employee_number"] = number, ["full_name"] = a.Str("applicant_name"), ["gender"] = a.Str("gender"), ["date_of_birth"] = a.Str("date_of_birth"),
                ["personal_email"] = a.Str("email"), ["mobile"] = a.Str("mobile"),
                ["company"] = req?.Data.Str("company"), ["organization_unit"] = o.Str("organization_unit"), ["position"] = o.Str("position"),
                ["job_profile"] = o.Str("job_profile"), ["grade"] = o.Str("grade"), ["employment_type"] = o.Str("employment_type"),
                ["hire_date"] = o.Str("proposed_start_date"), ["status"] = "Active",
            }, $"Hired from {ctx.Doc.Name}", ct);

            var note = "";
            if ((await m.SingleAsync("PayrollSettings", ct)).Str("base_salary_component") is { } baseComponent && o.Dec("base_salary") > 0)
            {
                var sa = await ctx.Docs.InsertAsync(ctx.User, "SalaryAssignment", new Dictionary<string, object?>
                {
                    ["employee"] = emp.Name, ["from_date"] = o.Str("proposed_start_date"), ["is_active"] = true,
                    ["earnings"] = new List<Dictionary<string, object?>> { new() { ["salary_component"] = baseComponent, ["amount"] = o.Dec("base_salary") } },
                }, $"From {ctx.Doc.Name}", ct);
                note = $", salary assignment {sa.Name}";
            }
            else note = " (no salary assignment: set 'Base salary component' in Payroll Settings)";

            var sys = UserContext.System(ctx.Doc.TenantId);
            var fresh = await ctx.Docs.GetAsync(sys, "JobApplicant", applicant.Name, ct);
            await ctx.Docs.UpdateAsync(sys, "JobApplicant", applicant.Name, fresh.Version,
                new Dictionary<string, object?> { ["status"] = "Hired", ["employee"] = emp.Name }, ct);
            return FormActionResult.NavigateTo($"/app/Employee/{emp.Name}", $"Employee {emp.Name} created{note}");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
        {
            return FormActionResult.Fail($"Creating the employee failed: {ex.Message}");
        }
    }
}
