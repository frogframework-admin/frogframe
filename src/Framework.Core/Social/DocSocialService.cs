using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Framework.Sharing;

namespace Framework.Social;

/// <summary>Tags, bookmark and follow state of one document for the acting user (form toolbar).</summary>
public sealed record DocSocialState(IReadOnlyList<string> Tags, bool CanEditTags, bool Bookmarked, bool Following, int Followers);

/// <summary>A bookmarked or followed document the user can still read, for "My bookmarks" / "Following".</summary>
public sealed record SavedDocument(string DocType, string DocTypeTitle, string Name, string? Title, DateTimeOffset At);

/// <summary>
/// Social features of documents:
/// <list type="bullet">
/// <item><b>Tags</b>: shared labels; visible to everyone who can read the document, changed by users who can edit it
/// (role scope or a write share), written to the document's history, filterable in list views (<c>_tag</c>).</item>
/// <item><b>Bookmarks</b>: personal; any user who can read the document.</item>
/// <item><b>Follow</b>: any user who can read the document; followers are notified of changes by
/// <see cref="DocFollowNotifier"/>, as long as they can still read it.</item>
/// </list>
/// </summary>
public sealed class DocSocialService(
    IDocSocialStore social,
    DocumentService documents,
    IDocumentStore store,
    IMetadataProvider metadata,
    IPermissionService permissions,
    TimeProvider clock,
    IDocShareStore? shares = null)
{
    public const string AuditTagged = "Tagged";
    public const string AuditUntagged = "Untagged";

    private CompiledDocType TopLevel(string docType)
    {
        if (!metadata.Current.TryGet(docType, out var m) || m.Def.IsChild || m.Def.IsSingle)
            throw new DocumentNotFoundException("DocType", docType);
        return m;
    }

    /// <summary>Loads the document the user can read (permission, row scope or share); throws otherwise.</summary>
    private async Task<(CompiledDocType Meta, Document Doc)> ReadableAsync(UserContext user, string docType, string name, CancellationToken ct)
    {
        var meta = TopLevel(docType);
        await documents.GetAsync(user, docType, name, ct);
        var doc = await store.GetAsync(user.TenantId, docType, name, ct) ?? throw new DocumentNotFoundException(meta.Title, name);
        return (meta, doc);
    }

    private async Task<bool> CanEditTagsAsync(UserContext user, CompiledDocType meta, Document doc, CancellationToken ct) =>
        user.IsAdmin || (permissions.Can(user, meta, DocActions.Write) && await permissions.InScopeAsync(shares, user, meta, DocActions.Write, doc, ct));

    public async Task<DocSocialState> StateAsync(UserContext user, string docType, string name, CancellationToken ct = default)
    {
        var (meta, doc) = await ReadableAsync(user, docType, name, ct);
        var tags = await social.TagsAsync(user.TenantId, docType, name, ct);
        return new DocSocialState(tags.Select(t => t.Tag).ToList(), await CanEditTagsAsync(user, meta, doc, ct),
            await social.IsBookmarkedAsync(user.TenantId, user.Id, docType, name, ct),
            await social.IsFollowingAsync(user.TenantId, docType, name, user.Id, ct),
            (await social.FollowersAsync(user.TenantId, docType, name, ct)).Count);
    }

    // ---------------------------------------------------------------- tags

    /// <summary>Adds a tag; returns the tags of the document afterwards. Adding a tag it already has changes nothing.</summary>
    public async Task<IReadOnlyList<string>> AddTagAsync(UserContext user, string docType, string name, string tag, CancellationToken ct = default)
    {
        var (meta, doc) = await ReadableAsync(user, docType, name, ct);
        if (!await CanEditTagsAsync(user, meta, doc, ct))
            throw new PermissionDeniedException($"You can only tag {meta.Title} documents you can edit.");
        var clean = DocTags.Normalize(tag, out var error) ?? throw new InvalidPayloadException(error!, ["tag"]);
        var existing = await social.TagsAsync(user.TenantId, docType, name, ct);
        if (existing.Any(t => string.Equals(t.Tag, clean, StringComparison.OrdinalIgnoreCase)))
            return existing.Select(t => t.Tag).ToList();
        if (existing.Count >= DocSocialLimits.MaxTagsPerDocument)
            throw new InvalidPayloadException($"{name} already has {DocSocialLimits.MaxTagsPerDocument} tags.", ["tag"]);

        // Reuse the spelling already used on this DocType ("Urgent", not "urgent" next to it).
        var known = (await social.TagUsageAsync(user.TenantId, docType, clean, 20, ct))
            .FirstOrDefault(u => string.Equals(u.Tag, clean, StringComparison.OrdinalIgnoreCase));
        clean = known?.Tag ?? clean;
        if (await social.AddTagAsync(new DocTag(user.TenantId, docType, name, clean, user.Id, clock.GetUtcNow()), ct))
            await AuditAsync(doc, user, AuditTagged, $"Tag added: {clean}", ct);
        return (await social.TagsAsync(user.TenantId, docType, name, ct)).Select(t => t.Tag).ToList();
    }

    public async Task<IReadOnlyList<string>> RemoveTagAsync(UserContext user, string docType, string name, string tag, CancellationToken ct = default)
    {
        var (meta, doc) = await ReadableAsync(user, docType, name, ct);
        if (!await CanEditTagsAsync(user, meta, doc, ct))
            throw new PermissionDeniedException($"You can only change tags of {meta.Title} documents you can edit.");
        var current = (await social.TagsAsync(user.TenantId, docType, name, ct))
            .FirstOrDefault(t => string.Equals(t.Tag, (tag ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (current is not null && await social.RemoveTagAsync(user.TenantId, docType, name, current.Tag, ct))
            await AuditAsync(doc, user, AuditUntagged, $"Tag removed: {current.Tag}", ct);
        return (await social.TagsAsync(user.TenantId, docType, name, ct)).Select(t => t.Tag).ToList();
    }

    /// <summary>Tags used on a DocType (most used first), for suggestions and the list filter.</summary>
    public Task<IReadOnlyList<TagUsage>> TagSuggestionsAsync(UserContext user, string docType, string? prefix, int max = 20, CancellationToken ct = default)
    {
        permissions.Demand(user, TopLevel(docType), DocActions.Read);
        return social.TagUsageAsync(user.TenantId, docType, string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim(), Math.Clamp(max, 1, 100), ct);
    }

    // ---------------------------------------------------------------- bookmarks & follow

    public async Task<bool> SetBookmarkAsync(UserContext user, string docType, string name, bool on, CancellationToken ct = default)
    {
        await ReadableAsync(user, docType, name, ct);
        if (!on) { await social.RemoveBookmarkAsync(user.TenantId, user.Id, docType, name, ct); return false; }
        if (await social.IsBookmarkedAsync(user.TenantId, user.Id, docType, name, ct)) return true;
        if (await social.CountBookmarksAsync(user.TenantId, user.Id, ct) >= DocSocialLimits.MaxBookmarksPerUser)
            throw new InvalidPayloadException($"You already have {DocSocialLimits.MaxBookmarksPerUser} bookmarks. Remove some first.");
        await social.AddBookmarkAsync(new Bookmark(user.TenantId, user.Id, docType, name, clock.GetUtcNow()), ct);
        return true;
    }

    public async Task<bool> SetFollowAsync(UserContext user, string docType, string name, bool on, CancellationToken ct = default)
    {
        await ReadableAsync(user, docType, name, ct);
        if (!on) { await social.UnfollowAsync(user.TenantId, docType, name, user.Id, ct); return false; }
        if (await social.IsFollowingAsync(user.TenantId, docType, name, user.Id, ct)) return true;
        if ((await social.FollowersAsync(user.TenantId, docType, name, ct)).Count >= DocSocialLimits.MaxFollowersPerDocument)
            throw new InvalidPayloadException($"{name} already has {DocSocialLimits.MaxFollowersPerDocument} followers.");
        await social.FollowAsync(new DocFollow(user.TenantId, docType, name, user.Id, clock.GetUtcNow()), ct);
        return true;
    }

    /// <summary>The user's bookmarks that they can still read, newest first.</summary>
    public async Task<IReadOnlyList<SavedDocument>> BookmarksAsync(UserContext user, CancellationToken ct = default) =>
        await ReadableOnlyAsync(user, (await social.BookmarksAsync(user.TenantId, user.Id, DocSocialLimits.MaxBookmarksPerUser, ct))
            .Select(b => (b.DocType, b.DocName, b.CreatedAt)), ct);

    /// <summary>Documents the user follows and can still read, newest first.</summary>
    public async Task<IReadOnlyList<SavedDocument>> FollowingAsync(UserContext user, CancellationToken ct = default) =>
        await ReadableOnlyAsync(user, (await social.FollowingAsync(user.TenantId, user.Id, 1_000, ct))
            .Select(f => (f.DocType, f.DocName, f.FollowedAt)), ct);

    private async Task<IReadOnlyList<SavedDocument>> ReadableOnlyAsync(UserContext user,
        IEnumerable<(string DocType, string Name, DateTimeOffset At)> items, CancellationToken ct)
    {
        var result = new List<SavedDocument>();
        foreach (var (docType, name, at) in items)
        {
            if (!metadata.Current.TryGet(docType, out var meta)) continue; // DocType removed
            try
            {
                var doc = await documents.GetAsync(user, docType, name, ct);
                var title = meta.Def.ListView.TitleField is { } tf ? Convert.ToString(doc.Get(tf), System.Globalization.CultureInfo.InvariantCulture) : null;
                result.Add(new SavedDocument(docType, meta.Title, name, string.IsNullOrWhiteSpace(title) ? null : title, at));
            }
            catch (Exception ex) when (ex is PermissionDeniedException or DocumentNotFoundException) { } // no longer readable
        }
        return result;
    }

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

/// <summary>
/// Notifies the followers of a document when it is updated, moves in its workflow, gets an action (submit, cancel, …),
/// is amended, commented on or deleted. The user who made the change is not notified, nor are followers who can no
/// longer read the document; users mentioned in a comment already get a mention notification instead. After a delete
/// the document's tags, bookmarks and follows are removed.
/// </summary>
public sealed class DocFollowNotifier(
    IDocSocialStore social,
    IUserDirectory users,
    INotificationStore inbox,
    IDocumentStore store,
    IMetadataProvider metadata,
    IPermissionService permissions,
    TimeProvider clock,
    INotificationLiveFeed? liveFeed = null,
    IDocShareStore? shares = null) : IEventHandler
{
    public bool CanHandle(string eventType) =>
        eventType is EventTypes.DocumentUpdated or EventTypes.WorkflowTransitioned or EventTypes.DocumentAmended or EventTypes.DocumentDeleted
            or EventTypes.CommentAdded
        || (eventType.StartsWith("document.", StringComparison.Ordinal) && eventType is not (EventTypes.DocumentInserted or EventTypes.AssignmentChanged));

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = message.Payload;
        var docType = p.GetValueOrDefault("docType");
        var name = p.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(docType) || string.IsNullOrWhiteSpace(name)) return;
        var deleted = message.Type == EventTypes.DocumentDeleted;
        try
        {
            var followers = await social.FollowersAsync(message.TenantId, docType, name, ct);
            if (followers.Count == 0 || !metadata.Current.TryGet(docType, out var meta)) return;
            var doc = deleted ? null : await store.GetAsync(message.TenantId, docType, name, ct);
            if (!deleted && doc is null) return; // deleted later; the delete event notifies

            var actor = p.GetValueOrDefault("actor");
            var actorName = p.GetValueOrDefault("actorName") ?? actor ?? "Someone";
            var mentioned = message.Type == EventTypes.CommentAdded
                ? (p.GetValueOrDefault("mentions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var (subject, body) = Describe(message.Type, p, actorName, meta.Title, name);

            foreach (var f in followers)
            {
                if (string.Equals(f.UserId, actor, StringComparison.OrdinalIgnoreCase)) continue;
                if (mentioned.Contains(f.UserId) || mentioned.Contains(DocumentComments.EveryoneToken)) continue;
                var target = await users.FindAsync(f.UserId, ct);
                if (target is null || target.TenantId != message.TenantId || !permissions.Can(target, meta, DocActions.Read)) continue;
                if (doc is not null && !await permissions.InScopeAsync(shares, target, meta, DocActions.Read, doc, ct)) continue;

                var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{message.TenantId}|follow|{message.Id}|{target.Id.ToLowerInvariant()}")));
                var n = new Notification(id, message.TenantId, target.Id, subject, body, docType, deleted ? null : name, clock.GetUtcNow());
                inbox.Add(n);
                if (liveFeed is not null) await liveFeed.PublishAsync(n, ct);
            }
        }
        finally
        {
            if (deleted) await social.RemoveDocumentAsync(message.TenantId, docType, name, ct);
        }
    }

    /// <summary>Subject and body of the notification for an event.</summary>
    public static (string Subject, string Body) Describe(string eventType, IReadOnlyDictionary<string, string?> p, string actorName, string title, string name)
    {
        var doc = $"{title} {name}";
        return eventType switch
        {
            EventTypes.DocumentUpdated => ($"{actorName} updated {doc}", $"Version {p.GetValueOrDefault("version")}. Open the document to see the changes in its history."),
            EventTypes.WorkflowTransitioned => ($"{actorName} moved {doc} to {p.GetValueOrDefault("workflowState") ?? "a new state"}", "Workflow state changed."),
            EventTypes.DocumentAmended => ($"{actorName} amended {doc}", "A new amendment was created."),
            EventTypes.DocumentDeleted => ($"{actorName} deleted {doc}", "You no longer follow it."),
            EventTypes.CommentAdded => ($"{actorName} commented on {doc}", Preview(p.GetValueOrDefault("comment"))),
            EventTypes.DocumentSubmit => ($"{actorName} submitted {doc}", "The document was submitted."),
            EventTypes.DocumentCancel => ($"{actorName} cancelled {doc}", "The document was cancelled."),
            _ => ($"{actorName} ran '{eventType["document.".Length..]}' on {doc}", "Action applied."),
        };
    }

    private static string Preview(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length <= CommentMentionNotifier.BodyPreviewLength ? t : string.Concat(t.AsSpan(0, CommentMentionNotifier.BodyPreviewLength), "…");
    }
}
