namespace Framework.Persistence;

public enum StartupPhase
{
    Ready,
    WaitingForDatabase,
    Failed
}

/// <summary>
/// Tracks application startup readiness (database connection, migrations, and initial seeding)
/// so HTTP endpoints can present clear English status messages and background workers can wait cleanly.
/// </summary>
public sealed class StartupReadiness
{
    private readonly TaskCompletionSource<bool> _dbReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile StartupPhase _databasePhase = StartupPhase.Ready;
    private volatile string? _databaseMessage;
    private volatile string? _databaseDetail;

    public StartupPhase DatabasePhase => _databasePhase;
    public bool IsDatabaseReady => _databasePhase == StartupPhase.Ready;
    public bool HasDatabaseFailed => _databasePhase == StartupPhase.Failed;
    public string? DatabaseMessage => _databaseMessage;
    public string? DatabaseDetail => _databaseDetail;

    public void BeginDatabaseStartup(string? message = null)
    {
        _databasePhase = StartupPhase.WaitingForDatabase;
        _databaseMessage = message ?? "Waiting for the database to be ready...";
        _databaseDetail = null;
    }

    public void ReportDatabaseWaiting(string? detail = null)
    {
        if (_databasePhase == StartupPhase.Ready) return;
        _databasePhase = StartupPhase.WaitingForDatabase;
        _databaseMessage = "Waiting for the database to be ready...";
        _databaseDetail = detail;
    }

    public void ReportDatabaseReady()
    {
        _databasePhase = StartupPhase.Ready;
        _databaseMessage = null;
        _databaseDetail = null;
        _dbReadyTcs.TrySetResult(true);
    }

    public void ReportDatabaseFailed(string error)
    {
        _databasePhase = StartupPhase.Failed;
        _databaseMessage = "Database initialization failed.";
        _databaseDetail = error;
        _dbReadyTcs.TrySetResult(false);
    }

    /// <summary>
    /// Waits until database startup completes. Returns <c>true</c> when ready, or <c>false</c> if startup failed.
    /// </summary>
    public Task<bool> WaitForDatabaseReadyAsync(CancellationToken ct = default)
    {
        if (_databasePhase == StartupPhase.Ready) return Task.FromResult(true);
        if (_databasePhase == StartupPhase.Failed) return Task.FromResult(false);
        return _dbReadyTcs.Task.WaitAsync(ct);
    }
}
