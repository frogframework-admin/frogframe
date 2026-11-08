using System.Collections.Concurrent;
using Framework.Navigation;
using Framework.Security;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresNavigationMenuStore(NpgsqlDataSource db, TimeProvider clock) : INavigationMenuStore
{
    private const string ModuleCols = "id, tenant_id, name, description, icon, sort_order, allowed_roles, is_system, is_active, created_at, updated_at, code";
    private const string GroupCols = "id, tenant_id, module_id, name, icon, sort_order, is_system, is_active, created_at, updated_at";
    private const string MenuCols = "id, tenant_id, title, href, icon, group_id, group_name, sort_order, allowed_roles, is_system, is_active, badge_key, created_at, updated_at";

    private readonly ConcurrentDictionary<string, bool> seededTenants = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    private static NavigationModule ReadModule(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        TenantId = r.GetString(1),
        Name = r.GetString(2),
        Description = r.IsDBNull(3) ? null : r.GetString(3),
        Icon = r.GetString(4),
        SortOrder = r.GetInt32(5),
        AllowedRoles = r.GetFieldValue<string[]>(6).ToList(),
        IsSystem = r.GetBoolean(7),
        IsActive = r.GetBoolean(8),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(9),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(10),
        Code = r.IsDBNull(11) ? "" : r.GetString(11)
    };

    private static NavigationMenuGroup ReadGroup(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        TenantId = r.GetString(1),
        ModuleId = r.GetString(2),
        Name = r.GetString(3),
        Icon = r.GetString(4),
        SortOrder = r.GetInt32(5),
        IsSystem = r.GetBoolean(6),
        IsActive = r.GetBoolean(7),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(8),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(9)
    };

    private static NavigationMenuItem ReadMenu(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        TenantId = r.GetString(1),
        Title = r.GetString(2),
        Href = r.GetString(3),
        Icon = r.GetString(4),
        GroupId = r.IsDBNull(5) ? null : r.GetString(5),
        GroupName = r.GetString(6),
        SortOrder = r.GetInt32(7),
        AllowedRoles = r.GetFieldValue<string[]>(8).ToList(),
        IsSystem = r.GetBoolean(9),
        IsActive = r.GetBoolean(10),
        BadgeKey = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(12),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(13)
    };

    private async Task EnsureSeededOnceAsync(string tenantId, CancellationToken ct)
    {
        if (seededTenants.ContainsKey(tenantId)) return;
        await EnsureDefaultMenusAsync(tenantId, ct);
        seededTenants[tenantId] = true;
    }

    public async Task<IReadOnlyList<NavigationModule>> ListModulesAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureSeededOnceAsync(tenantId, ct);
        var sql = onlyActive
            ? $"SELECT {ModuleCols} FROM docframe_navigation_modules WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, name"
            : $"SELECT {ModuleCols} FROM docframe_navigation_modules WHERE tenant_id = $1 ORDER BY sort_order, name";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationModule>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadModule(r));
        return list;
    }

    public async Task<NavigationModule?> GetModuleAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {ModuleCols} FROM docframe_navigation_modules WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadModule(r) : null;
    }

    public async Task SaveModuleAsync(NavigationModule module, CancellationToken ct = default)
    {
        module.UpdatedAt = clock.GetUtcNow();
        if (module.CreatedAt == default) module.CreatedAt = module.UpdatedAt;
        if (string.IsNullOrWhiteSpace(module.Id)) module.Id = Guid.NewGuid().ToString("N");
        module.Code = NavigationModule.NormalizeCode(module.Code);

        // Kode adalah kunci referensi DocType: setelah tersimpan tidak boleh berubah.
        await using (var cur = db.CreateCommand("SELECT code FROM docframe_navigation_modules WHERE id = $1 AND tenant_id = $2"))
        {
            cur.Parameters.AddWithValue(module.Id); cur.Parameters.AddWithValue(module.TenantId);
            if (await cur.ExecuteScalarAsync(ct) is string existingCode && existingCode.Length > 0 && existingCode != module.Code)
                throw new InvalidOperationException($"Kode modul '{existingCode}' tidak bisa diubah karena dipakai DocType.");
        }

        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_navigation_modules ({ModuleCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) " +
            "ON CONFLICT (id) DO UPDATE SET name=$3, description=$4, icon=$5, sort_order=$6, allowed_roles=$7, is_system=$8, is_active=$9, updated_at=$11, code=$12");

        cmd.Parameters.AddWithValue(module.Id);
        cmd.Parameters.AddWithValue(module.TenantId);
        cmd.Parameters.AddWithValue(module.Name);
        cmd.Parameters.AddWithValue((object?)module.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(module.Icon);
        cmd.Parameters.AddWithValue(module.SortOrder);
        cmd.Parameters.AddWithValue(module.AllowedRoles.ToArray());
        cmd.Parameters.AddWithValue(module.IsSystem);
        cmd.Parameters.AddWithValue(module.IsActive);
        cmd.Parameters.AddWithValue(module.CreatedAt);
        cmd.Parameters.AddWithValue(module.UpdatedAt);
        cmd.Parameters.AddWithValue(module.Code);

        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new InvalidOperationException($"Kode modul '{module.Code}' sudah dipakai modul lain.");
        }
        Changed?.Invoke();
    }

    public async Task DeleteModuleAsync(string id, string tenantId, CancellationToken ct = default)
    {
        var mod = await GetModuleAsync(id, tenantId, ct);
        if (mod is null) return;
        if (mod.IsSystem) throw new InvalidOperationException("Modul sistem tidak dapat dihapus.");

        await using (var chk = db.CreateCommand("SELECT COUNT(1) FROM docframe_navigation_menu_groups WHERE tenant_id = $1 AND module_id = $2"))
        {
            chk.Parameters.AddWithValue(tenantId);
            chk.Parameters.AddWithValue(id);
            var count = Convert.ToInt64(await chk.ExecuteScalarAsync(ct));
            if (count > 0)
                throw new InvalidOperationException("Modul masih memiliki group menu. Pindahkan atau hapus group menu terlebih dahulu.");
        }

        await using var cmd = db.CreateCommand("DELETE FROM docframe_navigation_modules WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<NavigationMenuGroup>> ListGroupsAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureSeededOnceAsync(tenantId, ct);
        var sql = onlyActive
            ? $"SELECT {GroupCols} FROM docframe_navigation_menu_groups WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, name"
            : $"SELECT {GroupCols} FROM docframe_navigation_menu_groups WHERE tenant_id = $1 ORDER BY sort_order, name";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationMenuGroup>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadGroup(r));
        return list;
    }

    public async Task<NavigationMenuGroup?> GetGroupAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {GroupCols} FROM docframe_navigation_menu_groups WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadGroup(r) : null;
    }

    public async Task SaveGroupAsync(NavigationMenuGroup group, CancellationToken ct = default)
    {
        group.UpdatedAt = clock.GetUtcNow();
        if (group.CreatedAt == default) group.CreatedAt = group.UpdatedAt;
        if (string.IsNullOrWhiteSpace(group.Id)) group.Id = Guid.NewGuid().ToString("N");

        await using (var cmd = db.CreateCommand(
            $"INSERT INTO docframe_navigation_menu_groups ({GroupCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) " +
            "ON CONFLICT (id) DO UPDATE SET module_id=$3, name=$4, icon=$5, sort_order=$6, is_system=$7, is_active=$8, updated_at=$10"))
        {
            cmd.Parameters.AddWithValue(group.Id);
            cmd.Parameters.AddWithValue(group.TenantId);
            cmd.Parameters.AddWithValue(group.ModuleId);
            cmd.Parameters.AddWithValue(group.Name);
            cmd.Parameters.AddWithValue(group.Icon);
            cmd.Parameters.AddWithValue(group.SortOrder);
            cmd.Parameters.AddWithValue(group.IsSystem);
            cmd.Parameters.AddWithValue(group.IsActive);
            cmd.Parameters.AddWithValue(group.CreatedAt);
            cmd.Parameters.AddWithValue(group.UpdatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Keep group_name synchronized on child menu items referencing this group_id.
        await using (var syncCmd = db.CreateCommand("UPDATE docframe_navigation_menus SET group_name = $1, updated_at = $2 WHERE tenant_id = $3 AND group_id = $4"))
        {
            syncCmd.Parameters.AddWithValue(group.Name);
            syncCmd.Parameters.AddWithValue(group.UpdatedAt);
            syncCmd.Parameters.AddWithValue(group.TenantId);
            syncCmd.Parameters.AddWithValue(group.Id);
            await syncCmd.ExecuteNonQueryAsync(ct);
        }

        Changed?.Invoke();
    }

    public async Task DeleteGroupAsync(string id, string tenantId, CancellationToken ct = default)
    {
        var grp = await GetGroupAsync(id, tenantId, ct);
        if (grp is null) return;
        if (grp.IsSystem) throw new InvalidOperationException("Group menu sistem tidak dapat dihapus.");

        await using (var chk = db.CreateCommand("SELECT COUNT(1) FROM docframe_navigation_menus WHERE tenant_id = $1 AND (group_id = $2 OR LOWER(group_name) = LOWER($3))"))
        {
            chk.Parameters.AddWithValue(tenantId);
            chk.Parameters.AddWithValue(id);
            chk.Parameters.AddWithValue(grp.Name);
            var count = Convert.ToInt64(await chk.ExecuteScalarAsync(ct));
            if (count > 0)
                throw new InvalidOperationException("Group menu masih memiliki item menu. Pindahkan atau hapus item menu terlebih dahulu.");
        }

        await using var cmd = db.CreateCommand("DELETE FROM docframe_navigation_menu_groups WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<NavigationMenuItem>> ListAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureSeededOnceAsync(tenantId, ct);
        var sql = onlyActive
            ? $"SELECT {MenuCols} FROM docframe_navigation_menus WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, title"
            : $"SELECT {MenuCols} FROM docframe_navigation_menus WHERE tenant_id = $1 ORDER BY sort_order, title";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationMenuItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadMenu(r));
        return list;
    }

    public async Task<NavigationMenuItem?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {MenuCols} FROM docframe_navigation_menus WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadMenu(r) : null;
    }

    public async Task SaveAsync(NavigationMenuItem item, CancellationToken ct = default)
    {
        item.UpdatedAt = clock.GetUtcNow();
        if (item.CreatedAt == default) item.CreatedAt = item.UpdatedAt;
        if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");

        if (!string.IsNullOrWhiteSpace(item.GroupId))
        {
            var grp = await GetGroupAsync(item.GroupId, item.TenantId, ct);
            if (grp is not null) item.GroupName = grp.Name;
        }

        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_navigation_menus ({MenuCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) " +
            "ON CONFLICT (id) DO UPDATE SET title=$3, href=$4, icon=$5, group_id=$6, group_name=$7, sort_order=$8, allowed_roles=$9, is_system=$10, is_active=$11, badge_key=$12, updated_at=$14");

        cmd.Parameters.AddWithValue(item.Id);
        cmd.Parameters.AddWithValue(item.TenantId);
        cmd.Parameters.AddWithValue(item.Title);
        cmd.Parameters.AddWithValue(item.Href);
        cmd.Parameters.AddWithValue(item.Icon);
        cmd.Parameters.AddWithValue((object?)item.GroupId ?? DBNull.Value);
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
        var now = clock.GetUtcNow();
        var added = false;

        // 1. Seed default modules
        var existingModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var chkMod = db.CreateCommand("SELECT id, name FROM docframe_navigation_modules WHERE tenant_id = $1"))
        {
            chkMod.Parameters.AddWithValue(tenantId);
            await using var r = await chkMod.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                existingModIds.Add(r.GetString(0));
                existingModNames.Add(r.GetString(1));
            }
        }

        foreach (var defMod in NavigationMenuDefaults.CreateDefaultModules(tenantId, now))
        {
            if (!existingModIds.Contains(defMod.Id) && !existingModNames.Contains(defMod.Name))
            {
                await SaveModuleInternalAsync(defMod, ct);
                existingModIds.Add(defMod.Id);
                existingModNames.Add(defMod.Name);
                added = true;
            }
        }

        // 2. Seed default menu groups
        var existingGrpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var chkGrp = db.CreateCommand("SELECT id, name FROM docframe_navigation_menu_groups WHERE tenant_id = $1"))
        {
            chkGrp.Parameters.AddWithValue(tenantId);
            await using var r = await chkGrp.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                var name = r.GetString(1);
                existingGrpIds.Add(id);
                groupIdByName[name] = id;
            }
        }

        foreach (var defGrp in NavigationMenuDefaults.CreateDefaultGroups(tenantId, now))
        {
            if (!existingGrpIds.Contains(defGrp.Id) && !groupIdByName.ContainsKey(defGrp.Name))
            {
                await SaveGroupInternalAsync(defGrp, ct);
                existingGrpIds.Add(defGrp.Id);
                groupIdByName[defGrp.Name] = defGrp.Id;
                added = true;
            }
        }

        // 3. Seed default menu items
        var existingHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var chkMenu = db.CreateCommand("SELECT href FROM docframe_navigation_menus WHERE tenant_id = $1"))
        {
            chkMenu.Parameters.AddWithValue(tenantId);
            await using var r = await chkMenu.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) existingHrefs.Add(r.GetString(0));
        }

        foreach (var defItem in NavigationMenuDefaults.CreateDefaultItems(tenantId, now))
        {
            if (!existingHrefs.Contains(defItem.Href))
            {
                if (string.IsNullOrWhiteSpace(defItem.GroupId) && groupIdByName.TryGetValue(defItem.GroupName, out var gid))
                {
                    defItem.GroupId = gid;
                }
                await SaveMenuInternalAsync(defItem, ct);
                existingHrefs.Add(defItem.Href);
                added = true;
            }
        }

        // 4. Backfill group_id on any existing menu items where group_id is NULL
        await using (var backfillCmd = db.CreateCommand(
            "UPDATE docframe_navigation_menus m " +
            "SET group_id = g.id " +
            "FROM docframe_navigation_menu_groups g " +
            "WHERE m.tenant_id = $1 AND g.tenant_id = $1 AND (m.group_id IS NULL OR m.group_id = '') AND LOWER(m.group_name) = LOWER(g.name)"))
        {
            backfillCmd.Parameters.AddWithValue(tenantId);
            var updated = await backfillCmd.ExecuteNonQueryAsync(ct);
            if (updated > 0) added = true;
        }

        // 5. Move report_templates to System group if it is still in the old Reports group, and update href/roles
        await using (var updateReportCmd = db.CreateCommand(
            "UPDATE docframe_navigation_menus " +
            "SET group_id = $2, group_name = 'System', href = 'admin/report-templates', allowed_roles = $3, sort_order = 115 " +
            "WHERE tenant_id = $1 AND (id = $4 OR href = 'report-templates' OR href = 'admin/report-templates') AND (group_name <> 'System' OR href <> 'admin/report-templates')"))
        {
            updateReportCmd.Parameters.AddWithValue(tenantId);
            updateReportCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupSistemId(tenantId));
            updateReportCmd.Parameters.AddWithValue(new string[] { UserContext.SystemManager, UserContext.Administrator });
            updateReportCmd.Parameters.AddWithValue($"menu_{tenantId}_report_templates");
            var moved = await updateReportCmd.ExecuteNonQueryAsync(ct);
            if (moved > 0) added = true;
        }

        // 6. Move/update any legacy widgets menu item to System group with href admin/widgets, and purge old analytic widgets menus
        await using (var updateWidgetCmd = db.CreateCommand(
            "UPDATE docframe_navigation_menus " +
            "SET group_id = $2, group_name = 'System', href = 'admin/widgets', icon = 'layout-grid', allowed_roles = $3, sort_order = 118 " +
            "WHERE tenant_id = $1 AND (id = $4 OR href = 'dashboard/widgets' OR href = 'widgets') AND (group_name <> 'System' OR href <> 'admin/widgets')"))
        {
            updateWidgetCmd.Parameters.AddWithValue(tenantId);
            updateWidgetCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupSistemId(tenantId));
            updateWidgetCmd.Parameters.AddWithValue(new string[] { UserContext.SystemManager, UserContext.Administrator });
            updateWidgetCmd.Parameters.AddWithValue($"menu_{tenantId}_widgets");
            var movedWidget = await updateWidgetCmd.ExecuteNonQueryAsync(ct);
            if (movedWidget > 0) added = true;
        }

        await using (var delOldWidgetCmd = db.CreateCommand(
            "DELETE FROM docframe_navigation_menus WHERE tenant_id = $1 AND (href = 'dashboard/widgets' OR href = 'widgets') AND id <> $2"))
        {
            delOldWidgetCmd.Parameters.AddWithValue(tenantId);
            delOldWidgetCmd.Parameters.AddWithValue($"menu_{tenantId}_widgets");
            var delCount = await delOldWidgetCmd.ExecuteNonQueryAsync(ct);
            if (delCount > 0) added = true;
        }

        seededTenants[tenantId] = true;
        if (added) Changed?.Invoke();
    }

    private async Task SaveModuleInternalAsync(NavigationModule module, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_navigation_modules ({ModuleCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue(module.Id);
        cmd.Parameters.AddWithValue(module.TenantId);
        cmd.Parameters.AddWithValue(module.Name);
        cmd.Parameters.AddWithValue((object?)module.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(module.Icon);
        cmd.Parameters.AddWithValue(module.SortOrder);
        cmd.Parameters.AddWithValue(module.AllowedRoles.ToArray());
        cmd.Parameters.AddWithValue(module.IsSystem);
        cmd.Parameters.AddWithValue(module.IsActive);
        cmd.Parameters.AddWithValue(module.CreatedAt);
        cmd.Parameters.AddWithValue(module.UpdatedAt);
        cmd.Parameters.AddWithValue(module.Code);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SaveGroupInternalAsync(NavigationMenuGroup group, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_navigation_menu_groups ({GroupCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(group.Id);
        cmd.Parameters.AddWithValue(group.TenantId);
        cmd.Parameters.AddWithValue(group.ModuleId);
        cmd.Parameters.AddWithValue(group.Name);
        cmd.Parameters.AddWithValue(group.Icon);
        cmd.Parameters.AddWithValue(group.SortOrder);
        cmd.Parameters.AddWithValue(group.IsSystem);
        cmd.Parameters.AddWithValue(group.IsActive);
        cmd.Parameters.AddWithValue(group.CreatedAt);
        cmd.Parameters.AddWithValue(group.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SaveMenuInternalAsync(NavigationMenuItem item, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_navigation_menus ({MenuCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(item.Id);
        cmd.Parameters.AddWithValue(item.TenantId);
        cmd.Parameters.AddWithValue(item.Title);
        cmd.Parameters.AddWithValue(item.Href);
        cmd.Parameters.AddWithValue(item.Icon);
        cmd.Parameters.AddWithValue((object?)item.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(item.GroupName);
        cmd.Parameters.AddWithValue(item.SortOrder);
        cmd.Parameters.AddWithValue(item.AllowedRoles.ToArray());
        cmd.Parameters.AddWithValue(item.IsSystem);
        cmd.Parameters.AddWithValue(item.IsActive);
        cmd.Parameters.AddWithValue((object?)item.BadgeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue(item.CreatedAt);
        cmd.Parameters.AddWithValue(item.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
