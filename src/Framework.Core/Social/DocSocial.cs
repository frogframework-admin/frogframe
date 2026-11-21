using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Framework.Social;

/// <summary>A label on a document. Tags are shared: everyone who can read the document sees them.</summary>
public sealed record DocTag(string TenantId, string DocType, string DocName, string Tag, string TaggedBy, DateTimeOffset TaggedAt);

/// <summary>A document a user saved for quick access. Personal: only that user sees it.</summary>
public sealed record Bookmark(string TenantId, string UserId, string DocType, string DocName, DateTimeOffset CreatedAt);

/// <summary>A user who is notified when a document changes.</summary>
public sealed record DocFollow(string TenantId, string DocType, string DocName, string UserId, DateTimeOffset FollowedAt);

/// <summary>How many documents of a DocType carry a tag (for suggestions and the tag filter).</summary>
public sealed record TagUsage(string Tag, int Count);

public static class DocSocialLimits
{
    public const int MaxTagLength = 40;
    public const int MaxTagsPerDocument = 20;
    /// <summary>Documents a tag filter can return in one list query (the rest is reached by narrowing the filter).</summary>
    public const int MaxNamesPerTagFilter = 5_000;
    public const int MaxBookmarksPerUser = 500;
    public const int MaxFollowersPerDocument = 500;
}

public static partial class DocTags
{
    /// <summary>List filter field for tags: <c>{"field": "_tag", "op": "=", "value": "urgent"}</c>.</summary>
    public const string FilterField = "_tag";

    /// <summary>Row key with the tags of each document in list results.</summary>
    public const string ListKey = "_tags";

    /// <summary>
    /// Trims and collapses spaces. A tag is 1–<see cref="DocSocialLimits.MaxTagLength"/> characters of letters, digits,
    /// spaces and <c>- _ . / &amp; #</c>; tags compare case-insensitively. Returns null with a reason when invalid.
    /// </summary>
    public static string? Normalize(string? raw, out string? error)
    {
        var tag = SpacesRx().Replace((raw ?? "").Trim(), " ");
        error = tag.Length == 0 ? "Enter a tag."
            : tag.Length > DocSocialLimits.MaxTagLength ? $"A tag can have at most {DocSocialLimits.MaxTagLength} characters."
            : !AllowedRx().IsMatch(tag) ? "A tag can only contain letters, digits, spaces and - _ . / & #."
            : null;
        return error is null ? tag : null;
    }

    /// <summary>
    /// Colour hue (0–359) of a tag: fixed per tag (case-insensitive), so the same tag has the same colour everywhere
    /// without anyone choosing it.
    /// </summary>
    public static int Hue(string tag)
    {
        var h = 2166136261u; // FNV-1a
        foreach (var c in tag.ToLowerInvariant()) { h ^= c; h *= 16777619u; }
        return (int)(h % 360);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRx();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} \-_./&#]*$")]
    private static partial Regex AllowedRx();
}

/// <summary>
/// Storage of tags, bookmarks and follows (tables <c>frogframe_doc_tags</c>, <c>frogframe_bookmarks</c> and
/// <c>frogframe_doc_follows</c> in PostgreSQL). Tags and user ids compare case-insensitively.
/// </summary>
public interface IDocSocialStore
{
    Task<IReadOnlyList<DocTag>> TagsAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
    /// <summary>Tags of several documents at once (list views), by document name.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> TagsForAsync(string tenantId, string docType, IReadOnlyCollection<string> docNames, CancellationToken ct = default);
    /// <summary>False when the document already has the tag (in any letter case).</summary>
    Task<bool> AddTagAsync(DocTag tag, CancellationToken ct = default);
    Task<bool> RemoveTagAsync(string tenantId, string docType, string docName, string tag, CancellationToken ct = default);
    Task<IReadOnlyList<string>> NamesWithTagAsync(string tenantId, string docType, string tag, int max, CancellationToken ct = default);
    /// <summary>Tags used on a DocType, most used first; optionally only those starting with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<TagUsage>> TagUsageAsync(string tenantId, string docType, string? prefix, int max, CancellationToken ct = default);

    Task<bool> AddBookmarkAsync(Bookmark bookmark, CancellationToken ct = default);
    Task<bool> RemoveBookmarkAsync(string tenantId, string userId, string docType, string docName, CancellationToken ct = default);
    Task<bool> IsBookmarkedAsync(string tenantId, string userId, string docType, string docName, CancellationToken ct = default);
    /// <summary>A user's bookmarks, newest first.</summary>
    Task<IReadOnlyList<Bookmark>> BookmarksAsync(string tenantId, string userId, int max, CancellationToken ct = default);
    Task<int> CountBookmarksAsync(string tenantId, string userId, CancellationToken ct = default);

    Task<bool> FollowAsync(DocFollow follow, CancellationToken ct = default);
    Task<bool> UnfollowAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default);
    Task<bool> IsFollowingAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<DocFollow>> FollowersAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
    /// <summary>Documents a user follows, newest first.</summary>
    Task<IReadOnlyList<DocFollow>> FollowingAsync(string tenantId, string userId, int max, CancellationToken ct = default);

    /// <summary>Removes the tags, bookmarks and follows of a deleted document.</summary>
    Task<int> RemoveDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
}

