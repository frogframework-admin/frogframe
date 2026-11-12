using Framework.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Moves file contents that are still in the database (written before Storage:Provider = S3) to object storage,
/// in small batches, when Storage:MigrateExisting = true. Safe to run on several nodes (advisory lock) and to stop
/// at any moment: a row is emptied only after its object was written, and rows keep working either way.
/// </summary>
public sealed class PostgresBlobMigrationWorker(NpgsqlDataSource db, IServiceProvider services, IConfiguration configuration,
    ILogger<PostgresBlobMigrationWorker> log) : BackgroundService
{
    private const long LockKey = 0x46524F47_424C4F42; // "FROGBLOB"
    private const int BatchSize = 20;

    private sealed record Table(string Name, string Kind, Func<string, string, string> Key);

    private static readonly Table[] Tables =
    [
        new("frogframe_files", "file", BlobKeys.File),
        new("frogframe_attachments", "attachment", BlobKeys.Attachment),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Storage:MigrateExisting", false)) return;
        var blobs = services.GetService<IBlobStorage>();
        if (blobs is null)
        {
            log.LogWarning("Storage:MigrateExisting is set but object storage is not configured (Storage:Provider = S3); nothing is moved");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; } // let the app start first

        await using var lockConn = await db.OpenConnectionAsync(stoppingToken);
        await using (var tryLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey})", lockConn))
            if (await tryLock.ExecuteScalarAsync(stoppingToken) is not true)
            {
                log.LogInformation("Another node is moving file contents to {Storage}", blobs.Name);
                return;
            }

        try
        {
            foreach (var table in Tables)
            {
                var moved = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    var batch = await NextBatchAsync(table, stoppingToken);
                    if (batch.Count == 0) break;
                    foreach (var (id, tenant, contentType, content) in batch)
                    {
                        await blobs.PutAsync(table.Key(tenant, id), content, contentType, stoppingToken);
                        // Only empty the row if it still holds the content we copied.
                        await using var clear = db.CreateCommand($"UPDATE {table.Name} SET content = ''::bytea WHERE id = $1 AND tenant_id = $2 AND length(content) > 0");
                        clear.Parameters.AddWithValue(id);
                        clear.Parameters.AddWithValue(tenant);
                        await clear.ExecuteNonQueryAsync(stoppingToken);
                        moved++;
                    }
                }
                if (moved > 0) log.LogInformation("Moved {Count} {Kind} contents from the database to {Storage}", moved, table.Kind, blobs.Name);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError(ex, "Moving file contents to {Storage} stopped; it continues at the next start", blobs.Name);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({LockKey})", lockConn);
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task<List<(string Id, string Tenant, string ContentType, byte[] Content)>> NextBatchAsync(Table table, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT id, tenant_id, content_type, content FROM {table.Name} WHERE length(content) > 0 LIMIT {BatchSize}");
        var list = new List<(string, string, string, byte[])>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<byte[]>(3)));
        return list;
    }
}
