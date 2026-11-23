using Apps.Erp;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Talent management suite: competency sync on Appraisal completion, 9-Box Talent Review,
/// Succession Plan candidate validation, 70-20-10 Development Plan, and Training Program/Request workflow.
/// </summary>
sealed partial class Tests
{
    private static async Task<(string ManagerEmp, string StaffEmp, string PositionName, string CycleName)> TalentSetupAsync(
        DocumentService docs, UserContext sys)
    {
        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?> { ["currency_code"] = "IDR", ["currency_name"] = "Indonesian Rupiah" });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?>
        {
            ["company_code"] = "ACME", ["company_name"] = "PT Acme Nusantara", ["base_currency"] = "IDR", ["country"] = "ID",
        });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "DIV-ENG", ["org_unit_name"] = "Engineering", ["unit_type"] = "Division",
            ["company"] = "ACME", ["effective_from"] = "2026-01-01",
        });
        await docs.InsertAsync(sys, "Competency", new Dictionary<string, object?>
        {
            ["competency_code"] = "COMP-LEAD", ["competency_name"] = "Leadership", ["category"] = "Leadership", ["is_active"] = true,
        });
        await docs.InsertAsync(sys, "Competency", new Dictionary<string, object?>
        {
            ["competency_code"] = "COMP-ARCH", ["competency_name"] = "System Architecture", ["category"] = "Technical", ["is_active"] = true,
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?>
        {
            ["job_code"] = "JOB-ENG-LEAD", ["job_title"] = "Engineering Lead", ["job_level"] = "Manager", ["is_manager_job"] = true,
            ["competencies"] = new List<Dictionary<string, object?>>
            {
                new() { ["competency"] = "COMP-LEAD", ["required_level"] = 4, ["is_mandatory"] = true },
            },
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?>
        {
            ["job_code"] = "JOB-SWE", ["job_title"] = "Software Engineer", ["job_level"] = "Staff",
        });
        var pos = await docs.InsertAsync(sys, "Position", new Dictionary<string, object?>
        {
            ["position_title"] = "Engineering Lead",
            ["organization_unit"] = "DIV-ENG", ["job_profile"] = "JOB-ENG-LEAD", ["company"] = "ACME",
        });
        var posDev = await docs.InsertAsync(sys, "Position", new Dictionary<string, object?>
        {
            ["position_title"] = "Software Engineer",
            ["organization_unit"] = "DIV-ENG", ["job_profile"] = "JOB-SWE", ["company"] = "ACME",
        });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-MGR", ["full_name"] = "Budi Santoso", ["gender"] = "Male",
            ["date_of_birth"] = "1985-04-10", ["company"] = "ACME", ["organization_unit"] = "DIV-ENG",
            ["position"] = pos.Name, ["job_profile"] = "JOB-ENG-LEAD", ["hire_date"] = "2020-01-01",
        });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-DEV", ["full_name"] = "Siti Aminah", ["gender"] = "Female",
            ["date_of_birth"] = "1994-07-15", ["company"] = "ACME", ["organization_unit"] = "DIV-ENG",
            ["position"] = posDev.Name, ["job_profile"] = "JOB-SWE", ["reports_to"] = "EMP-MGR", ["hire_date"] = "2022-03-01",
        });
        var cycle = await docs.InsertAsync(sys, "AppraisalCycle", new Dictionary<string, object?>
        {
            ["cycle_name"] = "2026 Annual Review", ["company"] = "ACME",
            ["start_date"] = "2026-01-01", ["end_date"] = "2026-12-31", ["status"] = "In progress",
        });
        return ("EMP-MGR", "EMP-DEV", pos.Name, cycle.Name);
    }

    [Test]
    public async Task Appraisal_scores_competencies_and_syncs_to_employee_on_complete()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var sys = UserContext.System("acme");
        var (mgr, dev, _, cycle) = await TalentSetupAsync(docs, sys);

        var empUser = new UserContext("siti", "Siti Aminah", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = dev });
        var mgrUser = new UserContext("budi", "Budi Santoso", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = mgr });

        var apr = await docs.InsertAsync(empUser, "Appraisal", new Dictionary<string, object?>
        {
            ["employee"] = dev,
            ["appraisal_cycle"] = cycle,
            ["goals"] = new List<Dictionary<string, object?>>
            {
                new() { ["goal"] = "Deliver Careers API", ["weight"] = 100m, ["self_score"] = 4 },
            },
            ["competencies"] = new List<Dictionary<string, object?>>
            {
                new() { ["competency"] = "COMP-ARCH", ["required_level"] = 3, ["self_score"] = 4 },
            },
        });

        await docs.RunActionAsync(empUser, "Appraisal", apr.Name, "Submit self review", apr.Version, null);
        var afterSelf = await docs.GetAsync(sys, "Appraisal", apr.Name);

        // Manager scores both goal and competency, then completes review.
        await docs.UpdateAsync(mgrUser, "Appraisal", apr.Name, afterSelf!.Version, new Dictionary<string, object?>
        {
            ["goals"] = new List<Dictionary<string, object?>>
            {
                new() { ["goal"] = "Deliver Careers API", ["weight"] = 100m, ["self_score"] = 4, ["manager_score"] = 5 },
            },
            ["competencies"] = new List<Dictionary<string, object?>>
            {
                new() { ["competency"] = "COMP-ARCH", ["required_level"] = 3, ["self_score"] = 4, ["manager_score"] = 4 },
            },
        });
        var scored = await docs.GetAsync(sys, "Appraisal", apr.Name);
        Assert.Equal("Outstanding", scored!.Data.Str("rating"), "final score 5 sets Outstanding rating");
        Assert.Equal(4m, scored.Data.Dec("competency_score"), "competency score averages manager scores");

        await docs.RunActionAsync(mgrUser, "Appraisal", apr.Name, "Complete review", scored.Version, null);

        var updatedEmp = await docs.GetAsync(sys, "Employee", dev);
        var empComps = updatedEmp!.Data.Rows("competencies");
        Assert.Equal(1, empComps.Count, "Appraisal completion syncs competency proficiency to Employee");
        Assert.Equal("COMP-ARCH", empComps[0].Str("competency"), "synced competency code matches");
        Assert.Equal(4m, empComps[0].Dec("proficiency_level"), "synced proficiency level matches manager score");
    }

    [Test]
    public async Task TalentReview_and_SuccessionPlan_validate_nine_box_and_candidates()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var sys = UserContext.System("acme");
        var (mgr, dev, pos, cycle) = await TalentSetupAsync(docs, sys);

        var hrTalent = new UserContext("hr-talent", "HR Talent", "acme", new HashSet<string> { ErpRoles.HrTalent }, new Dictionary<string, string>());
        var apr = await docs.InsertAsync(hrTalent, "Appraisal", new Dictionary<string, object?>
        {
            ["employee"] = dev,
            ["appraisal_cycle"] = cycle,
            ["goals"] = new List<Dictionary<string, object?>>
            {
                new() { ["goal"] = "Platform reliability", ["weight"] = 100m, ["self_score"] = 4, ["manager_score"] = 5 },
            },
        });
        await docs.RunActionAsync(hrTalent, "Appraisal", apr.Name, "Submit self review", apr.Version, null);
        var aprSelf = await docs.GetAsync(sys, "Appraisal", apr.Name);
        await docs.RunActionAsync(hrTalent, "Appraisal", apr.Name, "Complete review", aprSelf!.Version, null);

        var review = await docs.InsertAsync(hrTalent, "TalentReview", new Dictionary<string, object?>
        {
            ["employee"] = dev,
            ["appraisal_cycle"] = cycle,
            ["appraisal"] = apr.Name,
            ["potential_level"] = "High",
            ["talent_pool"] = "High Potential (HiPo)",
            ["risk_of_loss"] = "Medium",
            ["impact_of_loss"] = "High",
        });
        Assert.Equal("High", review.Data.Str("performance_level"), "score >= 3.75 maps to High performance");
        Assert.Equal("1A - Star / Future Leader", review.Data.Str("nine_box_position"), "High/High maps to 1A");
        await docs.RunActionAsync(hrTalent, "TalentReview", review.Name, Framework.Metadata.DocActions.Submit, review.Version, null);

        // Incumbent cannot be listed as succession candidate.
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(hrTalent, "SuccessionPlan", new Dictionary<string, object?>
        {
            ["position"] = pos,
            ["incumbent_employee"] = mgr,
            ["candidates"] = new List<Dictionary<string, object?>>
            {
                new() { ["employee"] = mgr, ["readiness"] = "Ready Now", ["ranking"] = 1 },
            },
        }), "incumbent cannot be a succession candidate");

        var plan = await docs.InsertAsync(hrTalent, "SuccessionPlan", new Dictionary<string, object?>
        {
            ["position"] = pos,
            ["incumbent_employee"] = mgr,
            ["criticality"] = "Mission Critical",
            ["candidates"] = new List<Dictionary<string, object?>>
            {
                new() { ["employee"] = dev, ["readiness"] = "1-2 Years", ["ranking"] = 1 },
            },
        });
        var cand = plan.Data.Rows("candidates")[0];
        Assert.Equal("Outstanding", cand.Str("latest_rating"), "candidate rating fetched from latest appraisal");
        Assert.Equal("1A - Star / Future Leader", cand.Str("nine_box_position"), "candidate 9-Box fetched from latest review");
    }

    [Test]
    public async Task DevelopmentPlan_and_TrainingRequest_enforce_progress_and_approval()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var sys = UserContext.System("acme");
        var (mgr, dev, _, cycle) = await TalentSetupAsync(docs, sys);

        var empUser = new UserContext("siti", "Siti Aminah", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = dev });
        var mgrUser = new UserContext("budi", "Budi Santoso", "acme", new HashSet<string> { ErpRoles.Employee }, new Dictionary<string, string> { ["employee"] = mgr });
        var hrTalent = new UserContext("hr-talent", "HR Talent", "acme", new HashSet<string> { ErpRoles.HrTalent }, new Dictionary<string, string>());

        var idp = await docs.InsertAsync(empUser, "DevelopmentPlan", new Dictionary<string, object?>
        {
            ["employee"] = dev,
            ["appraisal_cycle"] = cycle,
            ["mentor"] = mgr,
            ["start_date"] = "2026-02-01",
            ["target_completion_date"] = "2026-11-30",
            ["status"] = "Active",
            ["goals"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["development_area"] = "Lead cross-team architecture",
                    ["competency"] = "COMP-ARCH",
                    ["learning_type"] = "70% On-the-Job / Project",
                    ["action_plan"] = "Lead Q2 API redesign",
                    ["progress_percent"] = 100m,
                    ["status"] = "Completed",
                },
                new()
                {
                    ["development_area"] = "Coaching skills",
                    ["competency"] = "COMP-LEAD",
                    ["learning_type"] = "20% Coaching / Mentoring",
                    ["action_plan"] = "Bi-weekly mentoring with Budi",
                    ["progress_percent"] = 50m,
                    ["status"] = "In Progress",
                },
            },
        });
        Assert.Equal(75m, idp.Data.Dec("overall_progress"), "overall progress averages 100 and 50 to 75");

        // Cannot complete IDP while one goal is still In Progress.
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(hrTalent, "DevelopmentPlan", idp.Name, idp.Version,
            new Dictionary<string, object?> { ["status"] = "Completed" }), "incomplete goals block Completed status");

        // Training Program + Training Request approval workflow.
        await docs.InsertAsync(hrTalent, "TrainingProgram", new Dictionary<string, object?>
        {
            ["program_code"] = "TRN-ARCH-101",
            ["program_name"] = "Distributed Architecture Workshop",
            ["training_type"] = "External seminar",
            ["target_competency"] = "COMP-ARCH",
            ["provider"] = "Acme Academy",
            ["estimated_cost"] = 5000000m,
        });

        var req = await docs.InsertAsync(empUser, "TrainingRequest", new Dictionary<string, object?>
        {
            ["employee"] = dev,
            ["training_program"] = "TRN-ARCH-101",
            ["preferred_date"] = "2026-06-15",
            ["justification"] = "Supports Q2 API redesign IDP goal",
        });
        Assert.Equal(mgr, req.Data.Str("approver"), "approver defaults to reports_to manager");
        Assert.Equal("Distributed Architecture Workshop", req.Data.Str("training_title"), "training title autofilled from program");
        Assert.Equal(5000000m, req.Data.Dec("estimated_cost"), "estimated cost autofilled from program");

        await docs.RunActionAsync(empUser, "TrainingRequest", req.Name, "Submit for approval", req.Version, null);
        var pendingMgr = await docs.GetAsync(sys, "TrainingRequest", req.Name);
        await docs.RunActionAsync(mgrUser, "TrainingRequest", req.Name, "Approve (Manager)", pendingMgr!.Version, null);
        var pendingHr = await docs.GetAsync(sys, "TrainingRequest", req.Name);
        await docs.RunActionAsync(hrTalent, "TrainingRequest", req.Name, "Approve (HR)", pendingHr!.Version, null);
        var approved = await docs.GetAsync(sys, "TrainingRequest", req.Name);
        Assert.Equal("Approved", approved!.WorkflowState, "training request reaches Approved after manager and HR approval");
    }
}
