using Apps.Erp;
using Apps.Erp.Ess;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Files;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Security;
using Framework.Web;
using Framework.Web.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ESS mobile attendance tests: geofence, device binding, mock location, offline window, double-tap, night shift rules,
/// idempotent punch store, cross-tenant/cross-employee ID isolation, cache invalidation on HR edits, and photo aggregation onto Attendance.
/// </summary>
sealed partial class Tests
{
    private static readonly TimeZoneInfo EssZone = EssAttendanceService.ResolveTimeZone("Asia/Jakarta");
    private static readonly EssSite EssHq = new("JKT-HQ", "Headquarters Jakarta", -6.225100m, 106.809000m, 150);
    private static readonly EssSite EssWarehouse = new("CKR-WH", "Cikarang Warehouse", -6.348000m, 107.165000m, 300);
    private static readonly EssEmployeeProfile EssEmp = new()
    {
        Employee = "EMP-0001", FullName = "Ahmad", LocationCode = "JKT-HQ", DeviceId = "phone-1", AttendanceMode = EssAttendanceMode.AssignedLocation,
    };

    private static EssPunchRequest EssReq(string type = "in", decimal lat = -6.2252m, decimal lon = 106.8091m, decimal accuracy = 10m, string device = "phone-1") =>
        new() { Id = Guid.NewGuid(), Type = type, Latitude = lat, Longitude = lon, Accuracy = accuracy, DeviceId = device };

    private static EssPunch EssStored(string type, DateTimeOffset at, DateOnly workDate) => new()
    {
        Id = Guid.NewGuid(), TenantId = "acme", Employee = "EMP-0001", UserId = "u", PunchType = type, Status = EssPunchStatus.Accepted,
        ServerTime = at, PunchTime = at, WorkDate = workDate,
    };

    [Test]
    public Task Ess_punch_inside_radius_is_accepted_and_outside_is_rejected()
    {
        var now = new DateTimeOffset(2026, 11, 18, 1, 0, 0, TimeSpan.Zero); // 08:00 WIB
        var policy = new EssAttendancePolicy();
        var ok = EssAttendanceRules.Evaluate(EssReq(), "in", EssEmp, [EssHq, EssWarehouse], policy, null, now, EssZone);
        Assert.Equal(EssPunchStatus.Accepted, ok.Status, "punch inside office radius is accepted");
        Assert.Equal("JKT-HQ", ok.Site?.Code, "matched assigned employee work location");
        Assert.Equal(new DateOnly(2026, 11, 18), ok.WorkDate, "work date matches local WIB date");

        // ~1.1 km from office; Assigned location mode only matches assigned site.
        var far = EssAttendanceRules.Evaluate(EssReq(lat: -6.2350m), "in", EssEmp, [EssHq, EssWarehouse], policy, null, now, EssZone);
        Assert.Equal(EssPunchStatus.Rejected, far.Status, "outside radius is rejected");
        Assert.True(far.Reasons.Contains(EssReason.OutsideGeofence), "outside_geofence reason recorded");

        var lenient = EssAttendanceRules.Evaluate(EssReq(lat: -6.2350m), "in", EssEmp, [EssHq], policy with { RejectOutsideGeofence = false }, null, now, EssZone);
        Assert.Equal(EssPunchStatus.Flagged, lenient.Status, "lenient policy accepts but flags outside geofence");

        var anyOffice = EssAttendanceRules.Evaluate(EssReq(lat: -6.3481m, lon: 107.1651m), "in", EssEmp with { AttendanceMode = EssAttendanceMode.AnyOffice },
            [EssHq, EssWarehouse], policy, null, now, EssZone);
        Assert.Equal("CKR-WH", anyOffice.Site?.Code, "Any office mode matches nearest site");
        Assert.Equal(EssPunchStatus.Accepted, anyOffice.Status, "punch at warehouse accepted in Any office mode");

        var anywhere = EssAttendanceRules.Evaluate(EssReq(lat: -7.25m, lon: 112.75m), "in", EssEmp with { AttendanceMode = EssAttendanceMode.Anywhere },
            [EssHq], policy, null, now, EssZone);
        Assert.Equal(EssPunchStatus.Accepted, anywhere.Status, "field staff (Anywhere) is not restricted by radius");
        return Task.CompletedTask;
    }

