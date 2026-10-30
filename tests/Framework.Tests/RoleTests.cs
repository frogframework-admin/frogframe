using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    private IRoleStore Roles => sp.GetRequiredService<IRoleStore>();

    [Test]
    public async Task Roles_DefaultSeeding_ContainsAdministratorAndRequiredRoles()
    {
        var tenantId = "acme";
        var list = await Roles.ListAsync(tenantId);

        // Administrator wajib ada dan IsSystem = true
        var admin = list.FirstOrDefault(r => r.Name == "Administrator");
        Assert.True(admin is not null && admin.IsSystem, "Role Administrator harus ada dan bertipe sistem");

        // User wajib ada
        var user = list.FirstOrDefault(r => r.Name == "User");
        Assert.True(user is not null, "Role User harus ada dalam seed data");

        // HR wajib ada
        var hr = list.FirstOrDefault(r => r.Name == "HR");
        Assert.True(hr is not null, "Role HR harus ada dalam seed data");

        // Purchasing wajib ada
        var purchasing = list.FirstOrDefault(r => r.Name == "Purchasing");
        Assert.True(purchasing is not null, "Role Purchasing harus ada dalam seed data");
    }

    [Test]
    public async Task Roles_Administrator_CannotBeDeleted()
    {
        var tenantId = "acme";
        var list = await Roles.ListAsync(tenantId);
        var admin = list.First(r => r.Name == "Administrator");

        await Assert.Throws<InvalidOperationException>(() => Roles.DeleteAsync(admin.Id, tenantId), "Role Administrator tidak boleh dihapus");
    }

    [Test]
    public async Task Roles_CRUD_AddEditDelete_CustomRole()
    {
        var tenantId = "acme";
        var custom = new RoleDef
        {
            TenantId = tenantId,
            Name = "Auditor Internal",
            Description = "Pemeriksa audit sistem",
            IsSystem = false
        };

        // Create
        await Roles.SaveAsync(custom);
        var loaded = await Roles.GetAsync(custom.Id, tenantId);
        Assert.True(loaded is not null && loaded.Name == "Auditor Internal", "Role custom berhasil disimpan");

        // Update
        loaded!.Description = "Pemeriksa audit kepatuhan dan finansial";
        await Roles.SaveAsync(loaded);
        var updated = await Roles.GetAsync(custom.Id, tenantId);
        Assert.True(updated is not null && updated.Description == "Pemeriksa audit kepatuhan dan finansial", "Role custom berhasil diupdate");

        // Delete
        await Roles.DeleteAsync(custom.Id, tenantId);
        var deleted = await Roles.GetAsync(custom.Id, tenantId);
        Assert.True(deleted is null, "Role custom berhasil dihapus");
    }

    [Test]
    public Task Roles_UserContext_IsAdmin_WithAdministratorRole()
    {
        var adminUser = new UserContext("adm_1", "Admin Baru", "acme", new HashSet<string> { "Administrator" }, new Dictionary<string, string>());
        Assert.True(adminUser.IsAdmin, "Pengguna dengan role Administrator harus dikenali sebagai admin");

        var regularUser = new UserContext("usr_1", "User Biasa", "acme", new HashSet<string> { "User", "Employee" }, new Dictionary<string, string>());
        Assert.True(!regularUser.IsAdmin, "Pengguna biasa tidak boleh memiliki hak admin");
        return Task.CompletedTask;
    }
}