public sealed class InMemoryDocSocialStore : IDocSocialStore
{
    private readonly ConcurrentDictionary<(string Tenant, string DocType, string DocName, string Tag), DocTag> tags = new();
    private readonly ConcurrentDictionary<(string Tenant, string User, string DocType, string DocName), Bookmark> bookmarks = new();
    private readonly ConcurrentDictionary<(string Tenant, string DocType, string DocName, string User), DocFollow> follows = new();

    private static string Low(string s) => s.ToLowerInvariant();

    public Task<IReadOnlyList<DocTag>> TagsAsync(string tenantId, string docType, string docName, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocTag>>(tags.Values
            .Where(t => t.TenantId == tenantId && t.DocType == docType && t.DocName == docName)
            .OrderBy(t => t.Tag, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> TagsForAsync(string tenantId, string docType, IReadOnlyCollection<string> docNames, CancellationToken ct = default)
    {
        var wanted = docNames.ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(tags.Values
            .Where(t => t.TenantId == tenantId && t.DocType == docType && wanted.Contains(t.DocName))
            .GroupBy(t => t.DocName)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => t.Tag).Order(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    public Task<bool> AddTagAsync(DocTag tag, CancellationToken ct = default) =>
        Task.FromResult(tags.TryAdd((tag.TenantId, tag.DocType, tag.DocName, Low(tag.Tag)), tag));

    public Task<bool> RemoveTagAsync(string tenantId, string docType, string docName, string tag, CancellationToken ct = default) =>
        Task.FromResult(tags.TryRemove((tenantId, docType, docName, Low(tag)), out _));

    public Task<IReadOnlyList<string>> NamesWithTagAsync(string tenantId, string docType, string tag, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(tags.Keys
            .Where(k => k.Tenant == tenantId && k.DocType == docType && k.Tag == Low(tag))
            .Select(k => k.DocName).Order(StringComparer.Ordinal).Take(max).ToList());

    public Task<IReadOnlyList<TagUsage>> TagUsageAsync(string tenantId, string docType, string? prefix, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TagUsage>>(tags.Values
            .Where(t => t.TenantId == tenantId && t.DocType == docType
                        && (string.IsNullOrEmpty(prefix) || t.Tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(t => Low(t.Tag))
            .Select(g => new TagUsage(g.OrderBy(t => t.TaggedAt).First().Tag, g.Count()))
            .OrderByDescending(u => u.Count).ThenBy(u => u.Tag, StringComparer.OrdinalIgnoreCase).Take(max).ToList());

    public Task<bool> AddBookmarkAsync(Bookmark bookmark, CancellationToken ct = default) =>
        Task.FromResult(bookmarks.TryAdd((bookmark.TenantId, Low(bookmark.UserId), bookmark.DocType, bookmark.DocName), bookmark));

    public Task<bool> RemoveBookmarkAsync(string tenantId, string userId, string docType, string docName, CancellationToken ct = default) =>
        Task.FromResult(bookmarks.TryRemove((tenantId, Low(userId), docType, docName), out _));

    public Task<bool> IsBookmarkedAsync(string tenantId, string userId, string docType, string docName, CancellationToken ct = default) =>
        Task.FromResult(bookmarks.ContainsKey((tenantId, Low(userId), docType, docName)));

    public Task<IReadOnlyList<Bookmark>> BookmarksAsync(string tenantId, string userId, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Bookmark>>(bookmarks
            .Where(kv => kv.Key.Tenant == tenantId && kv.Key.User == Low(userId))
            .Select(kv => kv.Value).OrderByDescending(b => b.CreatedAt).Take(max).ToList());

    public Task<int> CountBookmarksAsync(string tenantId, string userId, CancellationToken ct = default) =>
        Task.FromResult(bookmarks.Keys.Count(k => k.Tenant == tenantId && k.User == Low(userId)));

    public Task<bool> FollowAsync(DocFollow follow, CancellationToken ct = default) =>
        Task.FromResult(follows.TryAdd((follow.TenantId, follow.DocType, follow.DocName, Low(follow.UserId)), follow));

    public Task<bool> UnfollowAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default) =>
        Task.FromResult(follows.TryRemove((tenantId, docType, docName, Low(userId)), out _));

    public Task<bool> IsFollowingAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default) =>
        Task.FromResult(follows.ContainsKey((tenantId, docType, docName, Low(userId))));

    public Task<IReadOnlyList<DocFollow>> FollowersAsync(string tenantId, string docType, string docName, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocFollow>>(follows.Values
            .Where(f => f.TenantId == tenantId && f.DocType == docType && f.DocName == docName)
            .OrderBy(f => f.FollowedAt).ToList());

    public Task<IReadOnlyList<DocFollow>> FollowingAsync(string tenantId, string userId, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocFollow>>(follows
            .Where(kv => kv.Key.Tenant == tenantId && kv.Key.User == Low(userId))
            .Select(kv => kv.Value).OrderByDescending(f => f.FollowedAt).Take(max).ToList());

    public Task<int> RemoveDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        var n = tags.Keys.Where(k => k.Tenant == tenantId && k.DocType == docType && k.DocName == docName).Count(k => tags.TryRemove(k, out _));
        n += bookmarks.Keys.Where(k => k.Tenant == tenantId && k.DocType == docType && k.DocName == docName).Count(k => bookmarks.TryRemove(k, out _));
        n += follows.Keys.Where(k => k.Tenant == tenantId && k.DocType == docType && k.DocName == docName).Count(k => follows.TryRemove(k, out _));
        return Task.FromResult(n);
    }
}
