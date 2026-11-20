using Framework.Documents;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Sharing;

/// <summary>Row scope check that also honours document shares, for services that load a single document.</summary>
public static class RowAccess
{
    /// <summary>
    /// True when the user's role scope covers the document for <paramref name="action"/>, or when the document is
    /// shared with the user (read; write only for a write share) and a role gives the DocType permission for that
    /// action. Shares never grant other actions.
    /// </summary>
    public static async Task<bool> InScopeAsync(this IPermissionService permissions, IDocShareStore? shares,
        UserContext user, CompiledDocType meta, string action, Document doc, CancellationToken ct = default)
    {
        if (permissions.InScope(user, meta, action, doc.Owner, doc.Data)) return true;
        if (shares is null || action is not (DocActions.Read or DocActions.Write) || !permissions.Can(user, meta, action)) return false;
        var share = await shares.FindAsync(user.TenantId, meta.Name, doc.Name, user.Id, ct);
        return share is not null && (action == DocActions.Read || share.CanWrite);
    }
}
