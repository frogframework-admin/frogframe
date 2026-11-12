using Framework.Localization;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresTranslationStore(NpgsqlDataSource db) : ITranslationStore
{
    public async Task<IReadOnlyList<TranslationOverride>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT language, source, target, modified_by, modified FROM frogframe_translations WHERE tenant_id = $1 ORDER BY language, source");
        cmd.Parameters.AddWithValue(tenantId);
        var list = new List<TranslationOverride>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TranslationOverride
            {
                TenantId = tenantId,
                Language = r.GetString(0),
                Source = r.GetString(1),
                Target = r.GetString(2),
                ModifiedBy = r.GetString(3),
                Modified = r.GetFieldValue<DateTimeOffset>(4),
            });
        }
        return list;
    }

    public async Task SaveAsync(TranslationOverride t, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO frogframe_translations (tenant_id, language, source, target, modified_by, modified)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (tenant_id, language, source) DO UPDATE SET
                target = EXCLUDED.target, modified_by = EXCLUDED.modified_by, modified = EXCLUDED.modified
            """);
        cmd.Parameters.AddWithValue(t.TenantId);
        cmd.Parameters.AddWithValue(t.Language);
        cmd.Parameters.AddWithValue(t.Source);
        cmd.Parameters.AddWithValue(t.Target);
        cmd.Parameters.AddWithValue(t.ModifiedBy);
        cmd.Parameters.AddWithValue(t.Modified);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string tenantId, string language, string source, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_translations WHERE tenant_id = $1 AND language = $2 AND source = $3");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(language);
        cmd.Parameters.AddWithValue(source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