    [Test]
    public Task Ess_device_mock_location_and_double_tap_rules()
    {
        var now = new DateTimeOffset(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);
        var policy = new EssAttendancePolicy();

        var otherPhone = EssAttendanceRules.Evaluate(EssReq(device: "phone-2"), "in", EssEmp, [EssHq], policy, null, now, EssZone);
        Assert.Equal(EssPunchStatus.Rejected, otherPhone.Status, "unregistered phone rejected when device binding is active");
        Assert.True(otherPhone.Reasons.Contains(EssReason.DeviceNotRegistered), "device_not_registered reason recorded");

        var firstPhone = EssAttendanceRules.Evaluate(EssReq(device: "phone-9"), "in", EssEmp with { DeviceId = null }, [EssHq], policy, null, now, EssZone);
        Assert.True(firstPhone.BindDevice && firstPhone.Status == EssPunchStatus.Accepted, "first phone is accepted and marked for binding");

        var mock = EssAttendanceRules.Evaluate(EssReq() with { IsMockLocation = true }, "in", EssEmp, [EssHq], policy, null, now, EssZone);
        Assert.Equal(EssPunchStatus.Rejected, mock.Status, "mock location is rejected");

        var noGps = EssAttendanceRules.Evaluate(EssReq() with { Latitude = null, Longitude = null }, "in", EssEmp, [EssHq], policy, null, now, EssZone);
        Assert.True(noGps.Status == EssPunchStatus.Rejected && noGps.Reasons.Contains(EssReason.LocationMissing), "missing GPS rejected");

        var lowAccuracy = EssAttendanceRules.Evaluate(EssReq(accuracy: 250m), "in", EssEmp, [EssHq], policy, null, now, EssZone);
        Assert.True(lowAccuracy.Status == EssPunchStatus.Flagged && lowAccuracy.Reasons.Contains(EssReason.LowAccuracy), "low GPS accuracy flagged");

        var previous = EssStored("in", now.AddSeconds(-20), new DateOnly(2026, 11, 18));
        var doubleTap = EssAttendanceRules.Evaluate(EssReq(), "in", EssEmp, [EssHq], policy, previous, now, EssZone);
        Assert.True(doubleTap.Status == EssPunchStatus.Rejected && doubleTap.Reasons.Contains(EssReason.TooSoon), "double-tap rejected");
        return Task.CompletedTask;
    }

    [Test]
    public Task Ess_offline_punch_and_night_shift_work_date()
    {
        var now = new DateTimeOffset(2026, 11, 18, 2, 0, 0, TimeSpan.Zero); // 09:00 WIB
        var policy = new EssAttendancePolicy();

        var offlineAt = now.AddHours(-1);
        var offline = EssAttendanceRules.Evaluate(EssReq() with { IsOffline = true, DeviceTime = offlineAt }, "in", EssEmp, [EssHq], policy, null, now, EssZone);
        Assert.Equal(offlineAt, offline.PunchTime, "offline punch uses device timestamp");
        Assert.Equal(EssPunchStatus.Flagged, offline.Status, "offline punch is flagged for review");

        var tooOld = EssAttendanceRules.Evaluate(EssReq() with { IsOffline = true, DeviceTime = now.AddHours(-30) }, "in", EssEmp, [EssHq], policy, null, now, EssZone);
        Assert.Equal(EssPunchStatus.Rejected, tooOld.Status, "offline punch older than 24 hours is rejected");

        // Night shift: clock-in 18-11 22:00 WIB, clock-out 19-11 06:00 WIB -> work date 18-11.
        var checkIn = EssStored("in", new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero), new DateOnly(2026, 11, 18));
        var morning = new DateTimeOffset(2026, 11, 18, 23, 0, 0, TimeSpan.Zero);
        var checkOut = EssAttendanceRules.Evaluate(EssReq("out"), "out", EssEmp, [EssHq], policy, checkIn, morning, EssZone);
        Assert.Equal(new DateOnly(2026, 11, 18), checkOut.WorkDate, "clock-out after midnight inherits clock-in date");
        Assert.Equal(EssPunchStatus.Accepted, checkOut.Status, "night shift clock-out accepted");

