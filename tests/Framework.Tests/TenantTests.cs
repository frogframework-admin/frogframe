using System.Security.Claims;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Tenancy;
using Framework.Web.Identity;
using Framework.Web.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

/// <summary>
/// Tenant management: registry and cache, platform administrators, creating a tenant with its first administrator,
/// suspension (sign-in, requests), user limit, per-tenant two-factor roles, and the PostgreSQL store and scheduler list.
/// </summary>
sealed partial class Tests
{
    private static ServiceProvider BuildTenantProvider(string platformAdmins = "root")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:Administrators"] = platformAdmins }).Build());
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddFrogFrameworkIdentity();
        services.AddFrogFrameworkTenancy();
        return services.BuildServiceProvider();
    }

    private static UserContext TenantUser(string id, string tenant, params string[] roles) =>
        new(id, id, tenant, roles.ToHashSet(), new Dictionary<string, string>());

    [Test]
    public async Task Tenant_directory_treats_unknown_tenants_as_active_and_sees_changes_at_once()
    {
        var store = new InMemoryTenantStore();
        using var directory = new TenantDirectory(store, TimeProvider.System);
        Assert.True(await directory.IsActiveAsync("legacy"), "a tenant without a registry row (data from before the registry) stays usable");

        await directory.SaveAsync(new Tenant { Id = "pt-a", Name = "PT A", Settings = new TenantSettings { MaxUsers = 5 } });
        Assert.True(await directory.IsActiveAsync("pt-a"), "registered and active");
        Assert.Equal(5, (await directory.SettingsAsync("pt-a")).MaxUsers, "settings");

        var t = (await store.GetAsync("pt-a"))!;
        t.Status = TenantStatus.Suspended;
        await directory.SaveAsync(t);
        Assert.True(!await directory.IsActiveAsync("pt-a"), "a change saved through the directory is visible immediately on this node");

        Assert.True(TenantIds.IsValid("maju-jaya") && TenantIds.IsValid("a1"), "valid codes");
        Assert.True(!TenantIds.IsValid("Maju") && !TenantIds.IsValid("-x") && !TenantIds.IsValid("a") && !TenantIds.IsValid("a b") && !TenantIds.IsValid(new string('a', 33)), "invalid codes");
    }

    [Test]
    public async Task Tenant_admin_creates_tenant_with_first_administrator_and_only_platform_admins_may()
    {
        await using var sp = BuildTenantProvider("root");
        var admin = sp.GetRequiredService<TenantAdminService>();
        var root = TenantUser("root", "group", UserContext.SystemManager);
        var localManager = TenantUser("boss", "group", UserContext.SystemManager);

        await Assert.Throws<PermissionDeniedException>(() => admin.ListAsync(localManager), "a tenant's System Manager is not a platform administrator");
        await Assert.Throws<PermissionDeniedException>(() => admin.CreateAsync(localManager,
            new NewTenantRequest("pt-b", "PT B", "admin.b", "Admin B", null, "Correct-Horse-42", new())), "cannot create tenants");

        await Assert.Throws<TenantAdminException>(() => admin.CreateAsync(root,
            new NewTenantRequest("PT B!", "PT B", "admin.b", "Admin B", null, "Correct-Horse-42", new())), "invalid code");
        await Assert.Throws<TenantAdminException>(() => admin.CreateAsync(root,
            new NewTenantRequest("pt-b", "PT B", "admin.b", "Admin B", null, "short", new())), "password policy checked before anything is created");
        Assert.True(await sp.GetRequiredService<ITenantStore>().GetAsync("pt-b") is null, "nothing left behind after a rejected password");

        var tenant = await admin.CreateAsync(root, new NewTenantRequest("pt-b", "PT Bintang", "admin.b", "Admin B", "admin@b.test",
            TenantAdminService.TemporaryPassword(), new TenantSettings { DefaultLanguage = "ID", MaxUsers = 3, TwoFactorRequiredRoles = [" System Manager ", ""] }));
        Assert.Equal("pt-b", tenant.Id, "id");
        Assert.Equal("id", tenant.Settings.DefaultLanguage, "language normalized");
        Assert.Equal("System Manager", string.Join("|", tenant.Settings.TwoFactorRequiredRoles), "roles trimmed, blanks dropped");

        var account = await sp.GetRequiredService<IUserAccountRepository>().FindByIdAsync("admin.b");
        Assert.True(account is { TenantId: "pt-b", IsActive: true } && account.Roles.Contains(UserContext.SystemManager), "first administrator in the new tenant");

        await Assert.Throws<TenantAdminException>(() => admin.CreateAsync(root,
            new NewTenantRequest("pt-b", "Again", "x.y", "X", null, "Correct-Horse-42", new())), "code in use");
        await Assert.Throws<TenantAdminException>(() => admin.CreateAsync(root,
            new NewTenantRequest("pt-c", "PT C", "admin.b", "X", null, "Correct-Horse-42", new())), "user names are unique across tenants");

        var list = await admin.ListAsync(root);
        Assert.True(list.Any(r => r.Tenant.Id == "pt-b" && r.Registered && r.Usage is { ActiveUsers: 1 }), "listed with usage");

        await Assert.Throws<TenantAdminException>(() => admin.SetStatusAsync(root, "group", false, "test"), "cannot suspend own tenant");
        await Assert.Throws<TenantAdminException>(() => admin.SetStatusAsync(root, "pt-b", false, " "), "reason required");
        Assert.Equal(TenantStatus.Suspended, (await admin.SetStatusAsync(root, "pt-b", false, "contract ended")).Status, "suspended");
        Assert.Equal(TenantStatus.Active, (await admin.SetStatusAsync(root, "pt-b", true, null)).Status, "reactivated");
    }

    [Test]
    public async Task Tenant_suspension_blocks_sign_in_and_requests_and_user_limit_applies()
    {
        await using var sp = BuildTenantProvider();
        var directory = sp.GetRequiredService<TenantDirectory>();
        await directory.SaveAsync(new Tenant { Id = "pt-s", Name = "PT S", Settings = new TenantSettings { MaxUsers = 2 } });

        var (scope, http) = IdentityRequest(sp);
        await using (scope)
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserAccount>>();
            Assert.True((await users.CreateAsync(new UserAccount { UserName = "s1", FullName = "S1", TenantId = "pt-s" }, "Correct-Horse-42")).Succeeded, "user 1");
            Assert.True((await users.CreateAsync(new UserAccount { UserName = "s2", FullName = "S2", TenantId = "pt-s" }, "Correct-Horse-42")).Succeeded, "user 2");
            var third = await users.CreateAsync(new UserAccount { UserName = "s3", FullName = "S3", TenantId = "pt-s" }, "Correct-Horse-42");
            Assert.True(!third.Succeeded && third.Errors.Any(e => e.Code == "TenantUserLimit"), "limit of 2 active users");
            Assert.True((await users.CreateAsync(new UserAccount { UserName = "s3", FullName = "S3", TenantId = "pt-s", IsActive = false }, "Correct-Horse-42")).Succeeded,
                "an inactive account does not count");
            var s3 = (await users.FindByNameAsync("s3"))!;
            s3.IsActive = true;
            Assert.True(!(await users.UpdateAsync(s3)).Succeeded, "re-activating counts against the limit");

            Assert.True((await signIn.PasswordSignInAsync("s1", "Correct-Horse-42", false, false)).Succeeded, "active tenant: sign-in works");
            var t = (await sp.GetRequiredService<ITenantStore>().GetAsync("pt-s"))!;
            t.Status = TenantStatus.Suspended;
            await directory.SaveAsync(t);
            var blocked = await signIn.PasswordSignInAsync("s1", "Correct-Horse-42", false, false);
            Assert.True(!blocked.Succeeded && blocked.IsNotAllowed, "suspended tenant: sign-in refused");
        }

        // A request of an already signed-in user of the suspended tenant.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "s1"), new Claim(FrogFrameClaims.Tenant, "pt-s")], "test"));
        var reached = false;
        var middleware = new TenantStatusMiddleware(_ => { reached = true; return Task.CompletedTask; });
        var api = new DefaultHttpContext { User = principal, RequestServices = sp };
        api.Request.Path = "/api/resource/Customer";
        api.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(api, directory);
        Assert.True(!reached && api.Response.StatusCode == 403, "API call refused with 403");

        var account = new DefaultHttpContext { User = principal, RequestServices = sp };
        account.Request.Path = "/account/login";
        await middleware.InvokeAsync(account, directory);
        Assert.True(reached, "account pages stay reachable");
    }

    [Test]
    public async Task Tenant_two_factor_roles_add_to_the_installation_list()
    {
        await using var sp = BuildTenantProvider();
        var directory = sp.GetRequiredService<TenantDirectory>();
        await directory.SaveAsync(new Tenant { Id = "pt-f", Name = "PT F", Settings = new TenantSettings { TwoFactorRequiredRoles = ["Finance-Admin"] } });

        ClaimsPrincipal User(string tenant) => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "fin"), new Claim(FrogFrameClaims.Tenant, tenant), new Claim(ClaimTypes.Role, "Finance-Admin")], "test", ClaimTypes.Name, ClaimTypes.Role));
        async Task<(bool Reached, string? Location)> Run(string tenant)
        {
            var reached = false;
            var mw = new TwoFactorRequirementMiddleware(_ => { reached = true; return Task.CompletedTask; },
                sp.GetRequiredService<IOptionsMonitor<TwoFactorOptions>>(), directory);
            var http = new DefaultHttpContext { User = User(tenant), RequestServices = sp };
            http.Request.Path = "/app/JournalEntry";
            await mw.InvokeAsync(http);
            return (reached, http.Response.Headers.Location.ToString());
        }

        var inTenant = await Run("pt-f");
        Assert.True(!inTenant.Reached && inTenant.Location.StartsWith(TwoFactorRequirementMiddleware.EnrolPath, StringComparison.Ordinal), "role required by the tenant: sent to enrolment");
        Assert.True((await Run("other")).Reached, "same role in another tenant: not required");
    }

    [Test]
    public async Task Tenant_store_postgres_round_trip_usage_and_scheduler_skip_suspended()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        var dbName = "ff_tenant_" + Guid.NewGuid().ToString("N")[..8];
        await using (var a = new NpgsqlConnection(pg)) { await a.OpenAsync(); await new NpgsqlCommand($"CREATE DATABASE {dbName}", a).ExecuteNonQueryAsync(); }
        try
        {
            var conn = new NpgsqlConnectionStringBuilder(pg) { Database = dbName }.ConnectionString;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton(TimeProvider.System);
            services.AddFrogFrameworkPostgreSql(conn);
            await using var sp = services.BuildServiceProvider();
            await sp.MigrateFrogFrameworkDatabaseAsync();
            var db = sp.GetRequiredService<NpgsqlDataSource>();
            var store = sp.GetRequiredService<ITenantStore>();
            Assert.True(store is PostgresTenantStore, "PostgreSQL store registered");

            await store.SaveAsync(new Tenant
            {
                Id = "pt-p", Name = "PT P", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                Settings = new TenantSettings { MaxUsers = 7, DefaultLanguage = "en", TwoFactorRequiredRoles = ["System Manager"], Notes = "n" },
            });
            var back = (await store.GetAsync("pt-p"))!;
            Assert.True(back.Settings.MaxUsers == 7 && back.Settings.DefaultLanguage == "en" && back.Settings.TwoFactorRequiredRoles.Single() == "System Manager", "settings round trip");

            await using (var cmd = db.CreateCommand("""
                INSERT INTO frogframe_users (id, user_name, normalized_user_name, full_name, tenant_id, roles, is_active, security_stamp, concurrency_stamp, created_at)
                VALUES ('u1', 'u1', 'U1', 'U1', 'pt-q', '{}', true, 's', 'c', now()), ('u2', 'u2', 'U2', 'U2', 'pt-p', '{}', true, 's', 'c', now())
                """)) await cmd.ExecuteNonQueryAsync();
            var usage = await store.UsageAsync();
            Assert.True(usage.Any(u => u.TenantId == "pt-q" && u.ActiveUsers == 1), "tenant with users but no registry row is reported");

            var provider = sp.GetRequiredService<Framework.Jobs.ITenantProvider>();
            Assert.True((await provider.GetActiveTenantsAsync()).Contains("pt-p"), "active tenant runs jobs");
            back.Status = TenantStatus.Suspended;
            await store.SaveAsync(back);
            var tenants = await provider.GetActiveTenantsAsync();
            Assert.True(!tenants.Contains("pt-p") && tenants.Contains("pt-q"), "suspended tenant skipped, unregistered tenant with users kept");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var a = new NpgsqlConnection(pg); await a.OpenAsync(); await new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", a).ExecuteNonQueryAsync();
        }
    }
}
