using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Framework.Cluster;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Signals between nodes over PostgreSQL LISTEN/NOTIFY on channel <c>frogframe_cluster</c>.
/// One extra database connection per node stays open for LISTEN; it must be a direct (session) connection —
/// LISTEN does not work through PgBouncer in transaction pooling mode.
/// </summary>
public sealed class PostgresClusterBus(NpgsqlDataSource db, ILogger<PostgresClusterBus> log) : BackgroundService, IClusterBus
{
    public const string Channel = "frogframe_cluster";
    /// <summary>PostgreSQL limits a NOTIFY payload to 8000 bytes.</summary>
    public const int MaxPayloadBytes = 7800;

    private readonly ClusterSubscriptions _subs = new(log);
    private readonly System.Threading.Channels.Channel<string> _outbox = System.Threading.Channels.Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string NodeId { get; } = LocalClusterBus.NewNodeId();
    public bool IsDistributed => true;
    public event Action? Reconnected;

    public IDisposable Subscribe(string topic, Action<string> handler) => _subs.Add(topic, handler);

    public void Publish(string topic, string payload = "")
    {
        var envelope = JsonSerializer.Serialize(new Envelope(NodeId, topic, payload), Json);
        if (Encoding.UTF8.GetByteCount(envelope) > MaxPayloadBytes)
        {
            log.LogWarning("Cluster signal {Topic} is too large ({Bytes} bytes) and was not sent", topic, Encoding.UTF8.GetByteCount(envelope));
            return;
        }
        _outbox.Writer.TryWrite(envelope);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(SendLoop(stoppingToken), ListenLoop(stoppingToken));

    private async Task SendLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in _outbox.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await using var cmd = db.CreateCommand("SELECT pg_notify($1, $2)");
                    cmd.Parameters.AddWithValue(Channel);
                    cmd.Parameters.AddWithValue(envelope);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Cluster signal could not be sent");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        var connectedBefore = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                conn.Notification += (_, e) => OnNotification(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", conn))
                    await listen.ExecuteNonQueryAsync(ct);
                log.LogInformation("Cluster signals active (node {Node})", NodeId);
                if (connectedBefore)
                {
                    // Signals sent while we were disconnected are lost: let the subscribers re-read.
                    ThreadPool.UnsafeQueueUserWorkItem(_ => { try { Reconnected?.Invoke(); } catch (Exception ex) { log.LogWarning(ex, "Cluster resync failed"); } }, null);
                }
                connectedBefore = true;
                delay = TimeSpan.FromSeconds(1);

                while (!ct.IsCancellationRequested)
                {
                    // Wake up regularly to notice a dead connection (no notifications arrive on a broken socket).
                    if (!await conn.WaitAsync(TimeSpan.FromSeconds(30), ct))
                    {
                        await using var ping = new NpgsqlCommand("SELECT 1", conn);
                        await ping.ExecuteScalarAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Cluster signal connection lost; retrying in {Delay}", delay);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    private void OnNotification(string payload)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(payload, Json); }
        catch (JsonException) { return; }
        if (envelope is null || envelope.Node == NodeId || string.IsNullOrEmpty(envelope.Topic)) return;
        _subs.Dispatch(envelope.Topic, envelope.Payload ?? "");
    }

    private sealed record Envelope(string Node, string Topic, string? Payload);
}
