namespace Apps.Erp.Ess;

/// <summary>Mobile attendance policy (HR Settings › ESS section). Defaults apply when fields are not configured.</summary>
public sealed record EssAttendancePolicy
{
    public bool RejectOutsideGeofence { get; init; } = true;
    public int DefaultRadiusM { get; init; } = 100;
    /// <summary>GPS accuracy worse than this threshold flags the punch for HR review rather than rejecting it.</summary>
    public int MaxAccuracyM { get; init; } = 100;
    public bool RejectMockLocation { get; init; } = true;
    /// <summary>Single device per employee: the first phone to punch is bound to Employee.ess_device_id; HR clears it to allow switching phones.</summary>
    public bool BindDevice { get; init; } = true;
    /// <summary>Offline punches older than this threshold are rejected.</summary>
    public int MaxOfflineHours { get; init; } = 24;
    /// <summary>Consecutive punches of the same type within this interval are rejected (double-tap with distinct IDs).</summary>
    public int MinPunchIntervalMinutes { get; init; } = 1;
    public bool RequirePhoto { get; init; }
    /// <summary>Clock-out within N hours after the last clock-in inherits the clock-in work date (overnight shift crossing midnight).</summary>
    public int OvernightWindowHours { get; init; } = 16;
    /// <summary>Clock skew between device and server (online punch) exceeding this threshold flags the punch.</summary>
    public int MaxClockSkewMinutes { get; init; } = 5;
    /// <summary>Every punch must carry a face match against the employee's verified Face Registration.</summary>
    public bool RequireFace { get; init; }
    /// <summary>Lowest accepted match score (0–1). The threshold sent by the app is ignored.</summary>
    public decimal MinFaceScore { get; init; } = 0.8m;
    /// <summary>A score below <see cref="MinFaceScore"/> rejects the punch instead of flagging it.</summary>
    public bool RejectFaceMismatch { get; init; }
}

/// <summary>Office/warehouse location with coordinates and attendance radius (DocType Location).</summary>
public sealed record EssSite(string Code, string Name, decimal Latitude, decimal Longitude, int RadiusM);

/// <summary>Employee profile data required for attendance evaluation (cached for a few minutes).</summary>
public sealed record EssEmployeeProfile
{
    public required string Employee { get; init; }
    public required string FullName { get; init; }
    public string Status { get; init; } = "Active";
    public string? LocationCode { get; init; }
    /// <summary>Assigned location | Any office | Anywhere | Not allowed</summary>
    public string AttendanceMode { get; init; } = EssAttendanceMode.AssignedLocation;
    public string? DeviceId { get; init; }
    public string? ShiftType { get; init; }
    public string? ShiftStart { get; init; }
    public string? ShiftEnd { get; init; }
    public string? Company { get; init; }
    public string? OrganizationUnit { get; init; }
    public string? Position { get; init; }
    public string? JobTitle { get; init; }
    public string? ReportsTo { get; init; }
    /// <summary>Name of the employee's active (verified) Face Registration.</summary>
    public string? FaceRegistration { get; init; }
}

public static class EssAttendanceMode
{
    public const string AssignedLocation = "Assigned location";
    public const string AnyOffice = "Any office";
    public const string Anywhere = "Anywhere";
    public const string NotAllowed = "Not allowed";
}

