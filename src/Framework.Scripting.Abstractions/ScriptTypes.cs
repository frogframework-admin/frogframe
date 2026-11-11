using System.Collections.Concurrent;
using System.Globalization;

namespace Framework.Scripting.Abstractions;

public enum ServerScriptType
{
    DocEvent,
    Api,
    Scheduled
}

public enum DocEventStage
{
    BeforeValidate,
    BeforeInsert,
    AfterSave,
    BeforeTransition,
    BeforeDelete,
    AfterDelete,
    OnCancel,
    OnAmend
}

public enum ServerScriptStatus
{
    Active,
    Disabled
}

public sealed class ScriptUser
{
    public required string Id { get; init; }
    public string? Email { get; init; }
    public string? Department { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];

    public bool IsInRole(string role) =>
        Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}

public sealed class ScriptDoc
{
    private readonly IDictionary<string, object?> _data;

    public ScriptDoc(IDictionary<string, object?> data, string docType = "", string name = "")
    {
        _data = data ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        DocType = docType;
        Name = name;
    }

    public string DocType { get; }
    public string Name { get; }
    public IDictionary<string, object?> Data => _data;

    public object? this[string key]
    {
        get => _data.TryGetValue(key, out var val) ? val : null;
        set => _data[key] = value;
    }

    public bool ContainsKey(string key) => _data.ContainsKey(key);

    public string? GetString(string key) => this[key]?.ToString();

    public decimal GetDecimal(string key, decimal defaultValue = 0m)
    {
        var val = this[key];
        if (val is null) return defaultValue;
        if (val is decimal d) return d;
        if (val is double db) return (decimal)db;
        if (val is int i) return i;
        if (val is long l) return l;
        if (decimal.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        var val = this[key];
        if (val is null) return defaultValue;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (int.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        var val = this[key];
        if (val is null) return defaultValue;
        if (val is bool b) return b;
        if (bool.TryParse(val.ToString(), out var parsed))
            return parsed;
        return defaultValue;
    }

    public IReadOnlyList<ScriptDoc> GetTable(string key)
    {
        if (!_data.TryGetValue(key, out var val) || val is null)
            return [];

        if (val is IEnumerable<IDictionary<string, object?>> dictList)
        {
            return dictList.Select(d => new ScriptDoc(d)).ToList();
        }

        if (val is IEnumerable<object> objList)
        {
            var res = new List<ScriptDoc>();
            foreach (var item in objList)
            {
                if (item is IDictionary<string, object?> d)
                    res.Add(new ScriptDoc(d));
            }
            return res;
        }

        return [];
    }
}

public interface IScriptDocs
{
    Task<ScriptDoc?> GetAsync(string docType, string name, CancellationToken ct = default);
    Task<ScriptDoc> InsertAsync(string docType, IDictionary<string, object?> data, CancellationToken ct = default);
    Task<ScriptDoc> UpdateAsync(string docType, string name, IDictionary<string, object?> data, CancellationToken ct = default);
    Task DeleteAsync(string docType, string name, CancellationToken ct = default);
    Task<ScriptDoc> RunActionAsync(string docType, string name, string action, string? comment = null, CancellationToken ct = default);
}

public readonly record struct ScriptLogEntry(DateTimeOffset Timestamp, string Level, string Message);

public interface IScriptLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    IReadOnlyList<ScriptLogEntry> Entries { get; }
}

public sealed class ScriptLog : IScriptLog
{
    private readonly ConcurrentQueue<ScriptLogEntry> _entries = new();

    public void Info(string message) => _entries.Enqueue(new ScriptLogEntry(DateTimeOffset.UtcNow, "INFO", message));
    public void Warn(string message) => _entries.Enqueue(new ScriptLogEntry(DateTimeOffset.UtcNow, "WARN", message));
    public void Error(string message) => _entries.Enqueue(new ScriptLogEntry(DateTimeOffset.UtcNow, "ERROR", message));

