using Framework.Navigation;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    private INavigationMenuStore NavMenus => sp.GetRequiredService<INavigationMenuStore>();

    [Test]
    public async Task NavigationMenu_DefaultSeeding_HasUniversalAndEssentialMenus()
    {
        var tenantId = "acme";
        var items = await NavMenus.ListAsync(tenantId, onlyActive: false);

        // Universal menu items: Home, Dashboard, Inbox
        var beranda = items.FirstOrDefault(x => x.Href == "");
        Assert.True(beranda is not null && (beranda.Title == "Home" || beranda.Title == "Beranda") && beranda.AllowedRoles.Count == 0, "Home menu should be for all employees");

        var dashboard = items.FirstOrDefault(x => x.Href == "dashboard");
        Assert.True(dashboard is not null && dashboard.Title == "Dashboard" && dashboard.AllowedRoles.Count == 0, "Dashboard should be for all employees");

        var inbox = items.FirstOrDefault(x => x.Href == "inbox");
        Assert.True(inbox is not null && (inbox.Title == "Inbox" || inbox.Title == "Kotak masuk") && inbox.BadgeKey == NavigationMenuDefaults.BadgeInboxUnread, "Inbox should have unread badge");

        // Menu Wajib Sistem: Metadata DocType, API Query, Kunci API, Pengguna, Laporan
        var metadata = items.FirstOrDefault(x => x.Href == "admin/metadata");
        Assert.True(metadata is not null && metadata.IsSystem && metadata.AllowedRoles.Contains(UserContext.SystemManager), "Metadata DocType harus di-seed");

        var workflows = items.FirstOrDefault(x => x.Href == "admin/workflows");
        Assert.True(workflows is not null && workflows.IsSystem && workflows.AllowedRoles.Contains(UserContext.SystemManager), "Workflow harus di-seed untuk System Manager");

        var queryApis = items.FirstOrDefault(x => x.Href == "query-apis");
        Assert.True(queryApis is not null && queryApis.IsSystem && queryApis.AllowedRoles.Contains(UserContext.SystemManager), "API Query harus di-seed");

        var apiKeys = items.FirstOrDefault(x => x.Href == "admin/api-keys");
        Assert.True(apiKeys is not null && apiKeys.IsSystem && apiKeys.AllowedRoles.Contains(UserContext.SystemManager), "Kunci API harus di-seed");

        var users = items.FirstOrDefault(x => x.Href == "admin/users");
        Assert.True(users is not null && users.IsSystem && users.AllowedRoles.Contains(UserContext.SystemManager), "Pengguna harus di-seed");

        var reports = items.FirstOrDefault(x => x.Href == "reports");
        Assert.True(reports is not null && reports.IsSystem && reports.AllowedRoles.Contains(UserContext.SystemManager), "Laporan harus di-seed");

        var menuAdmin = items.FirstOrDefault(x => x.Href == "admin/menus");
        Assert.True(menuAdmin is not null && menuAdmin.IsSystem && menuAdmin.AllowedRoles.Contains(UserContext.SystemManager), "Kelola Menu harus di-seed");

        var scheduledJobs = items.FirstOrDefault(x => x.Href == "admin/jobs");
        Assert.True(scheduledJobs is not null && scheduledJobs.IsSystem && scheduledJobs.AllowedRoles.Contains(UserContext.SystemManager), "Scheduled Jobs harus di-seed");

        var errorLogs = items.FirstOrDefault(x => x.Href == "admin/error-logs");
        Assert.True(errorLogs is not null && errorLogs.IsSystem && errorLogs.AllowedRoles.Contains(UserContext.SystemManager), "Error Log harus di-seed");
    }

    [Test]
    public async Task NavigationMenu_RoleVisibility_FiltersCorrectly()
    {
        var tenantId = "acme";
        var items = await NavMenus.ListAsync(tenantId, onlyActive: false);

        var employeeUser = U("dewi");
        var adminUser = U("admin");

        var empVisible = items.Where(m => m.IsVisibleFor(employeeUser)).ToList();
        var adminVisible = items.Where(m => m.IsVisibleFor(adminUser)).ToList();

        // Karyawan biasa bisa melihat Beranda, Dashboard, Kotak Masuk
        Assert.True(empVisible.Any(x => x.Href == ""), "Karyawan dapat melihat Beranda");
        Assert.True(empVisible.Any(x => x.Href == "dashboard"), "Karyawan dapat melihat Dashboard");
        Assert.True(empVisible.Any(x => x.Href == "inbox"), "Karyawan dapat melihat Kotak masuk");

        // Karyawan biasa TIDAK boleh melihat menu sistem admin
        Assert.True(!empVisible.Any(x => x.Href == "admin/metadata"), "Karyawan dilarang melihat Metadata DocType");
        Assert.True(!empVisible.Any(x => x.Href == "admin/workflows"), "Karyawan dilarang melihat Workflow admin");
        Assert.True(!empVisible.Any(x => x.Href == "query-apis"), "Karyawan dilarang melihat API Query");
        Assert.True(!empVisible.Any(x => x.Href == "admin/api-keys"), "Karyawan dilarang melihat Kunci API");
        Assert.True(!empVisible.Any(x => x.Href == "admin/users"), "Karyawan dilarang melihat Pengguna");
        Assert.True(!empVisible.Any(x => x.Href == "admin/menus"), "Karyawan dilarang melihat Kelola Menu");
        Assert.True(!empVisible.Any(x => x.Href == "admin/jobs"), "Karyawan dilarang melihat Scheduled Jobs");
        Assert.True(!empVisible.Any(x => x.Href == "admin/error-logs"), "Karyawan dilarang melihat Error Log");

        // Admin dapat melihat semua menu
        Assert.True(adminVisible.Any(x => x.Href == "admin/metadata"), "Admin dapat melihat Metadata DocType");
        Assert.True(adminVisible.Any(x => x.Href == "admin/workflows"), "Admin dapat melihat Workflow");
        Assert.True(adminVisible.Any(x => x.Href == "admin/menus"), "Admin dapat melihat Kelola Menu");
        Assert.True(adminVisible.Any(x => x.Href == "admin/jobs"), "Admin dapat melihat Scheduled Jobs");
        Assert.True(adminVisible.Any(x => x.Href == "admin/error-logs"), "Admin dapat melihat Error Log");
    }

    [Test]
    public async Task NavigationMenu_CRUD_AddEditDelete()
    {
        var tenantId = "acme";
        var custom = new NavigationMenuItem
        {
            TenantId = tenantId,
            Title = "Portal Karyawan",
            Href = "portal",
            Icon = "globe",
            GroupName = "Eksternal",
            SortOrder = 50,
            AllowedRoles = [],
            IsActive = true,
            IsSystem = false
        };

        // Create
        await NavMenus.SaveAsync(custom);
        var loaded = await NavMenus.GetAsync(custom.Id, tenantId);
        Assert.True(loaded is not null && loaded.Title == "Portal Karyawan", "Menu custom berhasil disimpan");

        // Update
        loaded!.Title = "Portal Karyawan Updated";
        loaded.SortOrder = 55;
        await NavMenus.SaveAsync(loaded);
        var updated = await NavMenus.GetAsync(custom.Id, tenantId);
        Assert.True(updated is not null && updated.Title == "Portal Karyawan Updated" && updated.SortOrder == 55, "Menu custom berhasil diperbarui");

        // Delete custom
        await NavMenus.DeleteAsync(custom.Id, tenantId);
        var deleted = await NavMenus.GetAsync(custom.Id, tenantId);
        Assert.True(deleted is null, "Menu custom berhasil dihapus");

        // System menu cannot be deleted
        var all = await NavMenus.ListAsync(tenantId, onlyActive: false);
        var sysMenu = all.First(m => m.IsSystem);
        await Assert.Throws<InvalidOperationException>(() => NavMenus.DeleteAsync(sysMenu.Id, tenantId), "Menu sistem tidak boleh dihapus");
    }

    [Test]
    public async Task NavigationModuleAndGroup_SeedingRoleAccessAndCrud()
    {
        var tenantId = "acme";
        var modules = await NavMenus.ListModulesAsync(tenantId, onlyActive: false);
        var groups = await NavMenus.ListGroupsAsync(tenantId, onlyActive: false);

        Assert.True(modules.Count >= 4, "Default modules should be seeded");
        Assert.True(groups.Count >= 6, "Default menu groups should be seeded");

        var employeeUser = U("dewi");
        var adminUser = U("admin");

        var empModules = modules.Where(m => m.IsVisibleFor(employeeUser)).ToList();
        var adminModules = modules.Where(m => m.IsVisibleFor(adminUser)).ToList();

        Assert.True(!empModules.Any(m => m.Name == "Administration"), "Regular employee should not see Administration module");
        Assert.True(adminModules.Any(m => m.Name == "Administration"), "Admin should see Administration module");

        // Create custom module and group
        var customMod = new NavigationModule
        {
            TenantId = tenantId,
            Name = "Finance",
            Description = "Financial management",
            Icon = "wallet",
            SortOrder = 25,
            AllowedRoles = ["Finance"],
            IsActive = true,
            IsSystem = false
        };
        await NavMenus.SaveModuleAsync(customMod);

        var customGrp = new NavigationMenuGroup
        {
            TenantId = tenantId,
            ModuleId = customMod.Id,
            Name = "Buku Besar",
            Icon = "book",
            SortOrder = 10,
            IsActive = true,
            IsSystem = false
        };
        await NavMenus.SaveGroupAsync(customGrp);

        // Cannot delete module while it still has a group
        await Assert.Throws<InvalidOperationException>(() => NavMenus.DeleteModuleAsync(customMod.Id, tenantId), "Cannot delete module with child groups");

        await NavMenus.DeleteGroupAsync(customGrp.Id, tenantId);
        await NavMenus.DeleteModuleAsync(customMod.Id, tenantId);
        Assert.True(await NavMenus.GetModuleAsync(customMod.Id, tenantId) is null, "Custom module deleted after removing child groups");
    }
}
