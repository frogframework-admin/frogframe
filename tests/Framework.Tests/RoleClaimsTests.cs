using Framework.Security;
using Framework.Web.Identity;

/// <summary>Role groups reach the identity: claims and the user directory carry the effective roles.</summary>
sealed partial class Tests
{
    [Test]
    public Task Role_claims_carry_effective_roles_of_groups()
    {
        RoleGroups.Register("ClaimsTestGroup", ["ClaimsTest-A", "ClaimsTest-B"]);
        var legacy = ClaimsUserMapper.FromAccount(new UserAccount { Id = "u1", UserName = "u1", FullName = "U", TenantId = "acme", Roles = ["ClaimsTestGroup"] });
        Assert.True(legacy.Roles.Contains("ClaimsTest-A") && legacy.Roles.Contains("ClaimsTest-B") && legacy.Roles.Contains("ClaimsTestGroup"),
            "a group holder gets the members in their claims");
        var member = ClaimsUserMapper.FromAccount(new UserAccount { Id = "u2", UserName = "u2", FullName = "U", TenantId = "acme", Roles = ["ClaimsTest-A"] });
        Assert.True(member.Roles.Contains("ClaimsTestGroup") && !member.Roles.Contains("ClaimsTest-B"), "a member counts as the group, not as its sibling");
        return Task.CompletedTask;
    }
}
