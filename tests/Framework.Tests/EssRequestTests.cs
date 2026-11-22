using Apps.Erp;
using Apps.Erp.Ess;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Files;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Jobs;
using Framework.Web;
using Framework.Web.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// ESS self-service requests: face registration (encrypted template, HR verification, one active registration, face
/// match on punches), overtime, attendance correction (approver → HR → attendance updated) and biodata change
/// (HR → employee updated).
/// </summary>
sealed partial class Tests
{
    /// <summary>Users are built on demand so role groups loaded by the test setup are expanded.</summary>
    private sealed class FixedUserDirectory(Func<UserContext[]> build) : IUserDirectory
    {
        private UserContext[] users => build();
        public Task<UserContext?> FindAsync(string userId, CancellationToken ct = default) => Task.FromResult(users.FirstOrDefault(u => u.Id == userId));
        public Task<IReadOnlyList<UserContext>> ListAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserContext>>(users.Where(u => u.TenantId == tenantId).ToList());
    }

    private static ServiceProvider BuildEssRequestProvider(Action<IServiceCollection>? extra = null)
    {
        var erpMetadataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Apps.Erp", "Metadata"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddFrogFramework(o => o.LoadFrom(erpMetadataPath));
        services.AddFrogFrameworkIdentity();
        services.AddErpHooks();
        services.TryAddSingleton<FileService>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMemoryCache();
        services.TryAddSingleton<IEssPunchStore, InMemoryEssPunchStore>();
        services.TryAddSingleton<EssAttendanceService>();
        services.TryAddSingleton<EssRequestService>();
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static async Task DrainErpOutboxAsync(IServiceProvider sp)
    {
        var processor = new OutboxProcessor(sp.GetRequiredService<IOutboxStore>(), sp.GetServices<IEventHandler>(), TimeProvider.System);
        for (var i = 0; i < 20 && (await processor.ProcessDueAsync()).Processed > 0; i++) { }
    }

    private static double[] Vector(double seed) => Enumerable.Range(0, 128).Select(i => Math.Round(Math.Sin(seed + i) * 0.5, 6)).ToArray();

    [Test]
    public async Task Ess_face_registration_is_encrypted_verified_by_HR_and_replaces_the_previous_one()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, store, m) = await MatrixSetupAsync(sp);
        var svc = sp.GetRequiredService<EssRequestService>();
        var staff = MatrixUser("STAFF-A");
        var hr = MatrixUser("HR-USER", "HR", "Employee");
        EssFaceRegistrationInput Input(double seed, bool consent = true) => new()
        {
            Photos = [(TinyPng, "front.png"), (TinyPng, "left.png")], Template = Vector(seed), Consent = consent,
            ModelName = "MobileFaceNet", ModelVersion = "1.2", QualityScore = 0.93m, LivenessScore = 0.97m, DeviceId = "dev-1",
        };

        await Assert.Throws<ValidationFailedException>(() => svc.RegisterFaceAsync(staff, "STAFF-A", Input(1, consent: false), default), "consent required");
        await Assert.Throws<ValidationFailedException>(() => svc.RegisterFaceAsync(staff, "STAFF-A", Input(1) with { Template = [0.1, 0.2] }, default), "template too short");

        var first = await svc.RegisterFaceAsync(staff, "STAFF-A", Input(1), default);
        Assert.Equal("Pending Verification", first.WorkflowState, "waits for HR");
        Assert.True(first.Data.Dec("template_dimensions") == 128 && first.Data.Str("template_sha256")?.Length == 64, "fingerprint computed from the decrypted template");
        var raw = await sp.GetRequiredService<IFileStore>().GetAsync("acme", first.Data.Str("template_file")!);
        Assert.True(raw is { } r && !System.Text.Encoding.UTF8.GetString(r.Content).Contains("0.4", StringComparison.Ordinal), "template stored encrypted");
        Assert.True(await svc.ActiveTemplateAsync(staff, "STAFF-A", default) is null, "not usable before verification");

