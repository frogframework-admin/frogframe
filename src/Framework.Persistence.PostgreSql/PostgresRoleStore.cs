using Framework.Security;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresRoleStore(NpgsqlDataSource db, TimeProvider clock) : IRoleStore
{
    private const string Cols = "id, tenant_id, name, description, is_system, created_at, updated_at";

    public event Action? Changed;

    private static RoleDef Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        TenantId = r.GetString(1),
        Name = r.GetString(2),
        Description = r.IsDBNull(3) ? null : r.GetString(3),
        IsSystem = r.GetBoolean(4),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(5),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(6)
    };

    public async Task<IReadOnlyList<RoleDef>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM docframe_roles WHERE tenant_id = $1 ORDER BY is_system DESC, name ASC");
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<RoleDef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));

        if (list.Count == 0)
        {
            await EnsureDefaultRolesAsync(tenantId, ct);
            return await ListAsync(tenantId, ct);
        }

        return list;
    }

    public async Task<RoleDef?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM docframe_roles WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<RoleDef?> GetByNameAsync(string name, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM docframe_roles WHERE tenant_id = $1 AND LOWER(name) = LOWER($2)");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task SaveAsync(RoleDef role, CancellationToken ct = default)
    {
        role.UpdatedAt = clock.GetUtcNow();
        if (role.CreatedAt == default) role.CreatedAt = role.UpdatedAt;
        if (string.IsNullOrWhiteSpace(role.Id)) role.Id = Guid.NewGuid().ToString("N");

        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_roles ({Cols}) VALUES ($1,$2,$3,$4,$5,$6,$7) " +
            "ON CONFLICT (id) DO UPDATE SET name=$3, description=$4, is_system=$5, updated_at=$7");

        cmd.Parameters.AddWithValue(role.Id);
        cmd.Parameters.AddWithValue(role.TenantId);
        cmd.Parameters.AddWithValue(role.Name);
        cmd.Parameters.AddWithValue((object?)role.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(role.IsSystem);
        cmd.Parameters.AddWithValue(role.CreatedAt);
        cmd.Parameters.AddWithValue(role.UpdatedAt);

        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id, string tenantId, CancellationToken ct = default)
    {
        var item = await GetAsync(id, tenantId, ct);
        if (item is null) return;
        if (item.IsSystem || string.Equals(item.Name, "Administrator", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Name, "System Manager", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Role '{item.Name}' adalah role sistem bawaan dan tidak dapat dihapus.");
        }

        await using var cmd = db.CreateCommand("DELETE FROM docframe_roles WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task EnsureDefaultRolesAsync(string tenantId, CancellationToken ct = default)
    {
        await using var chkCmd = db.CreateCommand("SELECT name FROM docframe_roles WHERE tenant_id = $1");
        chkCmd.Parameters.AddWithValue(tenantId);
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var r = await chkCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) existingNames.Add(r.GetString(0));
        }

        var defaults = RoleDefaults.CreateDefaultRoles(tenantId, clock.GetUtcNow());
        var added = false;

        foreach (var def in defaults)
        {
            if (!existingNames.Contains(def.Name))
            {
                await SaveAsync(def, ct);
                existingNames.Add(def.Name);
                added = true;
            }
        }

        if (added) Changed?.Invoke();
    }
}

