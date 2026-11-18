using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace Apps.Erp.Ess;

/// <summary>
/// A single attendance punch from the mobile application (table <c>ess_attendance_punches</c>). Stored as-is, including rejected punches;
/// the <see cref="EssAttendanceAggregationJob"/> background job materializes daily Attendance documents from accepted/flagged punches.
/// </summary>
public sealed record EssPunch
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Employee { get; init; }
    public required string UserId { get; init; }
    /// <summary><c>in</c> (clock-in) or <c>out</c> (clock-out).</summary>
    public required string PunchType { get; init; }
    /// <summary><c>accepted</c>, <c>flagged</c> (accepted but flagged for HR review), or <c>rejected</c>.</summary>
    public required string Status { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public required DateTimeOffset ServerTime { get; init; }
    public DateTimeOffset? DeviceTime { get; init; }
    public required DateTimeOffset PunchTime { get; init; }
    public required DateOnly WorkDate { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public decimal? AccuracyM { get; init; }
    public string? LocationCode { get; init; }
    public decimal? DistanceM { get; init; }
    public bool IsMockLocation { get; init; }
    public bool IsOffline { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceInfo { get; init; }
    public string? AppVersion { get; init; }
    public string? Note { get; init; }
    public string? PhotoFileId { get; init; }
    public string? AttendanceName { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public string? ProcessError { get; init; }

    public bool Counts => Status is EssPunchStatus.Accepted or EssPunchStatus.Flagged;
}

public static class EssPunchStatus
{
    public const string Accepted = "accepted";
    public const string Flagged = "flagged";
    public const string Rejected = "rejected";
}

/// <summary>Mobile attendance punch storage. Inserts are idempotent on <see cref="EssPunch.Id"/> (client-generated UUID).</summary>
public interface IEssPunchStore
{
    /// <summary>Inserts a new punch. If the ID already exists, returns the existing punch with <c>Created = false</c>.</summary>
    Task<(EssPunch Punch, bool Created)> InsertAsync(EssPunch punch, CancellationToken ct = default);
    Task<EssPunch?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    /// <summary>Returns the most recent counted (accepted/flagged) punch for the employee prior to <paramref name="before"/>.</summary>
    Task<EssPunch?> LastCountedAsync(string tenantId, string employee, DateTimeOffset before, CancellationToken ct = default);
    Task<IReadOnlyList<EssPunch>> ListAsync(string tenantId, string employee, DateOnly from, DateOnly to, CancellationToken ct = default);
    /// <summary>Links a verification photo to an employee's punch and marks that work day for re-aggregation onto the Attendance document.</summary>
    Task<bool> SetPhotoAsync(string tenantId, Guid id, string employee, string fileId, CancellationToken ct = default);
    /// <summary>Returns (employee, workDate) pairs with unprocessed punches, ordered oldest first.</summary>
    Task<IReadOnlyList<(string Employee, DateOnly WorkDate)>> PendingDaysAsync(string tenantId, int take, CancellationToken ct = default);
    Task<IReadOnlyList<EssPunch>> ListDayAsync(string tenantId, string employee, DateOnly workDate, CancellationToken ct = default);
    Task MarkProcessedAsync(string tenantId, IReadOnlyCollection<Guid> ids, string? attendanceName, string? error, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>In-memory store for development and testing. Data is lost on application restart.</summary>
public sealed class InMemoryEssPunchStore : IEssPunchStore
{
    private readonly ConcurrentDictionary<Guid, EssPunch> _punches = new();

    public Task<(EssPunch Punch, bool Created)> InsertAsync(EssPunch punch, CancellationToken ct = default)
    {
        var stored = _punches.GetOrAdd(punch.Id, punch);
        return Task.FromResult((stored, ReferenceEquals(stored, punch)));
    }

    public Task<EssPunch?> GetAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_punches.TryGetValue(id, out var p) && p.TenantId == tenantId ? p : null);

    public Task<EssPunch?> LastCountedAsync(string tenantId, string employee, DateTimeOffset before, CancellationToken ct = default) =>
        Task.FromResult(_punches.Values
            .Where(p => p.TenantId == tenantId && p.Employee == employee && p.Counts && p.PunchTime < before)
            .OrderByDescending(p => p.PunchTime).FirstOrDefault());

    public Task<IReadOnlyList<EssPunch>> ListAsync(string tenantId, string employee, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EssPunch>>(_punches.Values
            .Where(p => p.TenantId == tenantId && p.Employee == employee && p.WorkDate >= from && p.WorkDate <= to)
            .OrderBy(p => p.PunchTime).ToList());

    public Task<bool> SetPhotoAsync(string tenantId, Guid id, string employee, string fileId, CancellationToken ct = default)
    {
        while (_punches.TryGetValue(id, out var p) && p.TenantId == tenantId && p.Employee == employee)
            if (_punches.TryUpdate(id, p with { PhotoFileId = fileId, ProcessedAt = null }, p)) return Task.FromResult(true);
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<(string Employee, DateOnly WorkDate)>> PendingDaysAsync(string tenantId, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, DateOnly)>>(_punches.Values
            .Where(p => p.TenantId == tenantId && p.ProcessedAt is null && p.Counts)
            .GroupBy(p => (p.Employee, p.WorkDate))
            .OrderBy(g => g.Key.WorkDate).ThenBy(g => g.Key.Employee, StringComparer.Ordinal)
            .Take(take).Select(g => g.Key).ToList());

    public Task<IReadOnlyList<EssPunch>> ListDayAsync(string tenantId, string employee, DateOnly workDate, CancellationToken ct = default) =>
        ListAsync(tenantId, employee, workDate, workDate, ct);

    public Task MarkProcessedAsync(string tenantId, IReadOnlyCollection<Guid> ids, string? attendanceName, string? error, DateTimeOffset at, CancellationToken ct = default)
    {
        foreach (var id in ids)
            while (_punches.TryGetValue(id, out var p) && p.TenantId == tenantId)
                if (_punches.TryUpdate(id, p with { AttendanceName = attendanceName ?? p.AttendanceName, ProcessError = error, ProcessedAt = at }, p)) break;
        return Task.CompletedTask;
    }
}

/// <summary>PostgreSQL store: executes a single lean INSERT ... ON CONFLICT DO NOTHING per punch (bypassing document hooks, naming series, and document audit tables during peak hours).</summary>
public sealed class PostgresEssPunchStore(NpgsqlDataSource db) : IEssPunchStore
{
    private const string Columns =
        "id, tenant_id, employee, user_id, punch_type, status, reasons, server_time, device_time, punch_time, work_date, latitude, longitude, " +
        "accuracy_m, location_code, distance_m, is_mock_location, is_offline, device_id, device_info, app_version, note, photo_file_id, " +
        "attendance_name, processed_at, process_error";

    public async Task<(EssPunch Punch, bool Created)> InsertAsync(EssPunch p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO ess_attendance_punches (id, tenant_id, employee, user_id, punch_type, status, reasons, server_time, device_time, punch_time, " +
            "work_date, latitude, longitude, accuracy_m, location_code, distance_m, is_mock_location, is_offline, device_id, device_info, app_version, note) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(p.Id);
        cmd.Parameters.AddWithValue(p.TenantId);
        cmd.Parameters.AddWithValue(p.Employee);
        cmd.Parameters.AddWithValue(p.UserId);
        cmd.Parameters.AddWithValue(p.PunchType);
        cmd.Parameters.AddWithValue(p.Status);
        cmd.Parameters.Add(new NpgsqlParameter { Value = p.Reasons.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(p.ServerTime.ToUniversalTime());
        cmd.Parameters.Add(Nullable(p.DeviceTime?.ToUniversalTime(), NpgsqlDbType.TimestampTz));
        cmd.Parameters.AddWithValue(p.PunchTime.ToUniversalTime());
        cmd.Parameters.AddWithValue(p.WorkDate);
        cmd.Parameters.Add(Nullable(p.Latitude, NpgsqlDbType.Numeric));
        cmd.Parameters.Add(Nullable(p.Longitude, NpgsqlDbType.Numeric));
        cmd.Parameters.Add(Nullable(p.AccuracyM, NpgsqlDbType.Numeric));
        cmd.Parameters.Add(Nullable(p.LocationCode, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.DistanceM, NpgsqlDbType.Numeric));
        cmd.Parameters.AddWithValue(p.IsMockLocation);
        cmd.Parameters.AddWithValue(p.IsOffline);
        cmd.Parameters.Add(Nullable(p.DeviceId, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.DeviceInfo, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.AppVersion, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.Note, NpgsqlDbType.Text));
        if (await cmd.ExecuteNonQueryAsync(ct) == 1) return (p, true);
        // Replay with identical ID: return the previously persisted record.
        var existing = await GetByIdAsync(p.Id, ct) ?? throw new InvalidOperationException("Punch conflict could not be resolved");
        return (existing, false);
    }

    public async Task<EssPunch?> GetAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        await GetByIdAsync(id, ct) is { } p && p.TenantId == tenantId ? p : null;

    private async Task<EssPunch?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM ess_attendance_punches WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return (await ReadAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<EssPunch?> LastCountedAsync(string tenantId, string employee, DateTimeOffset before, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"SELECT {Columns} FROM ess_attendance_punches WHERE tenant_id = $1 AND employee = $2 AND status <> 'rejected' " +
            "AND work_date >= $4 AND punch_time < $3 ORDER BY punch_time DESC LIMIT 1");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(employee);
        cmd.Parameters.AddWithValue(before.ToUniversalTime());
        cmd.Parameters.AddWithValue(DateOnly.FromDateTime(before.UtcDateTime).AddDays(-2)); // uses index (tenant_id, employee, work_date, punch_time)
        return (await ReadAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<EssPunch>> ListAsync(string tenantId, string employee, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"SELECT {Columns} FROM ess_attendance_punches WHERE tenant_id = $1 AND employee = $2 AND work_date BETWEEN $3 AND $4 ORDER BY punch_time");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(employee);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        return await ReadAsync(cmd, ct);
    }

    public async Task<bool> SetPhotoAsync(string tenantId, Guid id, string employee, string fileId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE ess_attendance_punches SET photo_file_id = $4, processed_at = NULL WHERE id = $1 AND tenant_id = $2 AND employee = $3");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(employee);
        cmd.Parameters.AddWithValue(fileId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<(string Employee, DateOnly WorkDate)>> PendingDaysAsync(string tenantId, int take, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT employee, work_date FROM ess_attendance_punches WHERE tenant_id = $1 AND processed_at IS NULL AND status <> 'rejected' " +
            "GROUP BY employee, work_date ORDER BY work_date, employee LIMIT $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(take);
        var result = new List<(string, DateOnly)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add((r.GetString(0), r.GetFieldValue<DateOnly>(1)));
        return result;
    }

    public Task<IReadOnlyList<EssPunch>> ListDayAsync(string tenantId, string employee, DateOnly workDate, CancellationToken ct = default) =>
        ListAsync(tenantId, employee, workDate, workDate, ct);

    public async Task MarkProcessedAsync(string tenantId, IReadOnlyCollection<Guid> ids, string? attendanceName, string? error, DateTimeOffset at, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await using var cmd = db.CreateCommand(
            "UPDATE ess_attendance_punches SET attendance_name = COALESCE($3, attendance_name), process_error = $4, processed_at = $5 " +
            "WHERE tenant_id = $1 AND id = ANY($2)");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(ids.ToArray());
        cmd.Parameters.Add(Nullable(attendanceName, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(error, NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue(at.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) =>
        new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    private static async Task<IReadOnlyList<EssPunch>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<EssPunch>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new EssPunch
            {
                Id = r.GetGuid(0),
                TenantId = r.GetString(1),
                Employee = r.GetString(2),
                UserId = r.GetString(3),
                PunchType = r.GetString(4),
                Status = r.GetString(5),
                Reasons = r.GetFieldValue<string[]>(6),
                ServerTime = r.GetFieldValue<DateTimeOffset>(7),
                DeviceTime = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
                PunchTime = r.GetFieldValue<DateTimeOffset>(9),
                WorkDate = r.GetFieldValue<DateOnly>(10),
                Latitude = r.IsDBNull(11) ? null : r.GetDecimal(11),
                Longitude = r.IsDBNull(12) ? null : r.GetDecimal(12),
                AccuracyM = r.IsDBNull(13) ? null : r.GetDecimal(13),
                LocationCode = r.IsDBNull(14) ? null : r.GetString(14),
                DistanceM = r.IsDBNull(15) ? null : r.GetDecimal(15),
                IsMockLocation = r.GetBoolean(16),
                IsOffline = r.GetBoolean(17),
                DeviceId = r.IsDBNull(18) ? null : r.GetString(18),
                DeviceInfo = r.IsDBNull(19) ? null : r.GetString(19),
                AppVersion = r.IsDBNull(20) ? null : r.GetString(20),
                Note = r.IsDBNull(21) ? null : r.GetString(21),
                PhotoFileId = r.IsDBNull(22) ? null : r.GetString(22),
                AttendanceName = r.IsDBNull(23) ? null : r.GetString(23),
                ProcessedAt = r.IsDBNull(24) ? null : r.GetFieldValue<DateTimeOffset>(24),
                ProcessError = r.IsDBNull(25) ? null : r.GetString(25),
            });
        }
        return list;
    }
}
