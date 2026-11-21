using Framework.Social;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>Tags, bookmarks and follows in <c>frogframe_doc_tags</c>, <c>frogframe_bookmarks</c>, <c>frogframe_doc_follows</c> (migration 040).</summary>
public sealed class PostgresDocSocialStore(NpgsqlDataSource db) : IDocSocialStore
{
    private NpgsqlCommand Cmd(string sql, params object[] args)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return cmd;
    }

    private static async Task<List<T>> ReadAll<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> read, CancellationToken ct)
    {
        await using (cmd)
        {
            var list = new List<T>();
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(read(r));
            return list;
        }
    }

    private static async Task<int> Exec(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using (cmd) return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---- tags

    public async Task<IReadOnlyList<DocTag>> TagsAsync(string tenantId, string docType, string docName, CancellationToken ct = default) =>
        await ReadAll(Cmd("SELECT tenant_id, doctype, doc_name, tag, tagged_by, tagged_at FROM frogframe_doc_tags " +
                          "WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 ORDER BY LOWER(tag)", tenantId, docType, docName),
            r => new DocTag(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)), ct);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> TagsForAsync(string tenantId, string docType, IReadOnlyCollection<string> docNames, CancellationToken ct = default)
    {
        if (docNames.Count == 0) return new Dictionary<string, IReadOnlyList<string>>();
        var rows = await ReadAll(Cmd("SELECT doc_name, tag FROM frogframe_doc_tags WHERE tenant_id = $1 AND doctype = $2 AND doc_name = ANY($3) " +
                                     "ORDER BY doc_name, LOWER(tag)", tenantId, docType, docNames.ToArray()),
            r => (Name: r.GetString(0), Tag: r.GetString(1)), ct);
        return rows.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Tag).ToList());
    }

    public async Task<bool> AddTagAsync(DocTag tag, CancellationToken ct = default) =>
        await Exec(Cmd("INSERT INTO frogframe_doc_tags (tenant_id, doctype, doc_name, tag, tagged_by, tagged_at) VALUES ($1, $2, $3, $4, $5, $6) " +
                       "ON CONFLICT (tenant_id, doctype, doc_name, LOWER(tag)) DO NOTHING",
            tag.TenantId, tag.DocType, tag.DocName, tag.Tag, tag.TaggedBy, tag.TaggedAt), ct) > 0;

    public async Task<bool> RemoveTagAsync(string tenantId, string docType, string docName, string tag, CancellationToken ct = default) =>
        await Exec(Cmd("DELETE FROM frogframe_doc_tags WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 AND LOWER(tag) = LOWER($4)",
            tenantId, docType, docName, tag), ct) > 0;

    public async Task<IReadOnlyList<string>> NamesWithTagAsync(string tenantId, string docType, string tag, int max, CancellationToken ct = default) =>
        await ReadAll(Cmd("SELECT doc_name FROM frogframe_doc_tags WHERE tenant_id = $1 AND doctype = $2 AND LOWER(tag) = LOWER($3) " +
                          "ORDER BY doc_name LIMIT $4", tenantId, docType, tag, max), r => r.GetString(0), ct);

    public async Task<IReadOnlyList<TagUsage>> TagUsageAsync(string tenantId, string docType, string? prefix, int max, CancellationToken ct = default) =>
        // The spelling shown is the one used first.
        await ReadAll(Cmd("SELECT (array_agg(tag ORDER BY tagged_at))[1], count(*)::int FROM frogframe_doc_tags " +
                          "WHERE tenant_id = $1 AND doctype = $2 AND ($3 = '' OR LOWER(tag) LIKE LOWER($3) || '%') " +
                          "GROUP BY LOWER(tag) ORDER BY count(*) DESC, LOWER(tag) LIMIT $4",
                tenantId, docType, EscapeLike(prefix ?? ""), max),
            r => new TagUsage(r.GetString(0), r.GetInt32(1)), ct);

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ---- bookmarks

    public async Task<bool> AddBookmarkAsync(Bookmark b, CancellationToken ct = default) =>
        await Exec(Cmd("INSERT INTO frogframe_bookmarks (tenant_id, user_id, doctype, doc_name, created_at) VALUES ($1, $2, $3, $4, $5) " +
                       "ON CONFLICT (tenant_id, LOWER(user_id), doctype, doc_name) DO NOTHING",
            b.TenantId, b.UserId, b.DocType, b.DocName, b.CreatedAt), ct) > 0;

    public async Task<bool> RemoveBookmarkAsync(string tenantId, string userId, string docType, string docName, CancellationToken ct = default) =>
        await Exec(Cmd("DELETE FROM frogframe_bookmarks WHERE tenant_id = $1 AND LOWER(user_id) = LOWER($2) AND doctype = $3 AND doc_name = $4",
            tenantId, userId, docType, docName), ct) > 0;

    public async Task<bool> IsBookmarkedAsync(string tenantId, string userId, string docType, string docName, CancellationToken ct = default) =>
        (await ReadAll(Cmd("SELECT 1 FROM frogframe_bookmarks WHERE tenant_id = $1 AND LOWER(user_id) = LOWER($2) AND doctype = $3 AND doc_name = $4",
            tenantId, userId, docType, docName), _ => 1, ct)).Count > 0;

    public async Task<IReadOnlyList<Bookmark>> BookmarksAsync(string tenantId, string userId, int max, CancellationToken ct = default) =>
        await ReadAll(Cmd("SELECT tenant_id, user_id, doctype, doc_name, created_at FROM frogframe_bookmarks " +
                          "WHERE tenant_id = $1 AND LOWER(user_id) = LOWER($2) ORDER BY created_at DESC LIMIT $3", tenantId, userId, max),
            r => new Bookmark(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)), ct);

    public async Task<int> CountBookmarksAsync(string tenantId, string userId, CancellationToken ct = default) =>
        (await ReadAll(Cmd("SELECT count(*)::int FROM frogframe_bookmarks WHERE tenant_id = $1 AND LOWER(user_id) = LOWER($2)", tenantId, userId),
            r => r.GetInt32(0), ct))[0];

    // ---- follows

    public async Task<bool> FollowAsync(DocFollow f, CancellationToken ct = default) =>
        await Exec(Cmd("INSERT INTO frogframe_doc_follows (tenant_id, doctype, doc_name, user_id, followed_at) VALUES ($1, $2, $3, $4, $5) " +
                       "ON CONFLICT (tenant_id, doctype, doc_name, LOWER(user_id)) DO NOTHING",
            f.TenantId, f.DocType, f.DocName, f.UserId, f.FollowedAt), ct) > 0;

    public async Task<bool> UnfollowAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default) =>
        await Exec(Cmd("DELETE FROM frogframe_doc_follows WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 AND LOWER(user_id) = LOWER($4)",
            tenantId, docType, docName, userId), ct) > 0;

    public async Task<bool> IsFollowingAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default) =>
        (await ReadAll(Cmd("SELECT 1 FROM frogframe_doc_follows WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 AND LOWER(user_id) = LOWER($4)",
            tenantId, docType, docName, userId), _ => 1, ct)).Count > 0;

    public async Task<IReadOnlyList<DocFollow>> FollowersAsync(string tenantId, string docType, string docName, CancellationToken ct = default) =>
        await ReadAll(Cmd("SELECT tenant_id, doctype, doc_name, user_id, followed_at FROM frogframe_doc_follows " +
                          "WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 ORDER BY followed_at", tenantId, docType, docName),
            r => new DocFollow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)), ct);

    public async Task<IReadOnlyList<DocFollow>> FollowingAsync(string tenantId, string userId, int max, CancellationToken ct = default) =>
        await ReadAll(Cmd("SELECT tenant_id, doctype, doc_name, user_id, followed_at FROM frogframe_doc_follows " +
                          "WHERE tenant_id = $1 AND LOWER(user_id) = LOWER($2) ORDER BY followed_at DESC LIMIT $3", tenantId, userId, max),
            r => new DocFollow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)), ct);

    public async Task<int> RemoveDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var table in new[] { "frogframe_doc_tags", "frogframe_bookmarks", "frogframe_doc_follows" })
            n += await Exec(Cmd($"DELETE FROM {table} WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3", tenantId, docType, docName), ct);
        return n;
    }
}