        var orphanOut = EssAttendanceRules.Evaluate(EssReq("out"), "out", EssEmp, [EssHq], policy, null, now, EssZone);
        Assert.True(orphanOut.Status == EssPunchStatus.Flagged && orphanOut.Reasons.Contains(EssReason.OutWithoutIn), "clock-out without clock-in is flagged");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Ess_punch_store_is_idempotent_and_tracks_pending_days()
    {
        var store = new InMemoryEssPunchStore();
        var at = new DateTimeOffset(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);
        var punch = EssStored("in", at, new DateOnly(2026, 11, 18));
        var (_, created) = await store.InsertAsync(punch);
        var (again, createdAgain) = await store.InsertAsync(punch with { Status = EssPunchStatus.Rejected });
        Assert.True(created && !createdAgain, "replaying with identical ID does not create a duplicate record");
        Assert.Equal(EssPunchStatus.Accepted, again.Status, "replay returns initial evaluation status");

        var pending = await store.PendingDaysAsync("acme", 10);
        Assert.Equal(1, pending.Count, "one employee-day awaiting aggregation");
        await store.MarkProcessedAsync("acme", [punch.Id], "ATT-2026-0000001", null, at);
        Assert.Equal(0, (await store.PendingDaysAsync("acme", 10)).Count, "processed day is no longer pending");

        Assert.True(await store.SetPhotoAsync("acme", punch.Id, "EMP-0001", "f_photo"), "photo linked to own punch");
        Assert.True(!await store.SetPhotoAsync("acme", punch.Id, "EMP-0002", "f_other"), "photo cannot be linked to another employee's punch");
        Assert.Equal(1, (await store.PendingDaysAsync("acme", 10)).Count, "new photo marks work day for re-aggregation");
        Assert.Equal("f_photo", (await store.GetAsync("acme", punch.Id))?.PhotoFileId, "punch retrieved with attached photo ID");
        Assert.True(await store.GetAsync("globex", punch.Id) is null, "cross-tenant punch is isolated");

        var distance = EssAttendanceRules.DistanceMeters(-6.2251m, 106.8090m, -6.2350m, 106.8090m);
        Assert.True(distance is > 1090 and < 1110, $"Haversine distance ~1.1 km (actual {distance:0})");
    }

