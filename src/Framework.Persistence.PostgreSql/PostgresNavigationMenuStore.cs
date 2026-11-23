using System.Collections.Concurrent;
using Framework.Navigation;
using Framework.Security;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresNavigationMenuStore(NpgsqlDataSource db, TimeProvider clock) : INavigationMenuStore
{
    private const string ApplicationCols = "id, tenant_id, code, name, description, icon, sort_order, allowed_roles, is_system, is_active, created_at, updated_at";
    private const string ModuleCols = "id, tenant_id, name, description, icon, sort_order, allowed_roles, is_system, is_active, created_at, updated_at, code, application_id";
    private const string GroupCols = "id, tenant_id, module_id, name, icon, sort_order, is_system, is_active, created_at, updated_at";
    private const string MenuCols = "id, tenant_id, title, href, icon, group_id, group_name, sort_order, allowed_roles, is_system, is_active, badge_key, created_at, updated_at";

    private readonly ConcurrentDictionary<string, bool> seededTenants = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    private static NavigationApplication ReadApplication(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        TenantId = r.GetString(1),
        Code = r.GetString(2),
        Name = r.GetString(3),
        Description = r.IsDBNull(4) ? null : r.GetString(4),
        Icon = r.GetString(5),
        SortOrder = r.GetInt32(6),
        AllowedRoles = r.GetFieldValue<string[]>(7).ToList(),
        IsSystem = r.GetBoolean(8),
        IsActive = r.GetBoolean(9),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(10),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(11)
    };

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
        Code = r.IsDBNull(11) ? "" : r.GetString(11),
        ApplicationId = r.IsDBNull(12) ? null : r.GetString(12)
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

    public async Task<IReadOnlyList<NavigationApplication>> ListApplicationsAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureSeededOnceAsync(tenantId, ct);
        var sql = onlyActive
            ? $"SELECT {ApplicationCols} FROM frogframe_navigation_applications WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, name"
            : $"SELECT {ApplicationCols} FROM frogframe_navigation_applications WHERE tenant_id = $1 ORDER BY sort_order, name";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationApplication>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadApplication(r));
        return list;
    }

    public async Task<NavigationApplication?> GetApplicationAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {ApplicationCols} FROM frogframe_navigation_applications WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadApplication(r) : null;
    }

    public async Task SaveApplicationAsync(NavigationApplication application, CancellationToken ct = default)
    {
        application.UpdatedAt = clock.GetUtcNow();
        if (application.CreatedAt == default) application.CreatedAt = application.UpdatedAt;
        if (string.IsNullOrWhiteSpace(application.Id)) application.Id = Guid.NewGuid().ToString("N");
        application.Code = NavigationApplication.NormalizeCode(application.Code);

        await using (var cur = db.CreateCommand("SELECT code FROM frogframe_navigation_applications WHERE id = $1 AND tenant_id = $2"))
        {
            cur.Parameters.AddWithValue(application.Id);
            cur.Parameters.AddWithValue(application.TenantId);
            if (await cur.ExecuteScalarAsync(ct) is string existingCode && existingCode.Length > 0 && existingCode != application.Code)
                throw new InvalidOperationException($"Application code '{existingCode}' cannot be changed once created.");
        }

        await using var cmd = db.CreateCommand(
            $"INSERT INTO frogframe_navigation_applications ({ApplicationCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) " +
            "ON CONFLICT (id) DO UPDATE SET code=$3, name=$4, description=$5, icon=$6, sort_order=$7, allowed_roles=$8, is_system=$9, is_active=$10, updated_at=$12");

        cmd.Parameters.AddWithValue(application.Id);
        cmd.Parameters.AddWithValue(application.TenantId);
        cmd.Parameters.AddWithValue(application.Code);
        cmd.Parameters.AddWithValue(application.Name);
        cmd.Parameters.AddWithValue((object?)application.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(application.Icon);
        cmd.Parameters.AddWithValue(application.SortOrder);
        cmd.Parameters.AddWithValue(application.AllowedRoles.ToArray());
        cmd.Parameters.AddWithValue(application.IsSystem);
        cmd.Parameters.AddWithValue(application.IsActive);
        cmd.Parameters.AddWithValue(application.CreatedAt);
        cmd.Parameters.AddWithValue(application.UpdatedAt);

        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new InvalidOperationException($"Application code '{application.Code}' is already in use.");
        }
        Changed?.Invoke();
    }

    public async Task DeleteApplicationAsync(string id, string tenantId, CancellationToken ct = default)
    {
        var app = await GetApplicationAsync(id, tenantId, ct);
        if (app is null) return;
        if (app.IsSystem) throw new InvalidOperationException("System applications cannot be deleted.");

        await using var cmd = db.CreateCommand("DELETE FROM frogframe_navigation_applications WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<NavigationModule>> ListModulesAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureSeededOnceAsync(tenantId, ct);
        var sql = onlyActive
            ? $"SELECT {ModuleCols} FROM frogframe_navigation_modules WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, name"
            : $"SELECT {ModuleCols} FROM frogframe_navigation_modules WHERE tenant_id = $1 ORDER BY sort_order, name";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationModule>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadModule(r));
        return list;
    }

    public async Task<NavigationModule?> GetModuleAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {ModuleCols} FROM frogframe_navigation_modules WHERE id = $1 AND tenant_id = $2");
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
        await using (var cur = db.CreateCommand("SELECT code FROM frogframe_navigation_modules WHERE id = $1 AND tenant_id = $2"))
        {
            cur.Parameters.AddWithValue(module.Id); cur.Parameters.AddWithValue(module.TenantId);
            if (await cur.ExecuteScalarAsync(ct) is string existingCode && existingCode.Length > 0 && existingCode != module.Code)
                throw new InvalidOperationException($"Kode modul '{existingCode}' tidak bisa diubah karena dipakai DocType.");
        }

        await using var cmd = db.CreateCommand(
            $"INSERT INTO frogframe_navigation_modules ({ModuleCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) " +
            "ON CONFLICT (id) DO UPDATE SET name=$3, description=$4, icon=$5, sort_order=$6, allowed_roles=$7, is_system=$8, is_active=$9, updated_at=$11, code=$12, application_id=$13");

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
        cmd.Parameters.AddWithValue((object?)module.ApplicationId ?? DBNull.Value);

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

        await using (var chk = db.CreateCommand("SELECT COUNT(1) FROM frogframe_navigation_menu_groups WHERE tenant_id = $1 AND module_id = $2"))
        {
            chk.Parameters.AddWithValue(tenantId);
            chk.Parameters.AddWithValue(id);
            var count = Convert.ToInt64(await chk.ExecuteScalarAsync(ct));
            if (count > 0)
                throw new InvalidOperationException("Modul masih memiliki group menu. Pindahkan atau hapus group menu terlebih dahulu.");
        }

        await using var cmd = db.CreateCommand("DELETE FROM frogframe_navigation_modules WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<NavigationMenuGroup>> ListGroupsAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureSeededOnceAsync(tenantId, ct);
        var sql = onlyActive
            ? $"SELECT {GroupCols} FROM frogframe_navigation_menu_groups WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, name"
            : $"SELECT {GroupCols} FROM frogframe_navigation_menu_groups WHERE tenant_id = $1 ORDER BY sort_order, name";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationMenuGroup>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadGroup(r));
        return list;
    }

    public async Task<NavigationMenuGroup?> GetGroupAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {GroupCols} FROM frogframe_navigation_menu_groups WHERE id = $1 AND tenant_id = $2");
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
            $"INSERT INTO frogframe_navigation_menu_groups ({GroupCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) " +
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
        await using (var syncCmd = db.CreateCommand("UPDATE frogframe_navigation_menus SET group_name = $1, updated_at = $2 WHERE tenant_id = $3 AND group_id = $4"))
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

        await using (var chk = db.CreateCommand("SELECT COUNT(1) FROM frogframe_navigation_menus WHERE tenant_id = $1 AND (group_id = $2 OR LOWER(group_name) = LOWER($3))"))
        {
            chk.Parameters.AddWithValue(tenantId);
            chk.Parameters.AddWithValue(id);
            chk.Parameters.AddWithValue(grp.Name);
            var count = Convert.ToInt64(await chk.ExecuteScalarAsync(ct));
            if (count > 0)
                throw new InvalidOperationException("Group menu masih memiliki item menu. Pindahkan atau hapus item menu terlebih dahulu.");
        }

        await using var cmd = db.CreateCommand("DELETE FROM frogframe_navigation_menu_groups WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<NavigationMenuItem>> ListAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureSeededOnceAsync(tenantId, ct);
        var sql = onlyActive
            ? $"SELECT {MenuCols} FROM frogframe_navigation_menus WHERE tenant_id = $1 AND is_active = true ORDER BY sort_order, title"
            : $"SELECT {MenuCols} FROM frogframe_navigation_menus WHERE tenant_id = $1 ORDER BY sort_order, title";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);

        var list = new List<NavigationMenuItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadMenu(r));
        return list;
    }

    public async Task<NavigationMenuItem?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {MenuCols} FROM frogframe_navigation_menus WHERE id = $1 AND tenant_id = $2");
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
            $"INSERT INTO frogframe_navigation_menus ({MenuCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) " +
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

        await using var cmd = db.CreateCommand("DELETE FROM frogframe_navigation_menus WHERE id = $1 AND tenant_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    public async Task EnsureDefaultMenusAsync(string tenantId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var added = false;

        // 0. Seed default applications
        var existingAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingAppCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var chkApp = db.CreateCommand("SELECT id, code FROM frogframe_navigation_applications WHERE tenant_id = $1"))
        {
            chkApp.Parameters.AddWithValue(tenantId);
            await using var r = await chkApp.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                existingAppIds.Add(r.GetString(0));
                existingAppCodes.Add(r.GetString(1));
            }
        }

        foreach (var defApp in NavigationMenuDefaults.CreateDefaultApplications(tenantId, now))
        {
            if (!existingAppIds.Contains(defApp.Id) && !existingAppCodes.Contains(defApp.Code))
            {
                await SaveApplicationInternalAsync(defApp, ct);
                existingAppIds.Add(defApp.Id);
                existingAppCodes.Add(defApp.Code);
                added = true;
            }
        }

        // 1. Seed default modules
        var existingModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var chkMod = db.CreateCommand("SELECT id, name FROM frogframe_navigation_modules WHERE tenant_id = $1"))
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
            else if (!string.IsNullOrWhiteSpace(defMod.ApplicationId))
            {
                await using var updModApp = db.CreateCommand(
                    "UPDATE frogframe_navigation_modules SET application_id = $2 WHERE tenant_id = $1 AND (id = $3 OR name = $4 OR code = $5) AND (application_id IS NULL OR application_id = '' OR (is_system = true AND application_id <> $2))");
                updModApp.Parameters.AddWithValue(tenantId);
                updModApp.Parameters.AddWithValue(defMod.ApplicationId);
                updModApp.Parameters.AddWithValue(defMod.Id);
                updModApp.Parameters.AddWithValue(defMod.Name);
                updModApp.Parameters.AddWithValue(defMod.Code);
                if (await updModApp.ExecuteNonQueryAsync(ct) > 0) added = true;
            }
        }

        // 2. Seed default menu groups
        var existingGrpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var chkGrp = db.CreateCommand("SELECT id, name FROM frogframe_navigation_menu_groups WHERE tenant_id = $1"))
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
        await using (var chkMenu = db.CreateCommand("SELECT href FROM frogframe_navigation_menus WHERE tenant_id = $1"))
        {
            chkMenu.Parameters.AddWithValue(tenantId);
            await using var r = await chkMenu.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) existingHrefs.Add(r.GetString(0));
        }

        foreach (var defItem in NavigationMenuDefaults.CreateDefaultItems(tenantId, now))
        {
            if (!existingHrefs.Contains(defItem.Href))
            {
                if (string.IsNullOrWhiteSpace(defItem.GroupId) && !string.IsNullOrWhiteSpace(defItem.GroupName) && groupIdByName.TryGetValue(defItem.GroupName, out var gid))
                {
                    defItem.GroupId = gid;
                }
                await SaveMenuInternalAsync(defItem, ct);
                existingHrefs.Add(defItem.Href);
                added = true;
            }
            else if (defItem.IsSystem)
            {
                await using var updTitleCmd = db.CreateCommand(
                    "UPDATE frogframe_navigation_menus SET title = $3, sort_order = $4, updated_at = $5 WHERE tenant_id = $1 AND href = $2 AND is_system = true AND (title <> $3 OR sort_order <> $4)");
                updTitleCmd.Parameters.AddWithValue(tenantId);
                updTitleCmd.Parameters.AddWithValue(defItem.Href);
                updTitleCmd.Parameters.AddWithValue(defItem.Title);
                updTitleCmd.Parameters.AddWithValue(defItem.SortOrder);
                updTitleCmd.Parameters.AddWithValue(now);
                if (await updTitleCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
            }
        }

        // 4. Backfill group_id on any existing menu items where group_id is NULL
        await using (var backfillCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus m " +
            "SET group_id = g.id " +
            "FROM frogframe_navigation_menu_groups g " +
            "WHERE m.tenant_id = $1 AND g.tenant_id = $1 AND (m.group_id IS NULL OR m.group_id = '') AND m.group_name <> '' AND LOWER(m.group_name) = LOWER(g.name)"))
        {
            backfillCmd.Parameters.AddWithValue(tenantId);
            var updated = await backfillCmd.ExecuteNonQueryAsync(ct);
            if (updated > 0) added = true;
        }

        // 5. Move report_templates to Metadata & Customization group (below Dashboards, sort_order 70) and update href/roles/icon
        await using (var updateReportCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Metadata & Customization', href = 'admin/report-templates', icon = 'file-spreadsheet', allowed_roles = $3, sort_order = 70 " +
            "WHERE tenant_id = $1 AND (id = $4 OR href = 'report-templates' OR href = 'admin/report-templates') AND (group_id <> $2 OR group_name <> 'Metadata & Customization' OR sort_order <> 70 OR href <> 'admin/report-templates')"))
        {
            updateReportCmd.Parameters.AddWithValue(tenantId);
            updateReportCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupAdminMetadataId(tenantId));
            updateReportCmd.Parameters.AddWithValue(new string[] { UserContext.SystemManager, UserContext.Administrator });
            updateReportCmd.Parameters.AddWithValue($"menu_{tenantId}_report_templates");
            var moved = await updateReportCmd.ExecuteNonQueryAsync(ct);
            if (moved > 0) added = true;
        }

        // 6. Move/update widgets menu item to Metadata & Customization group with href admin/widgets, sort_order 55 (above Dashboards)
        await using (var updateWidgetCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Metadata & Customization', href = 'admin/widgets', icon = 'layout-grid', allowed_roles = $3, sort_order = 55 " +
            "WHERE tenant_id = $1 AND (id = $4 OR href = 'admin/widgets' OR href = 'dashboard/widgets' OR href = 'widgets') AND (group_id <> $2 OR group_name <> 'Metadata & Customization' OR sort_order <> 55 OR href <> 'admin/widgets')"))
        {
            updateWidgetCmd.Parameters.AddWithValue(tenantId);
            updateWidgetCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupAdminMetadataId(tenantId));
            updateWidgetCmd.Parameters.AddWithValue(new string[] { UserContext.SystemManager, UserContext.Administrator });
            updateWidgetCmd.Parameters.AddWithValue($"menu_{tenantId}_widgets");
            var movedWidget = await updateWidgetCmd.ExecuteNonQueryAsync(ct);
            if (movedWidget > 0) added = true;
        }

        await using (var delOldWidgetCmd = db.CreateCommand(
            "DELETE FROM frogframe_navigation_menus WHERE tenant_id = $1 AND (href = 'dashboard/widgets' OR href = 'widgets') AND id <> $2"))
        {
            delOldWidgetCmd.Parameters.AddWithValue(tenantId);
            delOldWidgetCmd.Parameters.AddWithValue($"menu_{tenantId}_widgets");
            var delCount = await delOldWidgetCmd.ExecuteNonQueryAsync(ct);
            if (delCount > 0) added = true;
        }

        // 7. Ensure menu item for dashboards has title 'Dashboards', group 'Metadata & Customization', and sort_order 60
        await using (var updateDashboardsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET title = 'Dashboards', group_id = $3, group_name = 'Metadata & Customization', sort_order = 60 " +
            "WHERE tenant_id = $1 AND (id = $2 OR href = 'admin/dashboards') AND (title <> 'Dashboards' OR group_id <> $3 OR sort_order <> 60)"))
        {
            updateDashboardsCmd.Parameters.AddWithValue(tenantId);
            updateDashboardsCmd.Parameters.AddWithValue($"menu_{tenantId}_dashboards_admin");
            updateDashboardsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupAdminMetadataId(tenantId));
            var renamed = await updateDashboardsCmd.ExecuteNonQueryAsync(ct);
            if (renamed > 0) added = true;
        }

        // 8. Purge legacy Department menu items (replaced by OrganizationUnit)
        await using (var delDeptCmd = db.CreateCommand(
            "DELETE FROM frogframe_navigation_menus WHERE tenant_id = $1 AND (href = 'app/Department' OR id = $2)"))
        {
            delDeptCmd.Parameters.AddWithValue(tenantId);
            delDeptCmd.Parameters.AddWithValue($"menu_{tenantId}_department");
            var delDeptCount = await delDeptCmd.ExecuteNonQueryAsync(ct);
            if (delDeptCount > 0) added = true;
        }

        // 8b. Migrate Payroll system menus from broad 'HR' role to 'Payroll' role
        await using (var migratePayrollCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET allowed_roles = array_replace(allowed_roles, 'HR', 'Payroll'), updated_at = $2 " +
            "WHERE tenant_id = $1 AND is_system = true " +
            "AND href IN ('app/PayrollRun', 'app/Payslip', 'app/SalaryAssignment', 'app/SalaryComponent', 'app/PayrollSettings') " +
            "AND 'HR' = ANY(allowed_roles) AND NOT ('Payroll' = ANY(allowed_roles))"))
        {
            migratePayrollCmd.Parameters.AddWithValue(tenantId);
            migratePayrollCmd.Parameters.AddWithValue(now);
            if (await migratePayrollCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        // 9. Remove deprecated ANALYTICS module and its groups
        await using (var delAnalyticsModCmd = db.CreateCommand(
            "DELETE FROM frogframe_navigation_modules WHERE tenant_id = $1 AND (code = 'ANALYTICS' OR id = $2)"))
        {
            delAnalyticsModCmd.Parameters.AddWithValue(tenantId);
            delAnalyticsModCmd.Parameters.AddWithValue(NavigationMenuDefaults.ModuleAnalyticsId(tenantId));
            var delModCount = await delAnalyticsModCmd.ExecuteNonQueryAsync(ct);
            if (delModCount > 0) added = true;
        }

        await using (var delAnalyticsGrpCmd = db.CreateCommand(
            "DELETE FROM frogframe_navigation_menu_groups WHERE tenant_id = $1 AND (module_id = $2 OR id = $3 OR id = $4)"))
        {
            delAnalyticsGrpCmd.Parameters.AddWithValue(tenantId);
            delAnalyticsGrpCmd.Parameters.AddWithValue(NavigationMenuDefaults.ModuleAnalyticsId(tenantId));
            delAnalyticsGrpCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupUtamaId(tenantId));
            delAnalyticsGrpCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupLaporanId(tenantId));
            var delGrpCount = await delAnalyticsGrpCmd.ExecuteNonQueryAsync(ct);
            if (delGrpCount > 0) added = true;
        }

        // 10. Ensure universal menu items (Home, Inbox, Dashboard, Reports) have empty allowed_roles, null group_id, and proper sort order
        await using (var updateUniversalCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = NULL, group_name = '', allowed_roles = '{}', sort_order = CASE " +
            "  WHEN href = '' OR id = $2 THEN 10 " +
            "  WHEN href = 'inbox' OR id = $3 THEN 20 " +
            "  WHEN href = 'dashboard' OR id = $4 THEN 30 " +
            "  WHEN href = 'reports' OR id = $5 THEN 40 " +
            "  ELSE sort_order END " +
            "WHERE tenant_id = $1 AND (href IN ('', 'inbox', 'dashboard', 'reports') OR id IN ($2, $3, $4, $5)) " +
            "AND (group_id IS NOT NULL OR group_name <> '' OR allowed_roles <> '{}' OR sort_order NOT IN (10, 20, 30, 40))"))
        {
            updateUniversalCmd.Parameters.AddWithValue(tenantId);
            updateUniversalCmd.Parameters.AddWithValue($"menu_{tenantId}_beranda");
            updateUniversalCmd.Parameters.AddWithValue($"menu_{tenantId}_inbox");
            updateUniversalCmd.Parameters.AddWithValue($"menu_{tenantId}_dashboard");
            updateUniversalCmd.Parameters.AddWithValue($"menu_{tenantId}_reports");
            var updatedUniversal = await updateUniversalCmd.ExecuteNonQueryAsync(ct);
            if (updatedUniversal > 0) added = true;
        }

        // 11. Migrate deprecated Lucide icon names on existing menu items and groups
        await using (var fixMenuIconsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus SET icon = CASE icon " +
            "  WHEN 'play-circle' THEN 'circle-play' " +
            "  WHEN 'file-question' THEN 'file-question-mark' " +
            "  WHEN 'check-square' THEN 'square-check-big' " +
            "  ELSE icon END " +
            "WHERE tenant_id = $1 AND icon IN ('play-circle', 'file-question', 'check-square')"))
        {
            fixMenuIconsCmd.Parameters.AddWithValue(tenantId);
            if (await fixMenuIconsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var fixGroupIconsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menu_groups SET icon = 'sliders-horizontal' " +
            "WHERE tenant_id = $1 AND icon = 'sliders'"))
        {
            fixGroupIconsCmd.Parameters.AddWithValue(tenantId);
            if (await fixGroupIconsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        // 12. Rename 'Employees & Structure' to 'Personnel Management' and move organization/job design items to 'Organization and Job Design'
        await using (var renameHrEmpGrpCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menu_groups SET name = 'Personnel Management', updated_at = $3 " +
            "WHERE tenant_id = $1 AND id = $2 AND is_system = true AND name <> 'Personnel Management'"))
        {
            renameHrEmpGrpCmd.Parameters.AddWithValue(tenantId);
            renameHrEmpGrpCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrEmployeesId(tenantId));
            renameHrEmpGrpCmd.Parameters.AddWithValue(now);
            if (await renameHrEmpGrpCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var updateHrEmpMenuGroupNameCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus SET group_name = 'Personnel Management', updated_at = $3 " +
            "WHERE tenant_id = $1 AND group_id = $2 AND is_system = true AND group_name <> 'Personnel Management'"))
        {
            updateHrEmpMenuGroupNameCmd.Parameters.AddWithValue(tenantId);
            updateHrEmpMenuGroupNameCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrEmployeesId(tenantId));
            updateHrEmpMenuGroupNameCmd.Parameters.AddWithValue(now);
            if (await updateHrEmpMenuGroupNameCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var moveOrgJobCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Organization and Job Design', updated_at = $3 " +
            "WHERE tenant_id = $1 AND is_system = true " +
            "AND href IN ('app/OrganizationUnitType', 'app/OrganizationUnit', 'hr/organizational-chart', 'app/JobFamilyGroup', 'app/JobFamily', 'app/JobProfile', 'app/Position', 'app/Grade') " +
            "AND (group_id IS DISTINCT FROM $2 OR group_name <> 'Organization and Job Design')"))
        {
            moveOrgJobCmd.Parameters.AddWithValue(tenantId);
            moveOrgJobCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrOrgJobDesignId(tenantId));
            moveOrgJobCmd.Parameters.AddWithValue(now);
            if (await moveOrgJobCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        // 13. Split 'Time & Attendance' into 'Attendance & Daily Tracking', 'Leave & Time Off', and 'Shift & Roster Management'
        await using (var renameAttGrpCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menu_groups SET name = 'Attendance & Daily Tracking', icon = 'clock', updated_at = $3 " +
            "WHERE tenant_id = $1 AND id = $2 AND is_system = true AND (name <> 'Attendance & Daily Tracking' OR icon <> 'clock')"))
        {
            renameAttGrpCmd.Parameters.AddWithValue(tenantId);
            renameAttGrpCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrAttendanceId(tenantId));
            renameAttGrpCmd.Parameters.AddWithValue(now);
            if (await renameAttGrpCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var moveAttItemsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Attendance & Daily Tracking', updated_at = $3 " +
            "WHERE tenant_id = $1 AND is_system = true " +
            "AND href IN ('app/Attendance', 'app/AttendanceCorrection', 'app/EmployeeDailySchedule', 'hr/attendance-recap', 'app/FaceRegistration') " +
            "AND (group_id IS DISTINCT FROM $2 OR group_name <> 'Attendance & Daily Tracking')"))
        {
            moveAttItemsCmd.Parameters.AddWithValue(tenantId);
            moveAttItemsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrAttendanceId(tenantId));
            moveAttItemsCmd.Parameters.AddWithValue(now);
            if (await moveAttItemsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var moveLeaveItemsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Leave & Time Off', updated_at = $3 " +
            "WHERE tenant_id = $1 AND is_system = true " +
            "AND href IN ('app/LeaveApplication', 'app/LeaveAllocation', 'app/HolidayList', 'app/LeaveType', 'app/LeavePolicy') " +
            "AND (group_id IS DISTINCT FROM $2 OR group_name <> 'Leave & Time Off')"))
        {
            moveLeaveItemsCmd.Parameters.AddWithValue(tenantId);
            moveLeaveItemsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrLeaveId(tenantId));
            moveLeaveItemsCmd.Parameters.AddWithValue(now);
            if (await moveLeaveItemsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var moveShiftItemsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Shift & Roster Management', updated_at = $3 " +
            "WHERE tenant_id = $1 AND is_system = true " +
            "AND href IN ('hr/roster-calendar', 'app/OvertimeRequest', 'app/ShiftSwapRequest', 'app/ShiftOverride', 'app/ShiftPreference', 'app/ShiftAssignment', 'app/ScheduleGeneration', 'app/ScheduleOptimization', 'app/ShiftType', 'app/ShiftPattern', 'app/ShiftGroup', 'app/ShiftRequirement', 'app/WorkScheduleRule') " +
            "AND (group_id IS DISTINCT FROM $2 OR group_name <> 'Shift & Roster Management')"))
        {
            moveShiftItemsCmd.Parameters.AddWithValue(tenantId);
            moveShiftItemsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrShiftRosterId(tenantId));
            moveShiftItemsCmd.Parameters.AddWithValue(now);
            if (await moveShiftItemsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        // 14. Split 'Talent & Recruitment' into 'Recruitment' and 'Talent Management'
        await using (var renameRecGrpCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menu_groups SET name = 'Recruitment', updated_at = $3 " +
            "WHERE tenant_id = $1 AND id = $2 AND is_system = true AND name = 'Talent & Recruitment'"))
        {
            renameRecGrpCmd.Parameters.AddWithValue(tenantId);
            renameRecGrpCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrRecruitmentId(tenantId));
            renameRecGrpCmd.Parameters.AddWithValue(now);
            if (await renameRecGrpCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var moveRecItemsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Recruitment', updated_at = $3, sort_order = CASE href " +
            "  WHEN 'app/JobRequisition' THEN 10 " +
            "  WHEN 'app/JobPosting' THEN 20 " +
            "  WHEN 'app/JobApplicant' THEN 30 " +
            "  WHEN 'app/Interview' THEN 40 " +
            "  WHEN 'app/JobOffer' THEN 50 " +
            "  ELSE sort_order END " +
            "WHERE tenant_id = $1 AND is_system = true " +
            "AND href IN ('app/JobRequisition', 'app/JobPosting', 'app/JobApplicant', 'app/Interview', 'app/JobOffer') " +
            "AND (group_id IS DISTINCT FROM $2 OR group_name <> 'Recruitment' OR sort_order IS DISTINCT FROM CASE href " +
            "  WHEN 'app/JobRequisition' THEN 10 " +
            "  WHEN 'app/JobPosting' THEN 20 " +
            "  WHEN 'app/JobApplicant' THEN 30 " +
            "  WHEN 'app/Interview' THEN 40 " +
            "  WHEN 'app/JobOffer' THEN 50 " +
            "  ELSE sort_order END)"))
        {
            moveRecItemsCmd.Parameters.AddWithValue(tenantId);
            moveRecItemsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrRecruitmentId(tenantId));
            moveRecItemsCmd.Parameters.AddWithValue(now);
            if (await moveRecItemsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var moveTalentItemsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Talent Management', updated_at = $3, sort_order = CASE href " +
            "  WHEN 'app/Competency' THEN 10 " +
            "  WHEN 'app/AppraisalCycle' THEN 20 " +
            "  WHEN 'app/Appraisal' THEN 30 " +
            "  WHEN 'app/TalentReview' THEN 40 " +
            "  WHEN 'app/SuccessionPlan' THEN 50 " +
            "  WHEN 'app/DevelopmentPlan' THEN 60 " +
            "  WHEN 'app/TrainingProgram' THEN 70 " +
            "  WHEN 'app/TrainingRequest' THEN 80 " +
            "  WHEN 'app/TrainingEvent' THEN 90 " +
            "  ELSE sort_order END " +
            "WHERE tenant_id = $1 AND is_system = true " +
            "AND href IN ('app/Competency', 'app/AppraisalCycle', 'app/Appraisal', 'app/TalentReview', 'app/SuccessionPlan', 'app/DevelopmentPlan', 'app/TrainingProgram', 'app/TrainingRequest', 'app/TrainingEvent') " +
            "AND (group_id IS DISTINCT FROM $2 OR group_name <> 'Talent Management' OR sort_order IS DISTINCT FROM CASE href " +
            "  WHEN 'app/Competency' THEN 10 " +
            "  WHEN 'app/AppraisalCycle' THEN 20 " +
            "  WHEN 'app/Appraisal' THEN 30 " +
            "  WHEN 'app/TalentReview' THEN 40 " +
            "  WHEN 'app/SuccessionPlan' THEN 50 " +
            "  WHEN 'app/DevelopmentPlan' THEN 60 " +
            "  WHEN 'app/TrainingProgram' THEN 70 " +
            "  WHEN 'app/TrainingRequest' THEN 80 " +
            "  WHEN 'app/TrainingEvent' THEN 90 " +
            "  ELSE sort_order END)"))
        {
            moveTalentItemsCmd.Parameters.AddWithValue(tenantId);
            moveTalentItemsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrTalentId(tenantId));
            moveTalentItemsCmd.Parameters.AddWithValue(now);
            if (await moveTalentItemsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        // 15. HR Settings is placed under Documents (remove explicit menu item so DeskLayout renders it in Documents)
        await using (var delHrSettingsCmd = db.CreateCommand(
            "DELETE FROM frogframe_navigation_menus WHERE tenant_id = $1 AND is_system = true AND (href = 'app/HRSettings' OR id = $2)"))
        {
            delHrSettingsCmd.Parameters.AddWithValue(tenantId);
            delHrSettingsCmd.Parameters.AddWithValue($"menu_{tenantId}_hr_settings");
            if (await delHrSettingsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        // 16. Move OnboardingList, OffboardingList, and KpiDefinition to Documents; move Location to Organization and Job Design; reorder HR groups
        await using (var delDocsMenusCmd = db.CreateCommand(
            "DELETE FROM frogframe_navigation_menus WHERE tenant_id = $1 AND is_system = true " +
            "AND (href IN ('app/OnboardingList', 'app/OffboardingList', 'app/KpiDefinition') OR id IN ($2, $3, $4))"))
        {
            delDocsMenusCmd.Parameters.AddWithValue(tenantId);
            delDocsMenusCmd.Parameters.AddWithValue($"menu_{tenantId}_onboarding_list");
            delDocsMenusCmd.Parameters.AddWithValue($"menu_{tenantId}_offboarding_list");
            delDocsMenusCmd.Parameters.AddWithValue($"menu_{tenantId}_kpi_definition");
            if (await delDocsMenusCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var moveLocationCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menus " +
            "SET group_id = $2, group_name = 'Organization and Job Design', sort_order = 90, updated_at = $3 " +
            "WHERE tenant_id = $1 AND is_system = true AND href = 'app/Location' " +
            "AND (group_id IS DISTINCT FROM $2 OR group_name <> 'Organization and Job Design' OR sort_order IS DISTINCT FROM 90)"))
        {
            moveLocationCmd.Parameters.AddWithValue(tenantId);
            moveLocationCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrOrgJobDesignId(tenantId));
            moveLocationCmd.Parameters.AddWithValue(now);
            if (await moveLocationCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        await using (var reorderHrGroupsCmd = db.CreateCommand(
            "UPDATE frogframe_navigation_menu_groups SET sort_order = CASE id " +
            "  WHEN $2 THEN 10 " +
            "  WHEN $3 THEN 20 " +
            "  WHEN $4 THEN 30 " +
            "  WHEN $5 THEN 40 " +
            "  WHEN $6 THEN 50 " +
            "  WHEN $7 THEN 60 " +
            "  WHEN $8 THEN 70 " +
            "  WHEN $9 THEN 80 " +
            "  ELSE sort_order END, updated_at = $10 " +
            "WHERE tenant_id = $1 AND is_system = true AND id IN ($2, $3, $4, $5, $6, $7, $8, $9) " +
            "AND sort_order IS DISTINCT FROM CASE id " +
            "  WHEN $2 THEN 10 " +
            "  WHEN $3 THEN 20 " +
            "  WHEN $4 THEN 30 " +
            "  WHEN $5 THEN 40 " +
            "  WHEN $6 THEN 50 " +
            "  WHEN $7 THEN 60 " +
            "  WHEN $8 THEN 70 " +
            "  WHEN $9 THEN 80 " +
            "  ELSE sort_order END"))
        {
            reorderHrGroupsCmd.Parameters.AddWithValue(tenantId);
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrOrgJobDesignId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrRecruitmentId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrEmployeesId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrShiftRosterId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrLeaveId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrAttendanceId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrPayrollId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(NavigationMenuDefaults.GroupHrTalentId(tenantId));
            reorderHrGroupsCmd.Parameters.AddWithValue(now);
            if (await reorderHrGroupsCmd.ExecuteNonQueryAsync(ct) > 0) added = true;
        }

        seededTenants[tenantId] = true;
        if (added) Changed?.Invoke();
    }

    private async Task SaveApplicationInternalAsync(NavigationApplication application, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO frogframe_navigation_applications ({ApplicationCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue(application.Id);
        cmd.Parameters.AddWithValue(application.TenantId);
        cmd.Parameters.AddWithValue(application.Code);
        cmd.Parameters.AddWithValue(application.Name);
        cmd.Parameters.AddWithValue((object?)application.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(application.Icon);
        cmd.Parameters.AddWithValue(application.SortOrder);
        cmd.Parameters.AddWithValue(application.AllowedRoles.ToArray());
        cmd.Parameters.AddWithValue(application.IsSystem);
        cmd.Parameters.AddWithValue(application.IsActive);
        cmd.Parameters.AddWithValue(application.CreatedAt);
        cmd.Parameters.AddWithValue(application.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SaveModuleInternalAsync(NavigationModule module, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO frogframe_navigation_modules ({ModuleCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) " +
            "ON CONFLICT (id) DO UPDATE SET application_id = EXCLUDED.application_id WHERE frogframe_navigation_modules.application_id IS NULL OR frogframe_navigation_modules.application_id = ''");
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
        cmd.Parameters.AddWithValue((object?)module.ApplicationId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SaveGroupInternalAsync(NavigationMenuGroup group, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO frogframe_navigation_menu_groups ({GroupCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) ON CONFLICT (id) DO NOTHING");
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
            $"INSERT INTO frogframe_navigation_menus ({MenuCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) ON CONFLICT (id) DO NOTHING");
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
