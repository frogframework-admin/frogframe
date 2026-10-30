using Framework.Navigation;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresNavigationMenuStore(NpgsqlDataSource db, TimeProvider clock) : INavigationMenuStore
{
    private const string Cols = "id, tenant_id, title, href, icon, group_name, sort_order, allowed_roles, is_system, is_active, badge_key, created_at, updated_at";

    public event Action? Changed;

    private static NavigationMenuItem Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        TenantId = r.GetString(1),
        Title = r.GetString(2),
        Href = r.GetString(3),
        Icon = r.GetString(4),
        GroupName = r.GetString(5),
        SortOrder = r.GetInt32(6),
        AllowedRoles = r.GetFieldValue<string[]>(7).ToList(),
        IsSystem = r.GetBoolean(8),
        IsActive = r.GetBoolean(9),
        BadgeKey = r.IsDBNull(10) ? null : r.GetString(10),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(11),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(12)
    };

    public async Task<IReadOnlyList<NavigationMenuItem>> ListAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        var sql = onlyActive
            ? $"SELECT {Cols} FROM docframe_navigation_menus WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, title"
            : $"SELECT {Cols} FROM docframe_navigation_menus WHERE tenant_id = $1 ORDER BY sort_order, title";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationMenuItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));

        if (list.Count == 0)
        {
            await EnsureDefaultMenusAsync(tenantId, ct);
            return await ListAsync(tenantId, onlyActive, ct);
        }

        return list;
    }

    public async Task<NavigationMenuItem?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM docframe_navigation_menus WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task SaveAsync(NavigationMenuItem item, CancellationToken ct = default)
    {
        item.UpdatedAt = clock.GetUtcNow();
        if (item.CreatedAt == default) item.CreatedAt = item.UpdatedAt;
        if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");

        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_navigation_menus ({Cols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) " +
            "ON CONFLICT (id) DO UPDATE SET title=$3, href=$4, icon=$5, group_name=$6, sort_order=$7, allowed_roles=$8, is_system=$9, is_active=$10, badge_key=$11, updated_at=$13");

        cmd.Parameters.AddWithValue(item.Id);
        cmd.Parameters.AddWithValue(item.TenantId);
        cmd.Parameters.AddWithValue(item.Title);
        cmd.Parameters.AddWithValue(item.Href);
        cmd.Parameters.AddWithValue(item.Icon);
        cmd.Parameters.AddWithValue(item.GroupName);
        cmd.Parameters.AddWithValue(item.SortOrder);
        cmd.Parameters.AddWithValue(item.AllowedRoles.ToArray());
        cmd.Parameters.AddWithValue(item.IsSystem);
        cmd.Parameters.AddWithValue(item.IsActive);
        cmd.Parameters.AddWithValue((object?)item.BadgeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue(item.CreatedAt);
        cmd.Parameters.AddWithValue(item.UpdatedAt);

        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id, string tenantId, CancellationToken ct = default)
    {
        var item = await GetAsync(id, tenantId, ct);
        if (item is null) return;
        if (item.IsSystem) throw new InvalidOperationException("Menu sistem tidak dapat dihapus.");

        await using var cmd = db.CreateCommand("DELETE FROM docframe_navigation_menus WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task EnsureDefaultMenusAsync(string tenantId, CancellationToken ct = default)
    {
        // Ambil href yang sudah ada di database untuk tenant ini
        await using var chkCmd = db.CreateCommand("SELECT href FROM docframe_navigation_menus WHERE tenant_id = $1");
        chkCmd.Parameters.AddWithValue(tenantId);
        var existingHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var r = await chkCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) existingHrefs.Add(r.GetString(0));
        }

        var defaults = NavigationMenuDefaults.CreateDefaultItems(tenantId, clock.GetUtcNow());
        var added = false;

        foreach (var def in defaults)
        {
            if (!existingHrefs.Contains(def.Href))
            {
                await SaveAsync(def, ct);
                existingHrefs.Add(def.Href);
                added = true;
            }
        }

        if (added) Changed?.Invoke();
    }
}