    [Test]
    public async Task Ess_service_photo_attachment_device_binding_cache_invalidation_and_aggregation()
    {
        var erpMetadataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Apps.Erp", "Metadata"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddFrogFramework(o => o.LoadFrom(erpMetadataPath));
        services.AddFrogFrameworkIdentity();
        services.AddErpHooks();
        await using var provider = services.BuildServiceProvider();

        var docs = provider.GetRequiredService<DocumentService>();
        var ess = provider.GetRequiredService<EssAttendanceService>();
        var files = provider.GetRequiredService<IFileStore>();
        var sys = UserContext.System("acme");

        // Seed minimal HR masters and Employee for ESS flow
        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?>
        {
            ["currency_code"] = "IDR",
            ["currency_name"] = "Indonesian Rupiah",
        });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?>
        {
            ["company_code"] = "ACME",
            ["company_name"] = "PT Acme Nusantara",
            ["base_currency"] = "IDR",
            ["country"] = "ID",
        });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "DEPT-IT",
            ["org_unit_name"] = "IT Department",
            ["unit_type"] = "Department",
            ["company"] = "ACME",
            ["staffing_model"] = "Job",
            ["effective_from"] = "2025-01-01",
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?>
        {
            ["job_code"] = "JOB-IT",
            ["job_title"] = "IT Staff",
            ["job_level"] = "Staff",
        });
        await docs.InsertAsync(sys, "Location", new Dictionary<string, object?>
        {
            ["location_code"] = "JKT-HQ",
            ["location_name"] = "Headquarters Jakarta",
            ["is_active"] = true,
            ["allow_mobile_attendance"] = true,
            ["geolocation"] = "-6.225100,106.809000",
            ["attendance_radius_m"] = 150,
        });
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?>
        {
            ["shift_name"] = "Office 08-17",
            ["start_time"] = "08:00:00",
            ["end_time"] = "17:00:00",
            ["break_minutes"] = 60,
        });
        await docs.SetSingleAsync(sys, "HRSettings", new Dictionary<string, object?>
        {
            ["default_shift_type"] = "Office 08-17",
            ["ess_bind_device"] = true,
            ["ess_reject_outside_geofence"] = true,
            ["ess_reject_mock_location"] = true,
        });
        var empDoc = await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-0001",
            ["full_name"] = "Ahmad Fadilah",
            ["gender"] = "Male",
            ["date_of_birth"] = "1995-05-10",
            ["company"] = "ACME",
            ["organization_unit"] = "DEPT-IT",
            ["job_profile"] = "JOB-IT",
            ["hire_date"] = "2025-01-01",
            ["status"] = "Active",
            ["location"] = "JKT-HQ",
            ["shift_type"] = "Office 08-17",
            ["attendance_mode"] = EssAttendanceMode.AssignedLocation,
        });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-0002",
            ["full_name"] = "Budi Santoso",
            ["gender"] = "Male",
            ["date_of_birth"] = "1994-03-15",
            ["company"] = "ACME",
            ["organization_unit"] = "DEPT-IT",
            ["job_profile"] = "JOB-IT",
            ["hire_date"] = "2025-01-01",
            ["status"] = "Active",
            ["location"] = "JKT-HQ",
            ["shift_type"] = "Office 08-17",
            ["attendance_mode"] = EssAttendanceMode.AssignedLocation,
        });

        var empUser = new UserContext(
            "ahmad@acme.co.id",
            "Ahmad Fadilah",
            "acme",
            new HashSet<string> { "Employee" },
            new Dictionary<string, string> { ["employee"] = empDoc.Name });

        // 1. First punch binds phone-A to Employee.ess_device_id
        var inReq = EssReq("in", device: "phone-A");
        var inRes = await ess.PunchAsync(empUser, empDoc.Name, inReq, CancellationToken.None);
        Assert.Equal(EssPunchStatus.Accepted, inRes.Punch.Status, "first punch accepted");
        var updatedEmp = await docs.GetAsync(sys, "Employee", empDoc.Name);
        Assert.Equal("phone-A", updatedEmp.Data["ess_device_id"] as string, "device bound on Employee document");

        // 2. Punch from phone-B is rejected while phone-A is bound
        var wrongPhone = await ess.PunchAsync(empUser, empDoc.Name, EssReq("out", device: "phone-B"), CancellationToken.None);
        Assert.Equal(EssPunchStatus.Rejected, wrongPhone.Punch.Status, "different device rejected");

        // 3. HR resets ess_device_id -> EssCacheInvalidationHook invalidates profile cache immediately -> phone-B is accepted
        await docs.UpdateAsync(sys, "Employee", empDoc.Name, updatedEmp.Version, new Dictionary<string, object?> { ["ess_device_id"] = null });
        var outReq = EssReq("out", device: "phone-B");
        var outRes = await ess.PunchAsync(empUser, empDoc.Name, outReq, CancellationToken.None);
        Assert.Equal(EssPunchStatus.Accepted, outRes.Punch.Status, "new phone accepted immediately after HR reset without stale cache");

        // 4. Cross-employee and cross-tenant UUID collision is blocked (Anti-IDOR)
        var otherEmpUser = new UserContext(
            "budi@acme.co.id",
            "Budi Santoso",
            "acme",
            new HashSet<string> { "Employee" },
            new Dictionary<string, string> { ["employee"] = "EMP-0002" });
        await Assert.Throws<ValidationFailedException>(() =>
            ess.PunchAsync(otherEmpUser, "EMP-0002", inReq, CancellationToken.None), "cross-employee reuse of punch UUID is rejected");

        // 5. Upload PNG selfie to clock-in punch and verify OrphanFileCleanupJob does not prune it
        byte[] validPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
        var photoId = await ess.AttachPhotoAsync(empUser, empDoc.Name, inReq.Id, new MemoryStream(validPng), CancellationToken.None);
        Assert.Equal(0, await files.PruneOrphansAsync(DateTimeOffset.UtcNow.AddDays(2)), "punch photo is bound to Attendance and not pruned as orphan");

        // 6. Run EssAttendanceAggregationJob -> materializes Attendance document with check_in, check_out, and check_in_photo
        var job = new EssAttendanceAggregationJob();
        await job.ExecuteAsync(new ScheduledJobContext
        {
            TenantId = "acme",
            SystemUser = sys,
            Docs = docs,
            Services = provider,
            ScheduledTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var punchAfter = await provider.GetRequiredService<IEssPunchStore>().GetAsync("acme", inReq.Id);
        Assert.True(punchAfter?.AttendanceName is { Length: > 0 } && punchAfter.ProcessError is null,
            $"aggregation succeeded without process_error (got error: {punchAfter?.ProcessError})");

        var attDoc = await docs.GetAsync(empUser, "Attendance", punchAfter!.AttendanceName!);
        Assert.Equal(photoId, attDoc.Data["check_in_photo"] as string, "selfie photo linked onto Attendance document");
        var fileInfo = await files.GetInfoAsync("acme", photoId);
        Assert.Equal(attDoc.Name, fileInfo?.DocName, "file record linked to materialized Attendance document");
    }
}