        await Assert.Throws<Exception>(() => docs.RunActionAsync(staff, "FaceRegistration", first.Name, "Verify", first.Version, null), "the employee cannot verify their own face");
        first = await docs.RunActionAsync(hr, "FaceRegistration", first.Name, "Verify", first.Version, null);
        Assert.True(first.WorkflowState == "Active" && first.Data.Bool("is_active") && first.Data.Str("verified_by") is not null, "verified");
        await DrainErpOutboxAsync(sp);
        var t1 = await svc.ActiveTemplateAsync(staff, "STAFF-A", default);
        Assert.True(t1 is { } a1 && a1.Template.SequenceEqual(Vector(1)), "the app gets the verified template back");

        var second = await svc.RegisterFaceAsync(staff, "STAFF-A", Input(2), default);
        second = await docs.RunActionAsync(hr, "FaceRegistration", second.Name, "Verify", second.Version, null);
        await DrainErpOutboxAsync(sp);
        Assert.Equal("Revoked", (await docs.GetAsync(hr, "FaceRegistration", first.Name)).WorkflowState, "previous registration revoked");
        Assert.True((await svc.ActiveTemplateAsync(staff, "STAFF-A", default))?.Registration.Name == second.Name, "one active registration");

        // A template file not encrypted by the ESS API is refused (e.g. uploaded through the desk form).
        var fake = new FileRecord { Id = "f_fake", TenantId = "acme", UploadedBy = hr.Id, ContentType = "application/octet-stream", Size = 5, Sha256 = "x", CreatedAt = DateTimeOffset.UtcNow };
        await sp.GetRequiredService<IFileStore>().SaveAsync(fake, "[1,2]"u8.ToArray());
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(hr, "FaceRegistration", new Dictionary<string, object?>
            { ["employee"] = "STAFF-B", ["consent"] = true, ["template_file"] = "f_fake" }), "unencrypted template refused");

        // Punch evaluation with face match required.
        var profile = new EssEmployeeProfile { Employee = "STAFF-A", FullName = "STAFF-A", AttendanceMode = EssAttendanceMode.Anywhere, FaceRegistration = second.Name };
        var policy = new EssAttendancePolicy { BindDevice = false, RequireFace = true, MinFaceScore = 0.8m };
        EssPunchEvaluation Eval(EssFaceMatch? face, EssEmployeeProfile? p = null, EssAttendancePolicy? pol = null) => EssAttendanceRules.Evaluate(
            new EssPunchRequest { Id = Guid.NewGuid(), Type = "in", Latitude = -6.2m, Longitude = 106.8m, Face = face },
            "in", p ?? profile, [], pol ?? policy, null, DateTimeOffset.UtcNow, FieldTypeSettings.DefaultTimeZone);
        Assert.Equal("accepted", Eval(new EssFaceMatch(0.91m, second.Name, "1.2", 0.95m)).Status, "matching face");
        Assert.True(Eval(new EssFaceMatch(0.5m, second.Name, null, null)) is { Status: "flagged" } e1 && e1.Reasons.Contains(EssReason.FaceMismatch), "low score flagged");
        Assert.Equal("rejected", Eval(new EssFaceMatch(0.5m, second.Name, null, null), pol: policy with { RejectFaceMismatch = true }).Status, "or rejected when configured");
        Assert.True(Eval(null).Reasons.Contains(EssReason.FaceMissing), "no face match sent");
        Assert.True(Eval(new EssFaceMatch(0.95m, first.Name, null, null)).Reasons.Contains(EssReason.FaceTemplateOutdated), "old template on the phone");
        Assert.True(Eval(null, profile with { FaceRegistration = null }).Reasons.Contains(EssReason.FaceNotRegistered), "no verified registration");
        Assert.Equal("accepted", Eval(null, pol: policy with { RequireFace = false }).Status, "face not required");
    }

    [Test]
    public async Task Ess_overtime_and_attendance_correction_follow_their_approvals_and_update_attendance()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, m) = await MatrixSetupAsync(sp);
        var svc = sp.GetRequiredService<EssRequestService>();
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var hr = MatrixUser("HR-USER", "HR", "Employee");
        var day = MatrixToday.AddDays(-3);

        var ot = await svc.CreateOvertimeAsync(staff, "STAFF-A", new EssOvertimeRequest(D.Iso(day), 2, null, null, "Month-end closing", true), default);
        Assert.True(ot.WorkflowState == "Pending Approval" && ot.Data.Str("approver") == "MGR-A", "overtime submitted to the line approver");
        Assert.Equal("Cancelled", (await svc.WithdrawAsync(staff, "OvertimeRequest", "employee", "STAFF-A", ot.Name, default)).WorkflowState, "withdrawn");
        await Assert.Throws<DocumentNotFoundException>(() => svc.WithdrawAsync(MatrixUser("STAFF-B"), "OvertimeRequest", "employee", "STAFF-B", ot.Name, default), "not someone else's");

        await Assert.Throws<ValidationFailedException>(() => svc.CreateCorrectionAsync(staff, "STAFF-A",
            new EssCorrectionRequest(D.Iso(MatrixToday.AddDays(2)), "Missing clock-in", $"{D.Iso(MatrixToday.AddDays(2))}T08:00", null, "x", null, true), default), "no future corrections");
        await Assert.Throws<ValidationFailedException>(() => svc.CreateCorrectionAsync(staff, "STAFF-A",
            new EssCorrectionRequest(D.Iso(day), "Missing clock-in", $"{D.Iso(day.AddDays(3))}T08:00", null, "x", null, true), default), "time on another day");

        var corr = await svc.CreateCorrectionAsync(staff, "STAFF-A",
            new EssCorrectionRequest(D.Iso(day), "Missing both", $"{D.Iso(day)}T08:05", $"{D.Iso(day)}T17:10", "Phone battery empty", null, true), default);
        Assert.True(corr.WorkflowState == "Pending Approval" && corr.Data.Str("approver") == "MGR-A", "to the line approver first");
        await Assert.Throws<ValidationFailedException>(() => svc.CreateCorrectionAsync(staff, "STAFF-A",
            new EssCorrectionRequest(D.Iso(day), "Missing clock-in", $"{D.Iso(day)}T08:00", null, "again", null, true), default), "one open correction per day");

        corr = await docs.RunActionAsync(manager, "AttendanceCorrection", corr.Name, "Approve", corr.Version, null);
        Assert.Equal("Pending HR Approval", corr.WorkflowState, "then HR");
        corr = await docs.RunActionAsync(hr, "AttendanceCorrection", corr.Name, "Approve (HR)", corr.Version, null);
        Assert.Equal("Approved", corr.WorkflowState, "approved");
        await DrainErpOutboxAsync(sp);

        var att = (await m.QueryAsync("Attendance", [new("employee", "=", "STAFF-A"), new("attendance_date", "=", D.Iso(day))], default)).Single();
        Assert.Equal("08:05", AttendanceHook.LocalTime(att.Data.GetValueOrDefault("check_in"))?.ToString("HH:mm"), "check-in applied in local time");
        Assert.Equal("17:10", AttendanceHook.LocalTime(att.Data.GetValueOrDefault("check_out"))?.ToString("HH:mm"), "check-out applied");
        Assert.True(att.Data.Str("remarks")?.Contains($"[{corr.Name}]") == true, "remarks name the correction");
        await DrainErpOutboxAsync(sp); // replay-safe
        Assert.Equal(1, (await m.QueryAsync("Attendance", [new("employee", "=", "STAFF-A"), new("attendance_date", "=", D.Iso(day))], default)).Count, "applied once");
    }

    [Test]
    public async Task Ess_biodata_change_only_allowed_fields_and_HR_approval_updates_the_employee()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, m) = await MatrixSetupAsync(sp);
        var svc = sp.GetRequiredService<EssRequestService>();
        var staff = MatrixUser("STAFF-A");
        var hr = MatrixUser("HR-USER", "HR", "Employee");
        await docs.InsertAsync(UserContext.System("acme"), "Bank", new Dictionary<string, object?> { ["bank_code"] = "BCA", ["bank_name"] = "Bank Central Asia" });
        System.Text.Json.JsonElement J(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

        await Assert.Throws<ValidationFailedException>(() => svc.CreateBiodataChangeAsync(staff, "STAFF-A",
            new EssBiodataRequest("Contact & address", new() { ["grade"] = J("\"G9\"") }, null, null, true), default), "field outside the category");
        await Assert.Throws<ValidationFailedException>(() => svc.CreateBiodataChangeAsync(staff, "STAFF-A",
            new EssBiodataRequest("Marital status & dependents", new() { ["ptkp_status"] = J("\"X/9\"") }, null, null, true), default), "option checked");
        await Assert.Throws<ValidationFailedException>(() => svc.CreateBiodataChangeAsync(staff, "STAFF-A",
            new EssBiodataRequest("Bank account", new() { ["bank_accounts"] = J("[{\"bank\":\"BCA\",\"account_no\":\"123\",\"account_holder\":\"STAFF-A\"}]") }, null, null, true), default),
            "bank account change needs a supporting document");

        var contact = await svc.CreateBiodataChangeAsync(staff, "STAFF-A", new EssBiodataRequest("Contact & address",
            new() { ["mobile"] = J("\"+628123456789\""), ["current_address"] = J("\"Jl. Melati 5, Bandung\"") }, "Moved", null, true), default);
        Assert.Equal("Pending HR Approval", contact.WorkflowState, "to HR");
        Assert.True(contact.Data.Rows("changes").All(r => r.Str("label") is { Length: > 0 }), "labels from the employee DocType");

        var proof = await svc.UploadAsync(staff, "BiodataChangeRequest", "attachment", "buku-tabungan.pdf", new MemoryStream("%PDF-1.4 test"u8.ToArray()), default);
        var bank = await svc.CreateBiodataChangeAsync(staff, "STAFF-A", new EssBiodataRequest("Bank account",
            new() { ["bank_accounts"] = J("[{\"bank\":\"BCA\",\"account_no\":\"8800123456\",\"account_holder\":\"STAFF-A\",\"is_default\":true}]") }, "New account", proof, true), default);
        await Assert.Throws<DocumentNotFoundException>(() => svc.UploadAsync(staff, "Employee", "photo", "x.png", new MemoryStream(TinyPng), default), "uploads only for request DocTypes");

        foreach (var req in new[] { contact, bank })
            await docs.RunActionAsync(hr, "BiodataChangeRequest", req.Name, "Approve (HR)", req.Version, null);
        await DrainErpOutboxAsync(sp);

        var fresh = await new Masters(sp.GetRequiredService<IDocumentStore>(), "acme").GetAsync("Employee", "STAFF-A", default);
        Assert.Equal("+628123456789", fresh!.Data.Str("mobile"), "mobile updated");
        Assert.Equal("Jl. Melati 5, Bandung", fresh.Data.Str("current_address"), "address updated");
        var account = fresh.Data.Rows("bank_accounts").Single();
        Assert.True(account.Str("account_no") == "8800123456" && account.Str("bank") == "BCA", "bank account table replaced");

        var current = await svc.BiodataAsync(staff, "STAFF-A", default);
        Assert.True(System.Text.Json.JsonSerializer.Serialize(current).Contains("8800123456"), "current values returned to the app");
    }

    [Test]
    public async Task Ess_approval_inbox_serves_supervisor_and_HR_stages_and_maps_decisions_to_workflow_actions()
    {
        var directory = new FixedUserDirectory(() => [MatrixUser("STAFF-A"), MatrixUser("MGR-A"), MatrixUser("HR-USER", "HR", "Employee")]);
        await using var sp = BuildEssRequestProvider(s => s.Replace(ServiceDescriptor.Singleton<IUserDirectory>(directory)));
        var notifications = sp.GetRequiredService<INotificationStore>();
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var svc = sp.GetRequiredService<EssRequestService>();
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var hr = MatrixUser("HR-USER", "HR", "Employee");
        var day = MatrixToday.AddDays(-3);
        System.Text.Json.JsonElement J(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

        var leaveDay = MatrixToday.AddDays(20);
        while (leaveDay.DayOfWeek != DayOfWeek.Monday) leaveDay = leaveDay.AddDays(1);
        var leave = await LeaveForAsync(docs, "STAFF-A", leaveDay, leaveDay, approve: false);
        var ot = await svc.CreateOvertimeAsync(staff, "STAFF-A", new EssOvertimeRequest(D.Iso(day), 2, null, null, "Closing", true), default);
        var corr = await svc.CreateCorrectionAsync(staff, "STAFF-A", new EssCorrectionRequest(D.Iso(day), "Missing clock-out", null, $"{D.Iso(day)}T17:00", "Battery", null, true), default);
        var bio = await svc.CreateBiodataChangeAsync(staff, "STAFF-A", new EssBiodataRequest("Contact & address", new() { ["mobile"] = J("\"+62811\"") }, null, null, true), default);
        var face = await svc.RegisterFaceAsync(staff, "STAFF-A", new EssFaceRegistrationInput { Photos = [(TinyPng, "f.png")], Template = Vector(3), Consent = true }, default);

        Assert.True(notifications.ForUser("acme", "u-MGR-A").Any(n => n.DocName == corr.Name), "supervisor notified of the correction");
        Assert.True(notifications.ForUser("acme", "u-HR-USER").Any(n => n.DocName == face.Name) && notifications.ForUser("acme", "u-HR-USER").Any(n => n.DocName == bio.Name),
            "HR notified of the face registration and the biodata change");
        Assert.True(notifications.ForUser("acme", "u-HR-USER").All(n => n.DocName != leave.Name), "HR not notified of leave waiting for the supervisor");

        string Names(IEnumerable<EssApproval> items) => string.Join(",", items.Select(i => $"{i.Doc.DocType}:{i.Stage}").Order());
        Assert.Equal("AttendanceCorrection:supervisor,LeaveApplication:supervisor,OvertimeRequest:supervisor",
            Names(await svc.ApprovalInboxAsync(manager, false, default)), "supervisor inbox: leave, overtime, correction");
        Assert.Equal("BiodataChangeRequest:hr,FaceRegistration:hr", Names(await svc.ApprovalInboxAsync(hr, false, default)),
            "HR inbox: HR stages only, overrides of pending leave/overtime left out");
        var withOverrides = await svc.ApprovalInboxAsync(hr, true, default);
        Assert.True(withOverrides.Any(i => i.Doc.Name == leave.Name && i.IsOverride && i.Stage == "hr"), "overrides on request");
        Assert.Equal(0, (await svc.ApprovalInboxAsync(staff, true, default)).Count, "nothing to decide on one's own requests");
        await Assert.Throws<PermissionDeniedException>(() => svc.DecideAsync(staff, "LeaveApplication", leave.Name, "approve", null, null, default), "requester cannot approve");

        Assert.Equal("Pending HR Approval", (await svc.DecideAsync(manager, "AttendanceCorrection", corr.Name, "approve", null, null, default)).WorkflowState, "supervisor stage");
        Assert.True((await svc.ApprovalInboxAsync(hr, false, default)).Any(i => i.Doc.Name == corr.Name && i.Stage == "hr"), "correction moves to the HR inbox");
        Assert.Equal("Approved", (await svc.DecideAsync(hr, "AttendanceCorrection", corr.Name, "approve", null, null, default)).WorkflowState, "HR stage (Approve (HR))");
        Assert.Equal("Active", (await svc.DecideAsync(hr, "FaceRegistration", face.Name, "approve", null, null, default)).WorkflowState, "approve = Verify");
        await Assert.Throws<ValidationFailedException>(() => svc.DecideAsync(hr, "BiodataChangeRequest", bio.Name, "reject", null, " ", default), "reason required to reject");
        Assert.Equal("Rejected", (await svc.DecideAsync(hr, "BiodataChangeRequest", bio.Name, "reject", null, "Number incomplete", default)).WorkflowState, "rejected with reason");
        Assert.Equal("Approved", (await svc.DecideAsync(manager, "OvertimeRequest", ot.Name, "approve", null, null, default)).WorkflowState, "overtime approved by the line approver");
        await Assert.Throws<ValidationFailedException>(() => svc.DecideAsync(hr, "LeaveApplication", leave.Name, "approve", "Approve (HR)", null, default), "HR override needs a comment");
        await Assert.Throws<PermissionDeniedException>(() => svc.DecideAsync(hr, "LeaveApplication", leave.Name, "approve", "Approve", null, default), "HR cannot use the supervisor's action");
        Assert.Equal("Approved", (await svc.DecideAsync(hr, "LeaveApplication", leave.Name, "approve", "Approve (HR)", "Supervisor on leave", default)).WorkflowState, "explicit HR override");
        await Assert.Throws<DocumentNotFoundException>(() => svc.DecideAsync(hr, "Employee", "STAFF-A", "approve", null, null, default), "only request DocTypes");
        Assert.True(notifications.ForUser("acme", "u-STAFF-A").Any(n => n.DocName == bio.Name && n.Subject.Contains("rejected")), "requester told about the decision");
    }

    [Test]
    public async Task Ess_review_panels_gather_evidence_for_the_approver()
    {
        await using var sp = BuildEssRequestProvider(s => s.AddSingleton<EssReviewService>());
        var (docs, _, m) = await MatrixSetupAsync(sp);
        var svc = sp.GetRequiredService<EssRequestService>();
        var reviews = sp.GetRequiredService<EssReviewService>();
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var hr = MatrixUser("HR-USER", "HR", "Employee");
        var sys = new UserContext("system", "System", "acme", new HashSet<string> { UserContext.SystemManager }, new Dictionary<string, string>());
        var day = MatrixToday.AddDays(-3);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);

        // Attendance correction: late clock-in recorded, clock-out refused by the app (outside the geofence).
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?>
        {
            ["shift_name"] = "Office 08-17", ["start_time"] = "08:00:00", ["end_time"] = "17:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 8,
            ["working_days_per_week"] = "5", ["late_grace_minutes"] = 10, ["early_out_grace_minutes"] = 5, ["is_night_shift"] = false,
        });
        var staffEmp = await docs.GetAsync(sys, "Employee", "STAFF-A");
        await docs.UpdateAsync(sys, "Employee", "STAFF-A", staffEmp.Version, new Dictionary<string, object?> { ["shift_type"] = "Office 08-17" });
        await docs.InsertAsync(sys, "Attendance", new Dictionary<string, object?> { ["employee"] = "STAFF-A", ["attendance_date"] = D.Iso(day),
            ["status"] = "Present", ["source"] = "Manual", ["check_in"] = $"{D.Iso(day)}T08:20:00" });
        var tz = Framework.Metadata.FieldTypeSettings.DefaultTimeZone;
        var outAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(17, 4)), tz.GetUtcOffset(day.ToDateTime(new TimeOnly(17, 4))));
        await sp.GetRequiredService<IEssPunchStore>().InsertAsync(new EssPunch { Id = Guid.NewGuid(), TenantId = "acme", Employee = "STAFF-A", UserId = "u-STAFF-A",
            PunchType = "out", Status = "rejected", Reasons = ["outside_geofence"], ServerTime = outAt, PunchTime = outAt, WorkDate = day, DistanceM = 640 });
        var corr = await svc.CreateCorrectionAsync(staff, "STAFF-A",
            new EssCorrectionRequest(D.Iso(day), "Missing clock-out", null, $"{D.Iso(day)}T17:05", "Clock-out refused at the gate", null, true), default);
        var cr = await reviews.CorrectionAsync(manager, corr.Name);
        Assert.Equal("08:20", cr.Before.CheckIn?.ToString("HH:mm"), "recorded check-in");
        Assert.True(cr.ShiftType == "Office 08-17" && cr.ScheduledStart?.ToString("HH:mm") == "08:00", "the day's shift");
        Assert.Equal(AttendanceCodes.NoClockOut, cr.Before.Code, "recorded: no clock-out");
        Assert.True(cr.After is { Code: AttendanceCodes.LateIn, LateMinutes: 10, EarlyOutMinutes: 0, Hours: 7.75m }, "corrected: late in, 10 minutes after the grace, 7.75 h");
        Assert.True(cr.Before.CheckOut is null, "no recorded check-out");
        Assert.True(cr.AfterIsPreview, "preview while pending");
        Assert.Equal("17:05", cr.After?.CheckOut?.ToString("HH:mm"), "preview has the requested check-out");
        Assert.True(cr.After?.Hours > 0, "preview computes working hours");
        Assert.True(cr.Punches.Count == 1 && cr.Punches[0].Status == "rejected", "the day's punches, refused ones included");
        Assert.True(cr.Checks.Any(c => c.Text.Contains("refused or flagged")), "refused punch pointed out");
        Assert.True(cr.Status.WaitingFor?.Contains("MGR-A") == true, "waiting for the supervisor");
        var att = (await m.QueryAsync("Attendance", [new("employee", "=", "STAFF-A"), new("attendance_date", "=", D.Iso(day))], default)).Single();
        Assert.True(att.Data.GetValueOrDefault("check_out") is null, "preview leaves the attendance alone");
        await Assert.Throws<DocumentNotFoundException>(() => reviews.CorrectionAsync(MatrixUser("STAFF-B"), corr.Name), "only readers of the request");

        // Face registration: the second one shows the active registration it replaces.
        EssFaceRegistrationInput Face(double seed) => new() { Photos = [(TinyPng, "a.png"), (TinyPng, "b.png"), (TinyPng, "c.png")], Template = Vector(seed),
            Consent = true, ModelName = "MobileFaceNet", QualityScore = 0.91m, LivenessScore = 0.95m, DeviceId = "dev-9" };
        var first = await svc.RegisterFaceAsync(staff, "STAFF-A", Face(1), default);
        await docs.RunActionAsync(hr, "FaceRegistration", first.Name, "Verify", first.Version, null);
        var second = await svc.RegisterFaceAsync(staff, "STAFF-A", Face(2), default);
        var fr = await reviews.FaceAsync(hr, second.Name);
        Assert.Equal(3, fr.Photos.Count, "three photos");
        Assert.True(fr.Photos.All(u => u.StartsWith("/files/")), "photos served by /files");
        Assert.Equal(first.Name, fr.Previous?.Name, "replaces the active registration");
        Assert.True(fr.Status.WaitingFor == "HR-Time-Attendance" && fr.Quality == 0.91m, "HR stage and scores");
        Assert.True(fr.Checks.Any(c => c.Tone == "positive" && c.Text.Contains("Consent")), "consent check");

        // Biodata: the employee's mobile changed after the request; the emergency contacts table is compared row by row.
        System.Text.Json.JsonElement J(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        var bio = await svc.CreateBiodataChangeAsync(staff, "STAFF-A", new EssBiodataRequest("Contact & address", new()
        {
            ["mobile"] = J("\"+62811000111\""),
            ["emergency_contacts"] = J("[{\"full_name\":\"Siti\",\"relationship\":\"Spouse\",\"phone\":\"081298765432\"}]"),
        }, null, null, true), default);
        var emp = await docs.GetAsync(sys, "Employee", "STAFF-A");
        await docs.UpdateAsync(sys, "Employee", "STAFF-A", emp.Version, new Dictionary<string, object?> { ["mobile"] = "+6289912345678" });
        var br = await reviews.BiodataAsync(hr, bio.Name);
        var mobile = br.Changes.Single(c => c.Field == "mobile");
        Assert.True(mobile.ChangedSinceRequest && mobile.Current == "+6289912345678" && mobile.Requested == "+62811000111", "live value and change warning");
        var contacts = br.Changes.Single(c => c.Field == "emergency_contacts");
        Assert.True(contacts.IsTable && contacts.Rows.Count == 1 && contacts.Rows[0].Kind == "added" && contacts.Rows[0].New.Contains("Siti"), "table row added");
        Assert.True(br.Checks.Any(c => c.Tone == "warning" && c.Text.Contains("Mobile", StringComparison.OrdinalIgnoreCase)), "warning about the changed field");
    }
}
