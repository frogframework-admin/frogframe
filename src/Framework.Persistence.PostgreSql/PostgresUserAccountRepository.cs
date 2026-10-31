using Framework.Security;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresUserAccountRepository(NpgsqlDataSource db) : IUserAccountRepository
{
    private const string Columns =
        "id, user_name, normalized_user_name, email, normalized_email, full_name, tenant_id, department, employee_id, roles, is_active, " +
        "password_hash, security_stamp, concurrency_stamp, lockout_enabled, lockout_end, access_failed_count, created_at, last_login_at, avatar_id";

    private static UserAccount Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), UserName = r.GetString(1), NormalizedUserName = r.GetString(2),
        Email = r.IsDBNull(3) ? null : r.GetString(3), NormalizedEmail = r.IsDBNull(4) ? null : r.GetString(4),
        FullName = r.GetString(5), TenantId = r.GetString(6),
        Department = r.IsDBNull(7) ? null : r.GetString(7), EmployeeId = r.IsDBNull(8) ? null : r.GetString(8),
        Roles = r.GetFieldValue<string[]>(9).ToList(), IsActive = r.GetBoolean(10),
        PasswordHash = r.IsDBNull(11) ? null : r.GetString(11), SecurityStamp = r.GetString(12), ConcurrencyStamp = r.GetString(13),
        LockoutEnabled = r.GetBoolean(14), LockoutEnd = r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        AccessFailedCount = r.GetInt32(16), CreatedAt = r.GetFieldValue<DateTimeOffset>(17),
        LastLoginAt = r.IsDBNull(18) ? null : r.GetFieldValue<DateTimeOffset>(18),
        AvatarId = r.IsDBNull(19) ? null : r.GetString(19),
    };

    private async Task<UserAccount?> One(string where, object value, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM docframe_users WHERE {where} = $1 LIMIT 1");
        cmd.Parameters.AddWithValue(value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public Task<UserAccount?> FindByIdAsync(string id, CancellationToken ct = default) => One("id", id, ct);
    public Task<UserAccount?> FindByNormalizedUserNameAsync(string n, CancellationToken ct = default) => One("normalized_user_name", n, ct);
    public Task<UserAccount?> FindByNormalizedEmailAsync(string n, CancellationToken ct = default) => One("normalized_email", n, ct);

    public async Task<IReadOnlyList<UserAccount>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM docframe_users WHERE tenant_id = $1 ORDER BY full_name");
        cmd.Parameters.AddWithValue(tenantId);
        var list = new List<UserAccount>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM docframe_users)");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task CreateAsync(UserAccount a, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_users ({Columns}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20)");
        AddParams(cmd, a);
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateAccountException($"Username '{a.UserName}' sudah dipakai");
        }
    }

    public async Task UpdateAsync(UserAccount a, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE docframe_users SET user_name=$2, normalized_user_name=$3, email=$4, normalized_email=$5, full_name=$6, tenant_id=$7, " +
            "department=$8, employee_id=$9, roles=$10, is_active=$11, password_hash=$12, security_stamp=$13, concurrency_stamp=$14, " +
            "lockout_enabled=$15, lockout_end=$16, access_failed_count=$17, created_at=$18, last_login_at=$19, avatar_id=$20 WHERE id=$1");
        AddParams(cmd, a);
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateAccountException($"Username '{a.UserName}' sudah dipakai");
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM docframe_users WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParams(NpgsqlCommand cmd, UserAccount a)
    {
        object N(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue(a.Id); cmd.Parameters.AddWithValue(a.UserName); cmd.Parameters.AddWithValue(a.NormalizedUserName);
        cmd.Parameters.AddWithValue(N(a.Email)); cmd.Parameters.AddWithValue(N(a.NormalizedEmail)); cmd.Parameters.AddWithValue(a.FullName);
        cmd.Parameters.AddWithValue(a.TenantId); cmd.Parameters.AddWithValue(N(a.Department)); cmd.Parameters.AddWithValue(N(a.EmployeeId));
        cmd.Parameters.AddWithValue(a.Roles.ToArray()); cmd.Parameters.AddWithValue(a.IsActive); cmd.Parameters.AddWithValue(N(a.PasswordHash));
        cmd.Parameters.AddWithValue(a.SecurityStamp); cmd.Parameters.AddWithValue(a.ConcurrencyStamp); cmd.Parameters.AddWithValue(a.LockoutEnabled);
        cmd.Parameters.AddWithValue(N(a.LockoutEnd)); cmd.Parameters.AddWithValue(a.AccessFailedCount); cmd.Parameters.AddWithValue(a.CreatedAt);
        cmd.Parameters.AddWithValue(N(a.LastLoginAt)); cmd.Parameters.AddWithValue(N(a.AvatarId));
    }
}
