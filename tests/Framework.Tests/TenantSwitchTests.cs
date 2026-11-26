using System.Security.Claims;
using Framework.Metadata;
using Framework.Security;
using Framework.Tenancy;
using Framework.Web.Identity;
using Framework.Web.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

/// <summary>
/// Platform administrators switching tenant, who may change the shared metadata, and optimistic concurrency of the
/// customization layer.
/// </summary>
sealed partial class Tests
{
    private static ClaimsPrincipal CookiePrincipal(string id, string tenant, params string[] roles)
    {
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, id));
        identity.AddClaim(new Claim(ClaimTypes.Name, id));
        identity.AddClaim(new Claim(FrogFrameClaims.Tenant, tenant));
        identity.AddClaim(new Claim(FrogFrameClaims.FullName, id));
        identity.AddClaim(new Claim(FrogFrameClaims.Employee, "EMP-0001"));
        identity.AddClaim(new Claim(FrogFrameClaims.Department, "Finance"));
        foreach (var r in roles) identity.AddClaim(new Claim(ClaimTypes.Role, r));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Runs the transformation for a request that carries the cookies the previous response set.</summary>
    private static async Task<ClaimsPrincipal> TransformWithCookiesAsync(IServiceProvider sp, HttpResponse previous, ClaimsPrincipal principal)
    {
        var next = new DefaultHttpContext { RequestServices = sp };
        var cookies = SetCookieHeaderValue.ParseList(previous.Headers.SetCookie.Select(v => v ?? "").ToList())
            .Where(c => c.Expires is null || c.Expires > DateTimeOffset.UtcNow)
            .Select(c => $"{c.Name}={c.Value}");
        next.Request.Headers.Cookie = string.Join("; ", cookies);
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = next;
        var transformation = new ActingTenantClaimsTransformation(sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<TenantSwitch>(),
            sp.GetRequiredService<PlatformAdmins>(), sp.GetRequiredService<TenantDirectory>());
        return await transformation.TransformAsync(principal);
    }

    [Test]
    public async Task Platform_admin_switches_tenant_as_system_manager_and_back()
    {
        await using var sp = BuildTenantProvider("root,ops");
        var directory = sp.GetRequiredService<TenantDirectory>();
        await directory.SaveAsync(new Tenant { Id = "holding", Name = "Holding" });
        await directory.SaveAsync(new Tenant { Id = "pt-b", Name = "PT B" });
        var switcher = sp.GetRequiredService<TenantSwitch>();

        var principal = CookiePrincipal("root", "holding", "Employee", "HR-Admin");
        var root = ClaimsUserMapper.ToUserContext(principal)!;

        var http = new DefaultHttpContext { RequestServices = sp };
        await switcher.SwitchAsync(http, root, "pt-b");
        var acting = await TransformWithCookiesAsync(sp, http.Response, principal);
        var user = ClaimsUserMapper.ToUserContext(acting)!;
        Assert.Equal("pt-b", user.TenantId, "acts in the chosen tenant");
        Assert.True(user.Roles.Contains(UserContext.SystemManager) && !user.Roles.Contains("HR-Admin"), "roles become System Manager only");
        Assert.True(user.Attr("employee") is null && user.Attr("department") is null, "home-tenant employee and department are not carried over");
        Assert.Equal("holding", TenantSwitch.HomeTenant(user), "home tenant kept");
        Assert.True(TenantSwitch.IsActing(user), "acting flag");

        // Same cookie presented by another platform administrator (copied cookie): ignored.
        var other = CookiePrincipal("ops", "holding", "Employee");
        var stolen = await TransformWithCookiesAsync(sp, http.Response, other);
        Assert.Equal("holding", ClaimsUserMapper.ToUserContext(stolen)!.TenantId, "the cookie is bound to the user id");

        // Bearer token / API key principal: never transformed.
        var bearerIdentity = new ClaimsIdentity(principal.Claims, IdentityConstants.BearerScheme);
        var bearer = await TransformWithCookiesAsync(sp, http.Response, new ClaimsPrincipal(bearerIdentity));
        Assert.Equal("holding", ClaimsUserMapper.ToUserContext(bearer)!.TenantId, "API tokens act in their own tenant");

        // Suspended target: the switch is ignored at once.
        var b = (await directory.GetAsync("pt-b"))!;
        b.Status = TenantStatus.Suspended;
        await directory.SaveAsync(b);
        var afterSuspend = await TransformWithCookiesAsync(sp, http.Response, principal);
        Assert.Equal("holding", ClaimsUserMapper.ToUserContext(afterSuspend)!.TenantId, "suspended tenant is not entered");
        await Assert.Throws<PermissionDeniedException>(async () => await switcher.SwitchAsync(new DefaultHttpContext { RequestServices = sp }, root, "pt-b"), "cannot switch to a suspended tenant");

        // Back home deletes the cookie.
        var back = new DefaultHttpContext { RequestServices = sp };
        await switcher.SwitchAsync(back, user, null);
        Assert.True(back.Response.Headers.SetCookie.ToString().Contains(TenantSwitch.CookieName), "cookie is cleared");
        Assert.True(!TenantSwitch.IsActing(root), "own tenant is not acting");
    }

    [Test]
    public async Task Only_platform_admins_can_switch_tenant()
    {
        await using var sp = BuildTenantProvider("root");
        await sp.GetRequiredService<TenantDirectory>().SaveAsync(new Tenant { Id = "pt-b", Name = "PT B" });
        var manager = TenantUser("tenant-admin", "pt-a", UserContext.SystemManager);
        await Assert.Throws<PermissionDeniedException>(
            async () => await sp.GetRequiredService<TenantSwitch>().SwitchAsync(new DefaultHttpContext { RequestServices = sp }, manager, "pt-b"),
            "a tenant System Manager is not a platform administrator");
    }

    [Test]
    public async Task Metadata_edit_policy_limits_shared_metadata_to_platform_admins_with_several_tenants()
    {
        await using var sp = BuildTenantProvider("root");
        var policy = sp.GetRequiredService<MetadataEditPolicy>();
        var directory = sp.GetRequiredService<TenantDirectory>();
        var tenantManager = TenantUser("sm-a", "pt-a", UserContext.SystemManager);
        var root = TenantUser("root", "pt-a", UserContext.SystemManager);
        var employee = TenantUser("emp", "pt-a", "Employee");

        await directory.SaveAsync(new Tenant { Id = "pt-a", Name = "PT A" });
        Assert.True(await policy.CanEditAsync(tenantManager), "one tenant: every System Manager may edit (as before)");
        Assert.True(!await policy.CanEditAsync(employee), "never without System Manager");

        await directory.SaveAsync(new Tenant { Id = "pt-b", Name = "PT B" });
        Assert.True(!await policy.CanEditAsync(tenantManager), "several tenants: a tenant System Manager cannot change other tenants' forms");
        Assert.True(await policy.CanEditAsync(root), "platform administrator may");
        await Assert.Throws<PermissionDeniedException>(async () => await policy.DemandAsync(tenantManager), "Demand throws");
    }

    [Test]
    public Task Customization_save_detects_concurrent_change()
    {
        var store = new FakeMetadataStore();
        var reg = CzRegistry(store, CzFile(1));
        Assert.Equal(0, reg.CustomizationVersion("CzItem"), "not customized yet");

        // Two administrators open the editor at customization v0.
        var first = CzEdited(reg);
        var second = CzEdited(reg);
        second.Fields.Insert(1, new FieldDef { Name = "custom_size", Type = "Data", Label = "Size" });

        reg.Publish(first, "admin-1", acknowledgeRisk: true, expectedCustomizationVersion: 0);
        Assert.Equal(1, reg.CustomizationVersion("CzItem"), "first save is v1");

        var ex = Assert.Throws<MetadataConcurrencyException>(
            () => reg.Publish(second, "admin-2", acknowledgeRisk: true, expectedCustomizationVersion: 0), "second save from a stale editor is refused");
        Assert.Equal(1, ex.CurrentVersion, "reports the current version");
        Assert.True(reg.Current.Get("CzItem").Def.Fields.All(f => f.Name != "custom_size"), "nothing was overwritten");

        Assert.Throws<MetadataConcurrencyException>(
            () => reg.ResetCustomization("CzItem", "admin-2", expectedCustomizationVersion: 0), "a stale reset is refused too");
        reg.ResetCustomization("CzItem", "admin-1", expectedCustomizationVersion: 1);
        Assert.Equal(2, reg.CustomizationVersion("CzItem"), "reset is saved as v2");

        // Callers that pass no expected version (scripts, older API clients) keep the old behaviour.
        reg.Publish(second, "admin-2", acknowledgeRisk: true);
        Assert.Equal(3, reg.CustomizationVersion("CzItem"), "no check without an expected version");
        return Task.CompletedTask;
    }
}
