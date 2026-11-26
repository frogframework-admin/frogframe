using Framework.Security;
using Framework.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Framework.Web.Tenancy;

/// <summary>
/// Who may change metadata (DocTypes, customizations, workflows, DocType permissions).
/// Metadata is shared by every tenant of the installation (docs/guides/tenants.md), so with more than one tenant a
/// tenant's System Manager would change forms and permissions of the other companies too. Rules:
/// <list type="bullet">
/// <item>always System Manager in the current tenant (also while a platform administrator acts in another tenant);</item>
/// <item>one registered tenant (or none): every System Manager may edit, as before;</item>
/// <item>more than one tenant: only platform administrators (<c>Platform:Administrators</c>), unless
///   <c>Platform:TenantAdminsCanEditMetadata</c> is true (all tenants trust each other's administrators).</item>
/// </list>
/// Reading metadata stays open to every System Manager.
/// </summary>
public sealed class MetadataEditPolicy(PlatformAdmins admins, ITenantStore tenants, IConfiguration? config = null)
{
    public const string AllowTenantAdminsKey = "Platform:TenantAdminsCanEditMetadata";

    public const string DeniedMessage =
        "Metadata is shared by every tenant of this installation. Only platform administrators (Platform:Administrators) can change it.";

    public async Task<bool> CanEditAsync(UserContext? user, CancellationToken ct = default)
    {
        if (user is null || !user.Roles.Contains(UserContext.SystemManager)) return false;
        if (admins.IsPlatformAdmin(user)) return true;
        if (string.Equals(config?[AllowTenantAdminsKey], "true", StringComparison.OrdinalIgnoreCase)) return true;
        return (await tenants.ListAsync(ct)).Count <= 1;
    }

    public async Task DemandAsync(UserContext user, CancellationToken ct = default)
    {
        if (!await CanEditAsync(user, ct)) throw new PermissionDeniedException(DeniedMessage);
    }
}