    public IReadOnlyList<ScriptLogEntry> Entries => _entries.ToArray();
}

public interface IScriptEmail
{
    Task SendAsync(string to, string subject, string bodyText, string? bodyHtml = null, CancellationToken ct = default);
}

/// <summary>
/// Execution budget for one script run. The compiler injects <c>Tick()</c> at the start of every loop body,
/// lambda body and catch block. Each tick enforces the statement limit, the wall-clock deadline, cancellation,
/// the remaining stack (deep recursion through delegates) and an approximate allocation limit.
/// Once tripped, every later tick throws again, so user code cannot swallow the exception and keep running.
/// The constructor is internal: scripts cannot create a new, larger budget.
/// </summary>
public sealed class ScriptBudget
{
    public const int DefaultMaxStatements = 50_000;
    public const long DefaultMaxAllocatedBytes = 256L * 1024 * 1024;

    private readonly long _deadlineTimestamp;
    private readonly CancellationToken _cancellationToken;
    private readonly long _maxAllocatedBytes;
    private int _count;
    private long _allocated;
    private long _lastThreadBytes;
    private int _lastThreadId;
    private string? _trippedReason;

    internal ScriptBudget(
        int maxStatements = DefaultMaxStatements,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        long maxAllocatedBytes = DefaultMaxAllocatedBytes)
    {
        MaxStatements = maxStatements;
        Timeout = timeout ?? TimeSpan.FromSeconds(5);
        _deadlineTimestamp = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(Timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        _cancellationToken = cancellationToken;
        _maxAllocatedBytes = maxAllocatedBytes;
        _lastThreadId = Environment.CurrentManagedThreadId;
        _lastThreadBytes = GC.GetAllocatedBytesForCurrentThread();
    }

    public int MaxStatements { get; }
    public TimeSpan Timeout { get; }
    public int Count => _count;
    public bool IsTripped => _trippedReason is not null;

    public void Tick()
    {
        if (_trippedReason is not null)
            throw new ScriptBudgetExceededException(_trippedReason);

        if (Interlocked.Increment(ref _count) > MaxStatements)
            Trip($"Script execution exceeded statement budget limit of {MaxStatements:N0} statements.");

        if (System.Diagnostics.Stopwatch.GetTimestamp() > _deadlineTimestamp)
            Trip($"Script execution exceeded the time limit of {Timeout.TotalMilliseconds:N0} ms.");

        if (_cancellationToken.IsCancellationRequested)
            Trip("Script execution was cancelled.");

        // Deep recursion (e.g. a delegate that calls itself) would end in a StackOverflowException,
        // which cannot be caught and terminates the whole process. Stop well before that.
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            Trip("Script recursion is too deep.");

        // Approximate allocation accounting. Awaits can move the script to another thread, so only the
        // allocation delta observed on the same thread between two ticks is counted.
        var threadId = Environment.CurrentManagedThreadId;
        var threadBytes = GC.GetAllocatedBytesForCurrentThread();
        if (threadId == _lastThreadId && threadBytes >= _lastThreadBytes)
            _allocated += threadBytes - _lastThreadBytes;
        _lastThreadId = threadId;
        _lastThreadBytes = threadBytes;
        if (_allocated > _maxAllocatedBytes)
            Trip($"Script execution exceeded the memory allocation limit of {_maxAllocatedBytes / (1024 * 1024):N0} MB.");
    }

    private void Trip(string reason)
    {
        _trippedReason ??= reason;
        throw new ScriptBudgetExceededException(_trippedReason);
    }
}

public sealed class ScriptContext
{
    public ScriptDoc? Doc { get; set; }
    public ScriptDoc? OldDoc { get; set; }
    public required ScriptUser User { get; init; }
    public required IScriptDocs Docs { get; init; }
    public required IScriptLog Log { get; init; }
    public required IScriptEmail Email { get; init; }
    public IDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public object? Result { get; set; }
    public ScriptBudget __budget { get; init; } = new ScriptBudget();
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
}

public class ScriptBudgetExceededException(string message) : Exception(message);

public class ScriptSecurityException(string message) : Exception(message);

public class ScriptValidationException : Exception
{
    public string? Field { get; }

    public ScriptValidationException(string message, string? field = null) : base(message)
    {
        Field = field;
    }
}

public class ScriptCompilationException(string message, IReadOnlyList<string> diagnostics) : Exception(message)
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
}
