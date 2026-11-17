using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;

namespace Framework.Documents;

/// <summary>Well-known audit actions that are not workflow transitions.</summary>
public static class AuditActions
{
    /// <summary>
    /// A user comment in the document timeline. Comments are stored as append-only audit entries
    /// (<see cref="AuditEntry.Comment"/> holds the text), so they interleave with the change history and cannot be edited.
    /// </summary>
    public const string Comment = "Comment";
}

/// <summary>Limits and <c>@mention</c> parsing for document comments.</summary>
public static partial class DocumentComments
{
    public const int MaxLength = 5000;
    public const int MaxMentions = 20;
    public const string EveryoneToken = "everyone";

    public static bool IsMentionNotification(Notification n) =>
        n.Subject.Contains(" mentioned you in ", StringComparison.Ordinal) ||
        n.Subject.Contains(" mentioned @everyone in ", StringComparison.Ordinal);

    // "@andi", "@dewi.anggraini", "@andi@acme.co.id". The look-behind keeps plain e-mail addresses ("x@y.com") from being read as mentions.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@.])@([A-Za-z0-9][A-Za-z0-9._\-]*(?:@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    /// <summary>Distinct mentioned user ids, in order of appearance (case-insensitive, trailing punctuation removed).</summary>
    public static IReadOnlyList<string> ParseMentions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MentionPattern().Matches(text))
        {
            var id = m.Groups[1].Value.TrimEnd('.', '-', '_');
            if (id.Length == 0 || !seen.Add(id)) continue;
            result.Add(id);
            if (result.Count == MaxMentions) break;
        }
        return result;
    }

    /// <summary>Splits text into plain and mention segments so the UI can highlight mentions without rendering raw HTML.</summary>
    public static IReadOnlyList<(string Text, bool IsMention)> Segments(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var parts = new List<(string, bool)>();
        var last = 0;
        foreach (Match m in MentionPattern().Matches(text))
        {
            if (m.Index > last) parts.Add((text[last..m.Index], false));
            parts.Add((m.Value, true));
            last = m.Index + m.Length;
        }
        if (last < text.Length) parts.Add((text[last..], false));
        return parts;
    }
}

/// <summary>
/// Turns <c>@mentions</c> in a new comment into in-app notifications. Runs from the outbox after the comment is committed.
/// A mentioned user is notified only when they belong to the same tenant, are active, are not the author, and can read the
/// document (role permission + row scope), so a mention never reveals a document the recipient may not open.
/// </summary>
public sealed class CommentMentionNotifier(
    IUserDirectory users,
    INotificationStore inbox,
    IDocumentStore store,
    IMetadataProvider metadata,
    IPermissionService permissions,
    TimeProvider clock,
    INotificationLiveFeed? liveFeed = null) : IEventHandler
{
    public const int BodyPreviewLength = 280;

    public bool CanHandle(string eventType) => eventType == EventTypes.CommentAdded;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = message.Payload;
        var docType = p.GetValueOrDefault("docType");
        var name = p.GetValueOrDefault("name");
        var commentId = p.GetValueOrDefault("commentId") ?? message.Id.ToString();
        var actor = p.GetValueOrDefault("actor");
        var mentions = (p.GetValueOrDefault("mentions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (docType is null || name is null || mentions.Length == 0) return;
        if (!metadata.Current.TryGet(docType, out var meta)) return;

        var doc = await store.GetAsync(message.TenantId, docType, name, ct);
        if (doc is null) return; // deleted after the comment was written

        var tenantUsers = await users.ListAsync(message.TenantId, ct);
        var actorName = p.GetValueOrDefault("actorName") ?? actor ?? "Someone";
        var body = Preview(p.GetValueOrDefault("comment"));
        var title = string.IsNullOrWhiteSpace(meta.Title) ? docType : meta.Title;
        var notified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mentioned in mentions)
        {
            if (string.Equals(mentioned, DocumentComments.EveryoneToken, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = tenantUsers.FirstOrDefault(u => string.Equals(u.Id, mentioned, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.TenantId != message.TenantId) continue;
            if (string.Equals(target.Id, actor, StringComparison.OrdinalIgnoreCase)) continue;
            if (!permissions.Can(target, meta, DocActions.Read)) continue;
            if (!permissions.InScope(target, meta, DocActions.Read, doc.Owner, doc.Data)) continue;
            if (!notified.Add(target.Id)) continue;

            var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{message.TenantId}|comment-mention|{commentId}|{target.Id}")));
            var notification = new Notification(id, message.TenantId, target.Id,
                $"{actorName} mentioned you in {title} {name}", body, docType, name, clock.GetUtcNow());
            inbox.Add(notification);
            if (liveFeed is not null)
                await liveFeed.PublishAsync(notification, ct);
        }

        if (mentions.Any(m => string.Equals(m, DocumentComments.EveryoneToken, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var target in tenantUsers)
            {
                if (target.TenantId != message.TenantId) continue;
                if (string.Equals(target.Id, actor, StringComparison.OrdinalIgnoreCase)) continue;
                if (!permissions.Can(target, meta, DocActions.Read)) continue;
                if (!permissions.InScope(target, meta, DocActions.Read, doc.Owner, doc.Data)) continue;
                if (!notified.Add(target.Id)) continue;

                var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{message.TenantId}|comment-mention|{commentId}|{target.Id}")));
                var notification = new Notification(id, message.TenantId, target.Id,
                    $"{actorName} mentioned @everyone in {title} {name}", body, docType, name, clock.GetUtcNow());
                inbox.Add(notification);
                if (liveFeed is not null)
                    await liveFeed.PublishAsync(notification, ct);
            }
        }
    }

    private static string Preview(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length <= BodyPreviewLength ? t : string.Concat(t.AsSpan(0, BodyPreviewLength), "…");
    }
}

/// <summary>Builds the outbox event for a new comment.</summary>
internal static class CommentEvents
{
    public static OutboxMessage Create(Document doc, UserContext user, string commentId, string text, IReadOnlyList<string> mentions, DateTimeOffset now) => new()
    {
        TenantId = doc.TenantId,
        Type = EventTypes.CommentAdded,
        CreatedAt = now,
        // One event per comment (not per document version): several comments may be added to the same version.
        IdempotencyKey = $"{doc.TenantId}:{doc.DocType}:{doc.Name}:comment:{commentId}",
        Payload = new()
        {
            ["docType"] = doc.DocType,
            ["name"] = doc.Name,
            ["owner"] = doc.Owner,
            ["commentId"] = commentId,
            ["comment"] = text,
            ["mentions"] = string.Join(',', mentions),
            ["actor"] = user.Id,
            ["actorName"] = user.FullName,
            ["occurredAt"] = now.ToString("O", CultureInfo.InvariantCulture)
        }
    };
}