/// <summary>Attendance punch payload submitted by the mobile app.</summary>
public sealed record EssPunchRequest
{
    public Guid Id { get; init; }
    public string? Type { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public decimal? Accuracy { get; init; }
    public bool IsMockLocation { get; init; }
    public bool IsOffline { get; init; }
    public DateTimeOffset? DeviceTime { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceInfo { get; init; }
    public string? AppVersion { get; init; }
    public string? Note { get; init; }
    /// <summary>Result of the on-device face match (required when HR Settings asks for it).</summary>
    public EssFaceMatch? Face { get; init; }
}

/// <param name="Score">Similarity 0–1 computed by the app against the downloaded template.</param>
/// <param name="Registration">Face Registration the template came from (detects an outdated template on the phone).</param>
public sealed record EssFaceMatch(decimal? Score, string? Registration, string? ModelVersion, decimal? Liveness);

/// <summary>Reason codes for rejected or flagged attendance punches with user-facing messages.</summary>
public static class EssReason
{
    public const string EmployeeInactive = "employee_inactive";
    public const string MobileNotAllowed = "mobile_attendance_not_allowed";
    public const string DeviceNotRegistered = "device_not_registered";
    public const string DeviceIdMissing = "device_id_missing";
    public const string MockLocation = "mock_location";
    public const string LocationMissing = "location_missing";
    public const string OutsideGeofence = "outside_geofence";
    public const string NoSiteConfigured = "no_site_configured";
    public const string LowAccuracy = "low_accuracy";
    public const string Offline = "offline";
    public const string OfflineTooOld = "offline_too_old";
    public const string DeviceTimeInFuture = "device_time_in_future";
    public const string ClockSkew = "clock_skew";
    public const string TooSoon = "too_soon";
    public const string OutWithoutIn = "out_without_in";
    public const string FaceNotRegistered = "face_not_registered";
    public const string FaceMissing = "face_missing";
    public const string FaceMismatch = "face_mismatch";
    public const string FaceTemplateOutdated = "face_template_outdated";

    /// <summary>Reasons that unconditionally reject a punch (others flag the punch for review).</summary>
    public static readonly IReadOnlySet<string> Blocking = new HashSet<string>
    {
        EmployeeInactive, MobileNotAllowed, DeviceNotRegistered, DeviceIdMissing, OfflineTooOld, DeviceTimeInFuture, TooSoon,
    };

    public static string Message(string code) => code switch
    {
        EmployeeInactive => "Employee status is not active. Please contact HR.",
        MobileNotAllowed => "Mobile attendance is not enabled for your profile. Please use the office time clock.",
        DeviceNotRegistered => "This phone is not the registered device for your attendance. Please ask HR to reset your registered device.",
        DeviceIdMissing => "Device ID was not provided. Please update the mobile application.",
        MockLocation => "Mock or simulated GPS location detected. Disable any location spoofing apps.",
        LocationMissing => "GPS coordinates were not provided. Enable location services and try again.",
        OutsideGeofence => "You are outside the permitted work location radius.",
        NoSiteConfigured => "Your work location does not have geofence coordinates configured. Punch recorded for HR review.",
        LowAccuracy => "Low GPS accuracy. Punch recorded for HR review.",
        Offline => "Offline punch synced later; device timestamp recorded for HR review.",
        OfflineTooOld => "Offline punch is older than the maximum allowed window. Please contact HR.",
        DeviceTimeInFuture => "Device clock is ahead of server time. Enable automatic date and time on your phone.",
        ClockSkew => "Device clock differs significantly from server time; server time was used.",
        TooSoon => "You just recorded a punch. Please wait a moment before punching again.",
        OutWithoutIn => "Clock-out recorded without a preceding clock-in.",
        FaceNotRegistered => "No verified face registration. Register your face in the app; punch recorded for HR review.",
        FaceMissing => "Face verification was not performed. Punch recorded for HR review.",
        FaceMismatch => "The face did not match your registered face.",
        FaceTemplateOutdated => "Your face template on this phone is outdated. Open the app online to refresh it.",
        _ => code,
    };
}

/// <summary>Evaluation outcome for a single attendance punch.</summary>
public sealed record EssPunchEvaluation(string Status, IReadOnlyList<string> Reasons, DateTimeOffset PunchTime, DateOnly WorkDate,
    EssSite? Site, double? DistanceM, bool BindDevice);

/// <summary>
/// Pure, I/O-free mobile attendance evaluation rules: device binding, mock location, geofence radius, GPS accuracy tolerance,
/// offline window, double-tap prevention, and overnight shift work date resolution.
/// </summary>
public static class EssAttendanceRules
{
    public static double DistanceMeters(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        const double R = 6371000; // Earth radius in meters
        static double Rad(decimal d) => (double)d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Returns candidate sites applicable to the employee: assigned work location or all mobile-enabled sites.</summary>
    public static IReadOnlyList<EssSite> CandidateSites(EssEmployeeProfile emp, IReadOnlyList<EssSite> allSites) =>
        emp.AttendanceMode == EssAttendanceMode.AssignedLocation && emp.LocationCode is { } code
            ? allSites.Where(s => s.Code == code).ToList() is { Count: > 0 } own ? own : allSites
            : allSites;

    public static EssPunchEvaluation Evaluate(EssPunchRequest req, string punchType, EssEmployeeProfile emp, IReadOnlyList<EssSite> sites,
        EssAttendancePolicy policy, EssPunch? lastCounted, DateTimeOffset serverTime, TimeZoneInfo timeZone)
    {
        var reasons = new List<string>();
        var bindDevice = false;

        if (emp.Status != "Active") reasons.Add(EssReason.EmployeeInactive);
        if (emp.AttendanceMode == EssAttendanceMode.NotAllowed) reasons.Add(EssReason.MobileNotAllowed);

        if (policy.BindDevice)
        {
            if (string.IsNullOrWhiteSpace(req.DeviceId)) reasons.Add(EssReason.DeviceIdMissing);
            else if (string.IsNullOrWhiteSpace(emp.DeviceId)) bindDevice = true;
            else if (!string.Equals(emp.DeviceId, req.DeviceId, StringComparison.Ordinal)) reasons.Add(EssReason.DeviceNotRegistered);
        }

        // Effective punch timestamp: online uses server time; offline uses device time (bounded by max age and future check).
        var punchTime = serverTime;
        if (req.IsOffline && req.DeviceTime is { } offlineAt)
        {
            reasons.Add(EssReason.Offline);
            if (offlineAt > serverTime.AddMinutes(2)) reasons.Add(EssReason.DeviceTimeInFuture);
            else if (serverTime - offlineAt > TimeSpan.FromHours(policy.MaxOfflineHours)) reasons.Add(EssReason.OfflineTooOld);
            else punchTime = offlineAt;
        }
        else if (req.DeviceTime is { } deviceAt && Math.Abs((deviceAt - serverTime).TotalMinutes) > policy.MaxClockSkewMinutes)
        {
            reasons.Add(EssReason.ClockSkew);
        }

        if (req.IsMockLocation) reasons.Add(EssReason.MockLocation);

        // Geofence & GPS evaluation
        EssSite? site = null;
        double? distance = null;
        if (req.Latitude is { } lat && req.Longitude is { } lon && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
        {
            var candidates = CandidateSites(emp, sites);
            foreach (var s in candidates)
            {
                var d = DistanceMeters(lat, lon, s.Latitude, s.Longitude);
                if (distance is null || d < distance) { distance = d; site = s; }
            }
            if (emp.AttendanceMode != EssAttendanceMode.Anywhere)
            {
                if (site is null) reasons.Add(EssReason.NoSiteConfigured);
                // GPS accuracy is treated as tolerance: if the accuracy circle (capped at MaxAccuracyM) intersects the site radius, it is accepted.
                else if (distance - (double)Math.Min(req.Accuracy ?? 0, policy.MaxAccuracyM) > site.RadiusM) reasons.Add(EssReason.OutsideGeofence);
            }
            if (req.Accuracy is { } acc && acc > policy.MaxAccuracyM) reasons.Add(EssReason.LowAccuracy);
        }
        else if (emp.AttendanceMode != EssAttendanceMode.Anywhere)
        {
            reasons.Add(EssReason.LocationMissing);
        }

        // Double-tap with distinct UUIDs within a short interval
        if (lastCounted is not null && lastCounted.PunchType == punchType
            && (punchTime - lastCounted.PunchTime).Duration() < TimeSpan.FromMinutes(Math.Max(0, policy.MinPunchIntervalMinutes)))
            reasons.Add(EssReason.TooSoon);

        // Work date resolution (night shift crossing midnight inherits clock-in work date)
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(punchTime, timeZone).DateTime);
        var workDate = localDate;
        if (punchType == "out")
        {
            if (lastCounted is { PunchType: "in" } open && punchTime - open.PunchTime <= TimeSpan.FromHours(policy.OvernightWindowHours)
                && punchTime >= open.PunchTime)
                workDate = open.WorkDate;
            else if (lastCounted is null || lastCounted.PunchType == "out" || lastCounted.WorkDate != localDate)
                reasons.Add(EssReason.OutWithoutIn);
        }

        // Face match (computed on the phone against the verified template).
        if (policy.RequireFace)
        {
            if (emp.FaceRegistration is null) reasons.Add(EssReason.FaceNotRegistered);
            else if (req.Face?.Score is not { } score) reasons.Add(EssReason.FaceMissing);
            else
            {
                if (score < policy.MinFaceScore) reasons.Add(EssReason.FaceMismatch);
                if (req.Face.Registration is { Length: > 0 } reg && reg != emp.FaceRegistration) reasons.Add(EssReason.FaceTemplateOutdated);
            }
        }

        var blocking = reasons.Any(EssReason.Blocking.Contains)
                       || (policy.RejectFaceMismatch && reasons.Contains(EssReason.FaceMismatch))
                       || (policy.RejectMockLocation && reasons.Contains(EssReason.MockLocation))
                       || (policy.RejectOutsideGeofence && (reasons.Contains(EssReason.OutsideGeofence) || reasons.Contains(EssReason.LocationMissing)));
        var status = blocking ? EssPunchStatus.Rejected : reasons.Count > 0 ? EssPunchStatus.Flagged : EssPunchStatus.Accepted;
        return new EssPunchEvaluation(status, reasons, punchTime, workDate, site, distance is null ? null : Math.Round(distance.Value, 1),
            bindDevice && status != EssPunchStatus.Rejected);
    }
}
