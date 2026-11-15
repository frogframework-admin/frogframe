using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Framework.Files;
using Framework.Localization;
using Framework.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Framework.Web.Services;

public enum DependencyState
{
    Ready,
    StartingUp,
    Disconnected,
    Disabled
}

public sealed record SystemConnectivitySnapshot(
    string DatabaseProvider,
    DependencyState DatabaseState,
    string? DatabaseMessage,
    bool ObjectStorageConfigured,
    string? ObjectStorageName,
    string? ObjectStorageEndpoint,
    DependencyState ObjectStorageState,
    string? ObjectStorageMessage,
    DateTimeOffset CheckedAt)
{
    public bool IsDatabaseHealthy => DatabaseState == DependencyState.Ready;
    public bool IsObjectStorageHealthy => !ObjectStorageConfigured || ObjectStorageState == DependencyState.Ready;
    public bool HasIssues => !IsDatabaseHealthy || !IsObjectStorageHealthy;
    public bool IsAllHealthy => !HasIssues;
}

/// <summary>
/// Tracks live readiness and connectivity of PostgreSQL Database and SeaweedFS / S3 Object Storage,
/// surfacing clear explanations to the UI whenever either dependency is starting up or unreachable.
/// </summary>
public sealed class SystemConnectivityService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SystemConnectivityService>? _log;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private SystemConnectivitySnapshot _current;

    public SystemConnectivityService(IServiceProvider services, ILogger<SystemConnectivityService>? log = null)
    {
        _services = services;
        _log = log;
        var dbChecker = _services.GetService<IDatabaseHealthCheck>();
        var s3Checker = _services.GetService<IObjectStorageHealthCheck>();
        _current = new SystemConnectivitySnapshot(
            DatabaseProvider: dbChecker?.ProviderName ?? "in-memory",
            DatabaseState: DependencyState.Ready,
            DatabaseMessage: null,
            ObjectStorageConfigured: s3Checker is not null,
            ObjectStorageName: s3Checker?.StorageName,
            ObjectStorageEndpoint: s3Checker?.Endpoint,
            ObjectStorageState: s3Checker is not null ? DependencyState.Ready : DependencyState.Disabled,
            ObjectStorageMessage: null,
            CheckedAt: DateTimeOffset.MinValue);
    }

    public SystemConnectivitySnapshot Current => _current;

    public event Action? Changed;

    public async Task<SystemConnectivitySnapshot> EnsureCheckedAsync(TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var age = DateTimeOffset.UtcNow - _current.CheckedAt;
        if (_current.CheckedAt != DateTimeOffset.MinValue && age < (maxAge ?? TimeSpan.FromSeconds(15)))
            return _current;
        return await CheckNowAsync(ct);
    }

    public async Task<SystemConnectivitySnapshot> CheckNowAsync(CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct);
        try
        {
            var dbChecker = _services.GetService<IDatabaseHealthCheck>();
            var s3Checker = _services.GetService<IObjectStorageHealthCheck>();

            var dbProvider = dbChecker?.ProviderName ?? "in-memory";
            var dbState = DependencyState.Ready;
            string? dbMsg = null;

            if (dbChecker is not null)
            {
                using var dbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dbCts.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    var ok = await dbChecker.CheckHealthAsync(dbCts.Token);
                    if (!ok)
                    {
                        dbState = DependencyState.Disconnected;
                        dbMsg = "Database health check returned false. Please ensure the PostgreSQL database service is running.";
                    }
                }
                catch (Exception ex)
                {
                    if (InfrastructureErrorHelper.IsDatabaseStartingUp(ex, out var detail))
                    {
                        dbState = DependencyState.StartingUp;
                        dbMsg = $"PostgreSQL database is still starting up or recovering ({detail}). Please wait until initialization finishes.";
                    }
                    else
                    {
                        dbState = DependencyState.Disconnected;
                        dbMsg = $"Cannot connect to PostgreSQL database ({InfrastructureErrorHelper.RootMessage(ex)}). Please ensure the PostgreSQL container/service is running.";
                    }
                    _log?.LogWarning(ex, "Database connectivity check failed: {Message}", dbMsg);
                }
            }

            var s3Configured = s3Checker is not null;
            var s3State = s3Configured ? DependencyState.Ready : DependencyState.Disabled;
            string? s3Msg = null;

            if (s3Checker is not null)
            {
                try
                {
                    var (healthy, err) = await s3Checker.CheckHealthAsync(ct);
                    if (!healthy)
                    {
                        s3State = DependencyState.Disconnected;
                        s3Msg = err ?? $"Cannot connect to SeaweedFS / Object Storage ({s3Checker.StorageName} at {s3Checker.Endpoint}).";
                    }
                }
                catch (Exception ex)
                {
                    s3State = DependencyState.Disconnected;
                    s3Msg = $"Cannot connect to SeaweedFS / Object Storage ({s3Checker.StorageName} at {s3Checker.Endpoint}): {InfrastructureErrorHelper.RootMessage(ex)}.";
                    _log?.LogWarning(ex, "Object storage connectivity check failed: {Message}", s3Msg);
                }
            }

            var next = new SystemConnectivitySnapshot(
                DatabaseProvider: dbProvider,
                DatabaseState: dbState,
                DatabaseMessage: dbMsg,
                ObjectStorageConfigured: s3Configured,
                ObjectStorageName: s3Checker?.StorageName,
                ObjectStorageEndpoint: s3Checker?.Endpoint,
                ObjectStorageState: s3State,
                ObjectStorageMessage: s3Msg,
                CheckedAt: DateTimeOffset.UtcNow);

            UpdateSnapshot(next);
            return next;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public void ReportObjectStorageHealthy()
    {
        if (_current.ObjectStorageState == DependencyState.Ready && _current.ObjectStorageMessage is null)
            return;
        UpdateSnapshot(_current with
        {
            ObjectStorageState = DependencyState.Ready,
            ObjectStorageMessage = null,
            CheckedAt = DateTimeOffset.UtcNow
        });
    }

    public void ReportDatabaseHealthy()
    {
        if (_current.DatabaseState == DependencyState.Ready && _current.DatabaseMessage is null)
            return;
        UpdateSnapshot(_current with
        {
            DatabaseState = DependencyState.Ready,
            DatabaseMessage = null,
            CheckedAt = DateTimeOffset.UtcNow
        });
    }

    public void ReportException(Exception ex)
    {
        if (InfrastructureErrorHelper.IsObjectStorageError(ex, out var s3Detail))
        {
            UpdateSnapshot(_current with
            {
                ObjectStorageConfigured = true,
                ObjectStorageState = DependencyState.Disconnected,
                ObjectStorageMessage = s3Detail,
                CheckedAt = DateTimeOffset.UtcNow
            });
        }
        else if (InfrastructureErrorHelper.IsDatabaseStartingUp(ex, out var startDetail))
        {
            UpdateSnapshot(_current with
            {
                DatabaseState = DependencyState.StartingUp,
                DatabaseMessage = $"PostgreSQL database is still starting up or recovering ({startDetail}). Please wait a moment and try again.",
                CheckedAt = DateTimeOffset.UtcNow
            });
        }
        else if (InfrastructureErrorHelper.IsDatabaseConnectivityError(ex, out var dbDetail))
        {
            UpdateSnapshot(_current with
            {
                DatabaseState = DependencyState.Disconnected,
                DatabaseMessage = $"Cannot connect to PostgreSQL database or the operation timed out ({dbDetail}).",
                CheckedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private void UpdateSnapshot(SystemConnectivitySnapshot next)
    {
        var changed = _current.DatabaseState != next.DatabaseState
            || _current.DatabaseMessage != next.DatabaseMessage
            || _current.ObjectStorageState != next.ObjectStorageState
            || _current.ObjectStorageMessage != next.ObjectStorageMessage;
        _current = next;
        if (changed)
        {
            try { Changed?.Invoke(); }
            catch { }
        }
    }
}

public static class InfrastructureErrorHelper
{
    public static bool TryDescribe(Exception ex, UiText? t, out string title, out string message)
    {
        string Tr(string s) => t is not null ? t[s] : s;

        if (IsObjectStorageError(ex, out var s3Detail))
        {
            title = Tr("SeaweedFS Object Storage Not Connected");
            message = $"{Tr("Cannot connect to SeaweedFS / Object Storage. Please ensure the SeaweedFS service is running.")} ({s3Detail})";
            return true;
        }

        if (IsDatabaseStartingUp(ex, out var startDetail))
        {
            title = Tr("Database Not Ready");
            message = $"{Tr("PostgreSQL database is not ready yet because it is still starting up or performing recovery. Please wait a moment and try again.")} ({startDetail})";
            return true;
        }

        if (IsDatabaseConnectivityError(ex, out var dbDetail))
        {
            title = Tr("Database Connection Error");
            message = $"{Tr("Cannot connect to the PostgreSQL database or the operation timed out. Please ensure the PostgreSQL database service is running and healthy.")} ({dbDetail})";
            return true;
        }

        title = string.Empty;
        message = string.Empty;
        return false;
    }

    public static bool IsObjectStorageError(Exception ex, out string detail)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is ObjectStorageUnavailableException osu)
            {
                detail = osu.Message;
                return true;
            }
            if (cur is AmazonS3Exception or AmazonServiceException)
            {
                detail = RootMessage(cur);
                return true;
            }
        }
        detail = string.Empty;
        return false;
    }

    public static bool IsDatabaseStartingUp(Exception ex, out string detail)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var typeName = cur.GetType().Name;
            if (typeName == "PostgresException")
            {
                var sqlState = cur.GetType().GetProperty("SqlState")?.GetValue(cur) as string;
                var msgText = cur.GetType().GetProperty("MessageText")?.GetValue(cur) as string ?? cur.Message;
                if (sqlState is "57P03" or "57P01" or "57P02")
                {
                    detail = $"SQLSTATE {sqlState}: {msgText}";
                    return true;
                }
            }
            if (cur.Message.Contains("57P03", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("the database system is starting up", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("the database system is in recovery mode", StringComparison.OrdinalIgnoreCase))
            {
                detail = cur.Message;
                return true;
            }
        }
        detail = string.Empty;
        return false;
    }

    public static bool IsDatabaseConnectivityError(Exception ex, out string detail)
    {
        if (IsDatabaseStartingUp(ex, out detail))
            return true;

        var hasNpgsqlContext = false;
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var typeName = cur.GetType().FullName ?? cur.GetType().Name;
            if (typeName.StartsWith("Npgsql.", StringComparison.Ordinal))
            {
                hasNpgsqlContext = true;
                if (cur.GetType().Name == "PostgresException")
                {
                    var sqlState = cur.GetType().GetProperty("SqlState")?.GetValue(cur) as string;
                    // Class 08 = Connection Exception, 53300 = too_many_connections, 57014 = query_canceled (statement timeout)
                    if (sqlState is not null && (sqlState.StartsWith("08", StringComparison.Ordinal) || sqlState is "53300" or "57014"))
                    {
                        var msgText = cur.GetType().GetProperty("MessageText")?.GetValue(cur) as string ?? cur.Message;
                        detail = $"SQLSTATE {sqlState}: {msgText}";
                        return true;
                    }
                    // Other PostgresExceptions (e.g. 23505 unique violation, P0001 trigger error) are business/schema errors, not connectivity errors.
                    detail = string.Empty;
                    return false;
                }
            }
            if (cur is ObjectDisposedException ode && ode.ObjectName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                hasNpgsqlContext = true;
            }
        }

        if (hasNpgsqlContext)
        {
            detail = RootMessage(ex);
            return true;
        }

        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException or TimeoutException)
            {
                detail = cur.Message;
                return true;
            }
        }

        detail = string.Empty;
        return false;
    }

    public static string RootMessage(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null) cur = cur.InnerException;
        return cur.Message;
    }
}
