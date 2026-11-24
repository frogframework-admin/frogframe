using Apps.Erp;
using Apps.Erp.Hooks;
using Apps.Erp.Reports;
using Framework.Actions;
using Framework.Documents;
using Framework.Files;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Fixes from the HRM review of 24 Nov 2026: applicant retention, offer needs an interview, requisition approval rules,
/// probation evaluation rules, job change with salary only, warning letters, announcements, calibration and 9-box.
/// </summary>
sealed partial class Tests
{
    private static ScheduledJobContext JobContext(IServiceProvider sp, DocumentService docs, DateTimeOffset at) => new()
    {
        TenantId = "acme", SystemUser = UserContext.System("acme"), Docs = docs, Services = sp, ScheduledTime = at,
    };

    [Test]
    public async Task Recruitment_review_fixes_retention_offer_guard_and_requisition_rules()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, store, m) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var req = await SubmittedRequisitionAsync(docs, sys, "Analyst", openings: 2);
        Assert.Equal("Approved", req.WorkflowState, "requisition approved through the workflow");

        var fileStore = sp.GetRequiredService<IFileStore>();
        var cv = await sp.GetRequiredService<FileService>().UploadFieldFileAsync(sys, "JobApplicant", "resume", "cv.pdf", Pdf());
        var app = await docs.InsertAsync(sys, "JobApplicant", new Dictionary<string, object?>
        {
            ["applicant_name"] = "Rina Kartika", ["email"] = "rina@example.com", ["mobile"] = "+62 811 000 111",
            ["job_requisition"] = req.Name, ["resume"] = cv, ["notes"] = "Asked for remote work", ["date_of_birth"] = "1996-05-01",
        });

        // An offer needs a passed interview with feedback (unless HR Settings allows otherwise).
        Dictionary<string, object?> Offer() => new()
        {
            ["job_applicant"] = app.Name, ["proposed_start_date"] = "2026-12-01", ["base_salary"] = 8_000_000m, ["employee_number"] = "EMP-NEW-1",
        };
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(sys, "JobOffer", Offer()), "no interview → no offer");
        var iv = await docs.InsertAsync(sys, "Interview", new Dictionary<string, object?>
        {
            ["job_applicant"] = app.Name, ["round"] = "User", ["scheduled_at"] = "2026-11-20T10:00:00", ["interviewer"] = "MGR-A",
            ["result"] = "Pass", ["feedback"] = "Strong analytical skills; mentioned family situation",
        });
        var offer = await docs.InsertAsync(sys, "JobOffer", Offer());
        Assert.True(offer.Name.Length > 0, "offer allowed after a passed interview with feedback");

        // Retention: a rejected applicant past the retention period is anonymized, the CV deleted, interview feedback cleared.
        var other = await docs.InsertAsync(sys, "JobApplicant", new Dictionary<string, object?>
        {
            ["applicant_name"] = "Doni Saputra", ["email"] = "doni@example.com", ["mobile"] = "+62 812 222 333",
            ["job_requisition"] = req.Name, ["resume"] = await sp.GetRequiredService<FileService>().UploadFieldFileAsync(sys, "JobApplicant", "resume", "cv2.pdf", Pdf()),
        });
        var otherCv = other.Data.Str("resume")!;
        var ivOther = await docs.InsertAsync(sys, "Interview", new Dictionary<string, object?>
        {
            ["job_applicant"] = other.Name, ["round"] = "HR screening", ["scheduled_at"] = "2026-11-18T09:00:00", ["interviewer"] = "MGR-A",
            ["result"] = "Fail", ["feedback"] = "Personal remarks about the candidate",
        });
        other = await docs.UpdateAsync(sys, "JobApplicant", other.Name, other.Version,
            new Dictionary<string, object?> { ["status"] = "Rejected", ["rejection_reason"] = "Salary expectation too high" });
        Assert.True(other.Data.Rows("stage_history").Any(r => r.Str("stage") == "Rejected"), "stage history records the rejection");

        var job = new ApplicantRetentionJob();
        await job.ExecuteAsync(JobContext(sp, docs, DateTimeOffset.UtcNow), default);
        Assert.Equal("Doni Saputra", (await docs.GetAsync(sys, "JobApplicant", other.Name)).Data.Str("applicant_name"), "not yet past the retention period");

        await job.ExecuteAsync(JobContext(sp, docs, DateTimeOffset.UtcNow.AddMonths(13)), default);
        var anon = await docs.GetAsync(sys, "JobApplicant", other.Name);
        Assert.Equal(ApplicantRetentionJob.AnonymizedName, anon.Data.Str("applicant_name"), "name removed");
        Assert.True(anon.Data.Str("mobile") is null && anon.Data.Str("resume") is null && anon.Data.Str("anonymized_at") is not null, "contact and CV link removed, time recorded");
        Assert.True(anon.Data.Str("email")!.EndsWith("@redacted.invalid"), "e-mail replaced");
        Assert.True(anon.Data.Rows("stage_history").All(r => r.Str("notes") is null) && anon.Data.Rows("stage_history").Any(r => r.Str("stage") == "Rejected"),
            "stage dates kept without notes");
        Assert.Equal(null, await fileStore.GetInfoAsync("acme", otherCv), "CV file deleted");
        Assert.Equal(null, (await docs.GetAsync(sys, "Interview", ivOther.Name)).Data.Str("feedback"), "interview feedback cleared");
        Assert.Equal("Rina Kartika", (await docs.GetAsync(sys, "JobApplicant", app.Name)).Data.Str("applicant_name"), "active applicant untouched");
        Assert.True((await docs.GetAsync(sys, "Interview", iv.Name)).Data.Str("feedback") is not null, "active applicant's interview untouched");
        var version = anon.Version;
        await job.ExecuteAsync(JobContext(sp, docs, DateTimeOffset.UtcNow.AddMonths(14)), default);
        Assert.Equal(version, (await docs.GetAsync(sys, "JobApplicant", other.Name)).Version, "anonymized once");

        // Requisition: while pending, a manager cannot change it; HR can.
        var mgr = new UserContext("mgr-a", "MGR-A", "acme", new HashSet<string> { "Manager", ErpRoles.Employee },
            new Dictionary<string, string> { ["employee"] = "MGR-A", ["department"] = "DEPT-A" });
        var draft = await docs.InsertAsync(mgr, "JobRequisition", new Dictionary<string, object?>
        {
            ["job_title"] = "Clerk", ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD",
            ["openings"] = 1, ["hiring_manager"] = "MGR-A",
        });
        var pending = await docs.RunActionAsync(mgr, "JobRequisition", draft.Name, "Submit for approval", draft.Version, null);
        Assert.Equal("Pending Unit Head", pending.WorkflowState, "waiting for the unit head");
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(mgr, "JobRequisition", pending.Name, pending.Version,
            new Dictionary<string, object?> { ["openings"] = 2 }), "pending requisition is locked for the manager");
        var unchanged = await docs.UpdateAsync(mgr, "JobRequisition", pending.Name, pending.Version,
            new Dictionary<string, object?> { ["openings"] = 1 });
        Assert.Equal(1m, unchanged.Data.Dec("openings"), "saving the same value is allowed");

        // Another employee cannot reject a requisition that is not theirs to approve.
        var stranger = new UserContext("pm-b", "PM-B", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = "PM-B" });
        await Assert.Throws<Exception>(() => docs.RunActionAsync(stranger, "JobRequisition", pending.Name, "Reject", unchanged.Version, "no"),
            "only the unit head or HR may reject");
    }

    [Test]
    public async Task Hr_review_fixes_probation_job_change_warning_and_announcements()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, store, m) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var mgrA = new UserContext("mgr-a", "MGR-A", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = "MGR-A" });
        var pmB = new UserContext("pm-b", "PM-B", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = "PM-B" });

        // Probation: only the direct manager (or HR) evaluates.
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(pmB, "ProbationEvaluation", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["reviewer"] = "PM-B", ["outcome"] = "Failed",
        }), "another employee cannot make themselves reviewer");
        var eval = await docs.InsertAsync(mgrA, "ProbationEvaluation", new Dictionary<string, object?> { ["employee"] = "STAFF-A", ["outcome"] = "Failed" });
        Assert.Equal("MGR-A", eval.Data.Str("reviewer"), "reviewer defaults to the direct manager");
        await docs.RunActionAsync(mgrA, "ProbationEvaluation", eval.Name, DocActions.Submit, eval.Version, null);
        var seps = await m.QueryAsync("EmployeeSeparation", [new FilterClause("employee", "=", "STAFF-A")], default);
        Assert.Equal(1, seps.Count, "failed probation creates a draft separation");
        Assert.Equal("PHK", seps[0].Data.Str("separation_type"), "with a valid separation type");

        // Job change with only a salary adjustment is accepted (rule has_change).
        var jc = await docs.InsertAsync(sys, "JobChange", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-B", ["change_reason"] = "Grade adjustment", ["effective_date"] = "2026-12-01",
            ["adjust_salary"] = true, ["new_base_salary"] = 9_500_000m,
        });
        Assert.True(jc.Name.Length > 0, "salary-only job change saved");
        var url = AppraisalCreateJobChangeAction.PrefillUrl(new Document { Name = "APR-1", DocType = "Appraisal", TenantId = "acme", Owner = "system" }, "STAFF-B", new DateOnly(2026, 11, 24));
        Assert.True(url.StartsWith("/app/JobChange/new?prefill.employee=STAFF-B") && url.Contains("prefill.change_reason=Promotion"), "propose action opens a prefilled form");

        // Warning letters: default validity and escalation message.
        var sp2 = await docs.InsertAsync(sys, "DisciplinaryAction", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["warning_level"] = "SP2", ["issued_date"] = "2026-11-24", ["description"] = "Repeated absence",
        });
        Assert.Equal("2027-05-23", sp2.Data.Str("valid_until"), "SP valid 6 months by default");
        Assert.Equal("Draft", sp2.Data.Str("warning_status"), "draft until submitted");
        var submitted = await docs.RunActionAsync(sys, "DisciplinaryAction", sp2.Name, DocActions.Submit, sp2.Version, null);
        Assert.Equal("Active", DisciplinaryActionHook.Status(submitted, new DateOnly(2027, 1, 1)), "active inside the validity");
        Assert.Equal("Expired", DisciplinaryActionHook.Status(submitted, new DateOnly(2027, 6, 1)), "expired after it");

        // Announcements: company, unit and sub-units come from the Employee record; mandatory read is recorded once.
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "DEPT-A-1", ["org_unit_name"] = "Team A1", ["company"] = "ACME", ["unit_type"] = "Team",
            ["parent_org_unit"] = "DEPT-A", ["staffing_model"] = "Job", ["status"] = "Active", ["effective_from"] = "2026-01-01",
        });
        var staffA = await docs.GetAsync(sys, "Employee", "STAFF-A");
        await docs.UpdateAsync(sys, "Employee", "STAFF-A", staffA.Version, new Dictionary<string, object?> { ["organization_unit"] = "DEPT-A-1" });
        var today = Hr.Today(TimeProvider.System);
        var forDept = await docs.InsertAsync(sys, "Announcement", new Dictionary<string, object?>
        {
            ["title"] = "New attendance policy", ["body"] = "Read and confirm.", ["target_unit"] = "DEPT-A", ["require_acknowledgment"] = true,
            ["publish_from"] = D.Iso(today),
        });
        var onlyDept = await docs.InsertAsync(sys, "Announcement", new Dictionary<string, object?>
        {
            ["title"] = "Dept A lunch", ["body"] = "Friday.", ["target_unit"] = "DEPT-A", ["include_sub_units"] = false, ["publish_from"] = D.Iso(today),
        });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?>
        {
            ["company_code"] = "OTHERCO", ["company_name"] = "Other Company",
        });
        await docs.InsertAsync(sys, "Announcement", new Dictionary<string, object?>
        {
            ["title"] = "Other company", ["body"] = "-", ["target_company"] = "OTHERCO", ["publish_from"] = D.Iso(today),
        });
        var visible = await Announcements.VisibleAsync(m, "STAFF-A", today, default);
        Assert.True(visible.Any(a => a.Name == forDept.Name), "parent-unit announcement reaches the sub-unit");
        Assert.True(visible.All(a => a.Name != onlyDept.Name), "without sub-units it does not");
        Assert.True(visible.All(a => a.Data.Str("title") != "Other company"), "other company's announcement hidden");

        var staffUser = new UserContext("staff-a", "STAFF-A", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = "STAFF-A" });
        var ack = new AnnouncementAcknowledgeAction(TimeProvider.System);
        var annDoc = await docs.GetAsync(staffUser, "Announcement", forDept.Name);
        Assert.True(ack.IsVisible(staffUser, annDoc), "acknowledge button shown");
        var ctx = new FormActionContext { User = staffUser, Meta = default!, Doc = annDoc, Docs = docs, Services = sp };
        Assert.True((await ack.ExecuteAsync(ctx, default)).Success, "acknowledged");
        var after = await docs.GetAsync(sys, "Announcement", forDept.Name);
        Assert.Equal(1m, after.Data.Dec("ack_count"), "one acknowledgment");
        Assert.True(!ack.IsVisible(staffUser, after), "button hidden afterwards");
        var hr = new UserContext("hr", "HR", "acme", new HashSet<string> { ErpRoles.HrEmployeeManagement }, new Dictionary<string, string>());
        var edited = await docs.UpdateAsync(hr, "Announcement", after.Name, after.Version, new Dictionary<string, object?>
        {
            ["body"] = "Updated text.", ["acknowledgments"] = new List<Dictionary<string, object?>>(),
        });
        Assert.Equal(1, edited.Data.Rows("acknowledgments").Count, "HR cannot remove acknowledgments by editing");
    }

    [Test]
    public async Task Talent_review_fixes_calibration_session_and_nine_box_report()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var sys = UserContext.System("acme");
        var (mgr, dev, _, cycle) = await TalentSetupAsync(docs, sys);
        var hrTalent = new UserContext("hr-talent", "HR Talent", "acme", new HashSet<string> { ErpRoles.HrTalent }, new Dictionary<string, string>());

        var otherCycle = await docs.InsertAsync(sys, "AppraisalCycle", new Dictionary<string, object?>
        {
            ["cycle_name"] = "2025 Annual Review", ["company"] = "ACME", ["start_date"] = "2025-01-01", ["end_date"] = "2025-12-31", ["status"] = "Closed",
        });
        var session = await docs.InsertAsync(hrTalent, "CalibrationSession", new Dictionary<string, object?>
        {
            ["session_name"] = "Engineering 2026", ["appraisal_cycle"] = cycle, ["organization_unit"] = "DIV-ENG",
        });
        var wrongCycle = await docs.InsertAsync(hrTalent, "CalibrationSession", new Dictionary<string, object?>
        {
            ["session_name"] = "Old", ["appraisal_cycle"] = otherCycle.Name,
        });
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(hrTalent, "TalentReview", new Dictionary<string, object?>
        {
            ["employee"] = dev, ["appraisal_cycle"] = cycle, ["calibration_session"] = wrongCycle.Name, ["performance_level"] = "High", ["potential_level"] = "High",
        }), "session of another cycle rejected");

        var review = await docs.InsertAsync(hrTalent, "TalentReview", new Dictionary<string, object?>
        {
            ["employee"] = dev, ["appraisal_cycle"] = cycle, ["calibration_session"] = session.Name, ["performance_level"] = "High", ["potential_level"] = "Moderate",
        });
        await docs.RunActionAsync(hrTalent, "CalibrationSession", session.Name, DocActions.Submit, session.Version, null);
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(hrTalent, "TalentReview", review.Name, review.Version,
            new Dictionary<string, object?> { ["potential_level"] = "High" }), "finalized session locks the levels");
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(hrTalent, "TalentReview", new Dictionary<string, object?>
        {
            ["employee"] = mgr, ["appraisal_cycle"] = cycle, ["calibration_session"] = session.Name, ["performance_level"] = "Moderate", ["potential_level"] = "Moderate",
        }), "no new reviews in a finalized session");

        await docs.RunActionAsync(hrTalent, "TalentReview", review.Name, DocActions.Submit, review.Version, null);
        var report = provider.GetServices<IReportProvider>().Single(r => r.Name == "erp-nine-box");
        var result = await report.RunAsync(hrTalent, new Dictionary<string, string?> { ["appraisal_cycle"] = cycle, ["organization_unit"] = "DIV-ENG" }, docs, default);
        Assert.Equal(9, result.Rows.Count, "one row per box");
        var star = result.Rows.Single(r => (string?)r["nine_box_position"] == "2A - High Performer");
        Assert.Equal(1, Convert.ToInt32(star["employees"]), "the reviewed employee is a High Performer");
    }
}
