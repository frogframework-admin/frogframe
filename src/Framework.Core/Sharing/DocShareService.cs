using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Framework.Sharing;

/// <summary>What the acting user may do in the share drawer of one document.</summary>
public sealed record ShareRights(bool CanManage, bool CanGrantWrite, bool CanGrantShare);

/// <summary>A share as shown in the drawer, with whether the user's roles let the share take effect.</summary>
public sealed record ShareView(DocShare Share, string UserName, bool HasRoleRead, bool HasRoleWrite);

/// <summary>A user who can be picked in the share drawer.</summary>
public sealed record ShareCandidate(string UserId, string FullName, bool HasRoleRead, bool HasRoleWrite, bool AlreadyHasAccess);

/// <summary>
/// Row sharing from the document form. Who may share a document:
/// <list type="bullet">
/// <item>an administrator;</item>
/// <item>the owner of the document;</item>
/// <item>a user whose role scope lets them write the document;</item>
/// <item>a user the document was shared with together with the <c>Share</c> right.</item>
/// </list>
/// Write can only be granted by someone who can write the document, and Share by someone allowed to share it.
/// Every change is written to the document's history and the target user is notified.
/// </summary>
public sealed class DocShareService(
    IDocShareStore shares,
    DocumentService documents,
    IDocumentStore store,
    IMetadataProvider metadata,
    IPermissionService permissions,
    IUserDirectory users,
    INotificationStore notifications,
    TimeProvider clock)
{
    public const string AuditShared = "Shared";
    public const string AuditUnshared = "Unshared";

    private CompiledDocType TopLevel(string docType)
    {
        if (!metadata.Current.TryGet(docType, out var m) || m.Def.IsChild || m.Def.IsSingle)
            throw new DocumentNotFoundException("DocType", docType);
        return m;
    }

    /// <summary>Rights of <paramref name="actor"/> on the share drawer of a document they can read.</summary>
    public async Task<ShareRights> RightsAsync(UserContext actor, string docType, string name, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        var doc = await documents.GetAsync(actor, docType, name, ct); // read permission, row scope or share
        var own = await shares.FindAsync(actor.TenantId, docType, name, actor.Id, ct);
        return Rights(actor, meta, doc, own);
    }

    private ShareRights Rights(UserContext actor, CompiledDocType meta, Document doc, DocShare? own)
    {
        var roleWrite = permissions.Can(actor, meta, DocActions.Write) && permissions.InScope(actor, meta, DocActions.Write, doc.Owner, doc.Data);
        var shareWrite = own?.CanWrite == true && permissions.Can(actor, meta, DocActions.Write);
        var canManage = actor.IsAdmin
                        || string.Equals(doc.Owner, actor.Id, StringComparison.OrdinalIgnoreCase)
                        || roleWrite
                        || own?.CanShare == true;
        return new ShareRights(canManage, canManage && (actor.IsAdmin || roleWrite || shareWrite), canManage);
    }

    public async Task<IReadOnlyList<ShareView>> ListAsync(UserContext actor, string docType, string name, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        await documents.GetAsync(actor, docType, name, ct);
        var list = await shares.ForDocumentAsync(actor.TenantId, docType, name, ct);
        var result = new List<ShareView>(list.Count);
        foreach (var s in list)
        {
            var u = await users.FindAsync(s.UserId, ct);
            var sameTenant = u is not null && u.TenantId == actor.TenantId;
            result.Add(new ShareView(s, sameTenant ? u!.FullName : s.UserId,
                sameTenant && permissions.Can(u!, meta, DocActions.Read),
                sameTenant && permissions.Can(u!, meta, DocActions.Write)));
        }
        return result;
    }

    /// <summary>Active users of the tenant except the actor, with whether a share would take effect for them.</summary>
    public async Task<IReadOnlyList<ShareCandidate>> CandidatesAsync(UserContext actor, string docType, string name, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        var doc = await documents.GetAsync(actor, docType, name, ct);
        var stored = await store.GetAsync(actor.TenantId, docType, name, ct) ?? throw new DocumentNotFoundException(meta.Title, name);
        return (await users.ListAsync(actor.TenantId, ct))
            .Where(u => !string.Equals(u.Id, actor.Id, StringComparison.OrdinalIgnoreCase))
            .Select(u => new ShareCandidate(u.Id, u.FullName,
                permissions.Can(u, meta, DocActions.Read), permissions.Can(u, meta, DocActions.Write),
                string.Equals(u.Id, doc.Owner, StringComparison.OrdinalIgnoreCase) || permissions.InScope(u, meta, DocActions.Read, stored.Owner, stored.Data)))
            .OrderBy(c => c.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Shares the document with a user, or changes the rights of an existing share.</summary>
    public async Task<DocShare> ShareAsync(UserContext actor, string docType, string name, string userId, bool canWrite, bool canShare, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        await documents.GetAsync(actor, docType, name, ct);
        var doc = await store.GetAsync(actor.TenantId, docType, name, ct) ?? throw new DocumentNotFoundException(meta.Title, name);
        var rights = Rights(actor, meta, doc, await shares.FindAsync(actor.TenantId, docType, name, actor.Id, ct));
        if (!rights.CanManage) throw new PermissionDeniedException($"You are not allowed to share {meta.Title} {name}.");
        if (canWrite && !rights.CanGrantWrite) throw new PermissionDeniedException($"You can only share {name} as read-only: you cannot edit it yourself.");
        if (canShare && !rights.CanGrantShare) throw new PermissionDeniedException($"You are not allowed to let others share {name}.");

        var target = (userId ?? "").Trim();
        if (target.Length == 0) throw new InvalidPayloadException("Choose a user to share with.", ["user"]);
        if (string.Equals(target, actor.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidPayloadException("You cannot share a document with yourself.", ["user"]);
        var u = await users.FindAsync(target, ct);
        if (u is null || u.TenantId != actor.TenantId) throw new InvalidPayloadException($"User '{target}' was not found.", ["user"]);
        if (string.Equals(u.Id, doc.Owner, StringComparison.OrdinalIgnoreCase)) throw new InvalidPayloadException($"{u.FullName} owns this document already.", ["user"]);

        var existing = await shares.ForDocumentAsync(actor.TenantId, docType, name, ct);
        var previous = existing.FirstOrDefault(s => string.Equals(s.UserId, u.Id, StringComparison.OrdinalIgnoreCase));
        if (previous is null && existing.Count >= DocShareLimits.MaxSharesPerDocument)
            throw new InvalidPayloadException($"{name} is already shared with {DocShareLimits.MaxSharesPerDocument} users.", ["user"]);

        var now = clock.GetUtcNow();
        var share = new DocShare(actor.TenantId, docType, name, u.Id, canWrite, canShare, actor.Id, previous?.SharedAt ?? now);
        await shares.UpsertAsync(share, ct);
        var rightsText = RightsText(share);
        await AuditAsync(doc, actor, AuditShared, previous is null
            ? $"Shared with {u.FullName} ({rightsText})"
            : $"Share with {u.FullName} changed from {RightsText(previous)} to {rightsText}", ct);
        if (previous is null)
            notifications.Add(new Notification(Guid.NewGuid(), actor.TenantId, u.Id, $"{actor.FullName} shared {meta.Title} {name} with you",
                $"Access: {rightsText}.", docType, name, now));
        return share;
    }

    /// <summary>Removes a share. Allowed for whoever may manage shares of the document, and for the user it was shared with.</summary>
    public async Task<bool> UnshareAsync(UserContext actor, string docType, string name, string userId, CancellationToken ct = default)
    {
        var meta = TopLevel(docType);
        await documents.GetAsync(actor, docType, name, ct);
        var doc = await store.GetAsync(actor.TenantId, docType, name, ct) ?? throw new DocumentNotFoundException(meta.Title, name);
        var self = string.Equals(userId, actor.Id, StringComparison.OrdinalIgnoreCase);
        if (!self && !Rights(actor, meta, doc, await shares.FindAsync(actor.TenantId, docType, name, actor.Id, ct)).CanManage)
            throw new PermissionDeniedException($"You are not allowed to change the sharing of {meta.Title} {name}.");
        var share = await shares.FindAsync(actor.TenantId, docType, name, userId, ct);
        if (share is null) return false;
        await shares.RemoveAsync(actor.TenantId, docType, name, userId, ct);
        var u = await users.FindAsync(share.UserId, ct);
        await AuditAsync(doc, actor, AuditUnshared, self ? "Left the share" : $"Stopped sharing with {u?.FullName ?? share.UserId}", ct);
        return true;
    }

    private static string RightsText(DocShare s) => s.CanWrite ? (s.CanShare ? "read, write, share" : "read, write") : s.CanShare ? "read, share" : "read";

    private async Task AuditAsync(Document doc, UserContext actor, string action, string comment, CancellationToken ct)
    {
        var uow = new UnitOfWork();
        uow.Audits.Add(new AuditEntry
        {
            TenantId = doc.TenantId, DocType = doc.DocType, DocName = doc.Name, Action = action, Actor = actor.FullName,
            At = clock.GetUtcNow(), Comment = comment, SchemaVersion = doc.SchemaVersion,
        });
        await store.CommitAsync(uow, ct);
    }
}

/// <summary>Removes the shares of a deleted document.</summary>
public sealed class DocShareCleanupHandler(IDocShareStore shares) : IEventHandler
{
    public bool CanHandle(string eventType) => eventType == EventTypes.DocumentDeleted;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var docType = message.Payload.GetValueOrDefault("docType");
        var docName = message.Payload.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(docType) || string.IsNullOrWhiteSpace(docName)) return;
        await shares.RemoveDocumentAsync(message.TenantId, docType, docName, ct);
    }
}
