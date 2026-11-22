using System.Globalization;
using System.Security.Cryptography;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Files;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Apps.Erp.Ess;

/// <summary>Attendance punch evaluation and persistence outcome returned to the mobile application.</summary>
public sealed record EssPunchResult(EssPunch Punch, bool Duplicate, EssSite? Site);

/// <summary>
/// Mobile attendance service: evaluates punches against cached profile, location, and policy data, then executes a single INSERT
/// into <see cref="IEssPunchStore"/>. Avoids heavy document creation during peak rush hours; <see cref="EssAttendanceAggregationJob"/>
/// subsequently materializes daily Attendance documents through <see cref="DocumentService"/>.
/// </summary>
public sealed class EssAttendanceService(IEssPunchStore punches, IDocumentStore store, DocumentService docs, IFileStore files,
    IMemoryCache cache, TimeProvider clock, IConfiguration config, ILogger<EssAttendanceService> log)
{
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SitesTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PolicyTtl = TimeSpan.FromMinutes(1);
    public const long MaxPhotoBytes = 1024 * 1024;
    private long _profileEpoch;

    public TimeZoneInfo TimeZone => ResolveTimeZone(config["Ess:TimeZone"]);

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        // Defaults to the application time zone (FieldTypeSettings.DefaultTimeZone, Asia/Jakarta), matching Datetime field rendering.
        if (string.IsNullOrWhiteSpace(id)) return FieldTypeSettings.DefaultTimeZone;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return FieldTypeSettings.DefaultTimeZone; }
    }

    // ------------------------------------------------------------------ cached master data

    public async Task<EssEmployeeProfile?> GetProfileAsync(string tenantId, string employee, CancellationToken ct)
    {
        var key = ("ess-emp", tenantId, employee, Interlocked.Read(ref _profileEpoch));
        if (cache.TryGetValue(key, out EssEmployeeProfile? hit)) return hit;
        var m = new Masters(store, tenantId);
        var emp = await m.GetAsync("Employee", employee, ct);
        if (emp is null) return null;
        var d = emp.Data;
        var shift = await m.GetAsync("ShiftType", d.Str("shift_type") ?? (await m.SingleAsync("HRSettings", ct)).Str("default_shift_type"), ct);
        var position = await m.GetAsync("Position", d.Str("position"), ct);
        var profile = new EssEmployeeProfile
        {
            Employee = emp.Name,
            FullName = d.Str("full_name") ?? emp.Name,
            Status = d.Str("status") ?? "Active",
            LocationCode = d.Str("location"),
            AttendanceMode = d.Str("attendance_mode") ?? EssAttendanceMode.AssignedLocation,
            DeviceId = d.Str("ess_device_id"),
            ShiftType = shift?.Name,
            ShiftStart = shift?.Data.Str("start_time"),
            ShiftEnd = shift?.Data.Str("end_time"),
            Company = d.Str("company"),
            OrganizationUnit = d.Str("organization_unit"),
            Position = d.Str("position"),
            JobTitle = position?.Data.Str("position_title") ?? d.Str("job_profile"),
            ReportsTo = d.Str("reports_to"),
            FaceRegistration = (await m.QueryAsync("FaceRegistration", [new("employee", "=", emp.Name)], ct, take: 20))
                .FirstOrDefault(f => f.WorkflowState == "Active")?.Name,
        };
        cache.Set(key, profile, ProfileTtl);
        return profile;
    }

    public void InvalidateProfile(string tenantId, string employee) =>
        cache.Remove(("ess-emp", tenantId, employee, Interlocked.Read(ref _profileEpoch)));

    public void InvalidateAllProfiles() => Interlocked.Increment(ref _profileEpoch);

    public void InvalidateSites(string tenantId) => cache.Remove(("ess-sites", tenantId));

    public void InvalidatePolicy(string tenantId) => cache.Remove(("ess-policy", tenantId));

    public async Task<IReadOnlyList<EssSite>> GetSitesAsync(string tenantId, CancellationToken ct)
    {
        var key = ("ess-sites", tenantId);
        if (cache.TryGetValue(key, out IReadOnlyList<EssSite>? hit) && hit is not null) return hit;
        var policy = await GetPolicyAsync(tenantId, ct);
        var m = new Masters(store, tenantId);
        var sites = new List<EssSite>();
        foreach (var loc in await m.QueryAsync("Location", [], ct, take: 5000))
        {
            var d = loc.Data;
            if (d.ContainsKey("is_active") && d["is_active"] is false) continue;
            if (d.Bool("is_group") || d.ContainsKey("allow_mobile_attendance") && d["allow_mobile_attendance"] is false) continue;
            if (ParseGeo(d.Str("geolocation")) is not { } geo) continue;
            var radius = (int)d.Dec("attendance_radius_m");
            sites.Add(new EssSite(loc.Name, d.Str("location_name") ?? loc.Name, geo.Lat, geo.Lon, radius > 0 ? radius : policy.DefaultRadiusM));
        }
        cache.Set(key, (IReadOnlyList<EssSite>)sites, SitesTtl);
        return sites;
    }

    public async Task<EssAttendancePolicy> GetPolicyAsync(string tenantId, CancellationToken ct)
    {
        var key = ("ess-policy", tenantId);
        if (cache.TryGetValue(key, out EssAttendancePolicy? hit) && hit is not null) return hit;
        var hs = await new Masters(store, tenantId).SingleAsync("HRSettings", ct);
        var defaults = new EssAttendancePolicy();
        bool Flag(string f, bool fallback) => hs.TryGetValue(f, out var v) && v is bool b ? b : fallback;
        int Num(string f, int fallback) => hs.TryGetValue(f, out var v) && v is not null && D.ToDec(v) is var n && n > 0 ? (int)n : fallback;
        var policy = new EssAttendancePolicy
        {
            RejectOutsideGeofence = Flag("ess_reject_outside_geofence", defaults.RejectOutsideGeofence),
            DefaultRadiusM = Num("ess_default_radius_m", defaults.DefaultRadiusM),
            MaxAccuracyM = Num("ess_max_gps_accuracy_m", defaults.MaxAccuracyM),
            RejectMockLocation = Flag("ess_reject_mock_location", defaults.RejectMockLocation),
            BindDevice = Flag("ess_bind_device", defaults.BindDevice),
            MaxOfflineHours = Num("ess_max_offline_hours", defaults.MaxOfflineHours),
            MinPunchIntervalMinutes = Num("ess_min_punch_interval_minutes", defaults.MinPunchIntervalMinutes),
            RequirePhoto = Flag("ess_require_photo", defaults.RequirePhoto),
            OvernightWindowHours = Num("ess_overnight_window_hours", defaults.OvernightWindowHours),
            RequireFace = Flag("ess_require_face", defaults.RequireFace),
            MinFaceScore = hs.TryGetValue("ess_min_face_score", out var fs) && fs is not null && D.ToDec(fs) is var score && score is > 0 and <= 1 ? score : defaults.MinFaceScore,
            RejectFaceMismatch = Flag("ess_reject_face_mismatch", defaults.RejectFaceMismatch),
        };
        cache.Set(key, policy, PolicyTtl);
        return policy;
    }

    public static (decimal Lat, decimal Lon)? ParseGeo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split(',') is not { Length: 2 } p) return null;
        return decimal.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
               && decimal.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            ? (lat, lon) : null;
    }

    // ------------------------------------------------------------------ punch ingestion

    public async Task<EssPunchResult> PunchAsync(UserContext user, string employee, EssPunchRequest req, CancellationToken ct)
    {
        var type = req.Type?.Trim().ToLowerInvariant();
        if (req.Id == Guid.Empty) throw new ValidationFailedException([new FieldError("id", "id (UUID from mobile app) is required")]);
        if (type is not ("in" or "out")) throw new ValidationFailedException([new FieldError("type", "type must be 'in' or 'out'")]);

        // Replay with identical ID: return the previously persisted result without re-evaluating.
        if (await punches.GetAsync(user.TenantId, req.Id, ct) is { } existing)
            return existing.Employee == employee
                ? new EssPunchResult(existing, true, await SiteAsync(user.TenantId, existing.LocationCode, ct))
                : throw new ValidationFailedException([new FieldError("id", "This punch ID is already in use")]);

        var profile = await GetProfileAsync(user.TenantId, employee, ct)
                      ?? throw new PermissionDeniedException($"Employee {employee} was not found");
        var policy = await GetPolicyAsync(user.TenantId, ct);
        var sites = await GetSitesAsync(user.TenantId, ct);
        var now = clock.GetUtcNow();
        var last = await punches.LastCountedAsync(user.TenantId, employee, req.IsOffline && req.DeviceTime is { } dt && dt < now ? dt : now, ct);
        var eval = EssAttendanceRules.Evaluate(req, type, profile, sites, policy, last, now, TimeZone);

        var punch = new EssPunch
        {
            Id = req.Id, TenantId = user.TenantId, Employee = employee, UserId = user.Id, PunchType = type, Status = eval.Status,
            Reasons = eval.Reasons, ServerTime = now, DeviceTime = req.DeviceTime, PunchTime = eval.PunchTime, WorkDate = eval.WorkDate,
            Latitude = req.Latitude, Longitude = req.Longitude, AccuracyM = req.Accuracy, LocationCode = eval.Site?.Code,
            DistanceM = eval.DistanceM is { } dist ? (decimal)dist : null, IsMockLocation = req.IsMockLocation, IsOffline = req.IsOffline,
            DeviceId = Trim(req.DeviceId, 200), DeviceInfo = Trim(req.DeviceInfo, 200), AppVersion = Trim(req.AppVersion, 50), Note = Trim(req.Note, 500),
        };
        var (saved, created) = await punches.InsertAsync(punch, ct);
        if (saved.TenantId != user.TenantId || saved.Employee != employee)
            throw new ValidationFailedException([new FieldError("id", "This punch ID is already in use")]);
        if (created && eval.BindDevice && saved.DeviceId is { } deviceId)
            await BindDeviceAsync(user.TenantId, employee, deviceId, ct);
        return new EssPunchResult(saved, !created, eval.Site);
    }

    private async Task<EssSite?> SiteAsync(string tenantId, string? code, CancellationToken ct) =>
        code is null ? null : (await GetSitesAsync(tenantId, ct)).FirstOrDefault(s => s.Code == code);

    /// <summary>Binds the first device used by the employee to Employee.ess_device_id. Failure to bind does not fail the punch.</summary>
    private async Task BindDeviceAsync(string tenantId, string employee, string deviceId, CancellationToken ct)
    {
        try
        {
            var sys = new SystemDocs(docs, tenantId);
            var current = await sys.TryGetAsync("Employee", employee, ct);
            if (current is not null && string.IsNullOrWhiteSpace(current.Data.Str("ess_device_id")))
                await docs.UpdateAsync(sys.User, "Employee", employee, current.Version,
                    new Dictionary<string, object?> { ["ess_device_id"] = deviceId }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "ESS: failed to bind device for employee {Employee}", employee);
        }
        finally
        {
            InvalidateProfile(tenantId, employee);
        }
    }

    /// <summary>Uploads and attaches a verification selfie (JPEG/PNG/WebP, max 1 MB) to the punch; the aggregation job links it to the Attendance document.</summary>
    public async Task<string> AttachPhotoAsync(UserContext user, string employee, Guid punchId, Stream content, CancellationToken ct)
    {
        var punch = await punches.GetAsync(user.TenantId, punchId, ct);
        if (punch is null || punch.Employee != employee) throw new DocumentNotFoundException("EssPunch", punchId.ToString());
        if (punch.PhotoFileId is { } already) return already;

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxPhotoBytes) throw new ValidationFailedException([new FieldError("photo", "Photo size cannot exceed 1 MB")]);
        }
        var bytes = ms.ToArray();
        var type = FileService.SniffImage(bytes) is { } t && t != "image/gif" ? t
            : throw new ValidationFailedException([new FieldError("photo", "Photo format must be JPEG, PNG, or WebP")]);
        var record = new FileRecord
        {
            Id = "f_" + Guid.NewGuid().ToString("N"), TenantId = user.TenantId, UploadedBy = user.Id, ContentType = type,
            Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), CreatedAt = clock.GetUtcNow(),
            DocType = "Attendance",
        };
        await files.SaveAsync(record, bytes, ct);
        await punches.SetPhotoAsync(user.TenantId, punchId, employee, record.Id, ct);
        return record.Id;
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();
}

public sealed class Location { }
public sealed class ShiftType { }
public sealed class HRSettings { }

/// <summary>Invalidates cached ESS profile, site, and policy data immediately when HR updates master documents.</summary>
public sealed class EssCacheInvalidationHook(IServiceProvider sp) :
    IAfterSave<Employee>, IAfterSave<Location>, IAfterSave<ShiftType>, IAfterSave<HRSettings>
{
    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var ess = sp.GetService(typeof(EssAttendanceService)) as EssAttendanceService;
        if (ess is null) return Task.CompletedTask;
        switch (c.Doc.DocType)
        {
            case "Employee":
                ess.InvalidateProfile(c.Doc.TenantId, c.Doc.Name);
                break;
            case "Location":
                ess.InvalidateSites(c.Doc.TenantId);
                break;
            case "ShiftType":
                ess.InvalidateAllProfiles();
                break;
            case "HRSettings":
                ess.InvalidatePolicy(c.Doc.TenantId);
                ess.InvalidateSites(c.Doc.TenantId);
                ess.InvalidateAllProfiles();
                break;
        }
        return Task.CompletedTask;
    }
}
