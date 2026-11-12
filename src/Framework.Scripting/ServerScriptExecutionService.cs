using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Framework.Documents;
using Framework.Email;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Security;
using Framework.Scripting.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Scripting;

/// <summary>Kill switch for all server scripts. The web host reads it from configuration (<c>Scripting:Enabled</c>).</summary>
public interface IScriptingSwitch
{
    bool IsEnabled { get; }
}

/// <summary>Result of calling an Api script. <see cref="Found"/> is false when no active script has that name.</summary>
public sealed record ScriptApiResult(bool Found, object? Data);

public sealed class ServerScriptExecutionService : IDisposable
{
    private sealed record Limits(TimeSpan Timeout, int MaxStatements);

    private static readonly Limits DocEventLimits = new(TimeSpan.FromSeconds(5), ScriptBudget.DefaultMaxStatements);
    private static readonly Limits ApiLimits = new(TimeSpan.FromSeconds(10), ScriptBudget.DefaultMaxStatements);
    private static readonly Limits ScheduledLimits = new(TimeSpan.FromSeconds(60), 500_000);
    /// <summary>Client Scripts run while the user waits on every load, change and save: keep them short.</summary>
    private static readonly Limits FormLimits = new(TimeSpan.FromSeconds(1), 20_000);
    private static readonly TimeSpan FormScriptListCache = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FormFailureRecordInterval = TimeSpan.FromSeconds(60);
    private const int MaxChangedValues = 200;

    /// <summary>Extra time given to a script after its deadline before the caller stops waiting for it.</summary>
    private static readonly TimeSpan HardTimeoutGrace = TimeSpan.FromSeconds(1);

    private readonly IServerScriptStore _store;
    private readonly IServiceProvider _services;
    private readonly IMetadataProvider _metadata;
    private readonly IAutomationIdentity _identity;
    private readonly IPermissionService _permissions;
    private readonly IEmailQueue? _emailQueue;
    private readonly IScriptingSwitch? _switch;
    private readonly ILogger<ServerScriptExecutionService> _logger;
    private DocumentService? _docs;
    private DocumentService Docs => _docs ??= (DocumentService)(_services.GetService(typeof(DocumentService))
        ?? throw new InvalidOperationException("DocumentService is not registered in service provider."));

    private readonly ConcurrentDictionary<string, (CompiledScript Script, int Version, string Code)> _cache = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset LoadedAt, IReadOnlyList<ServerScriptDef> Scripts)> _formScripts = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFormFailureRecorded = new();
    private bool _disposed;

    public ServerScriptExecutionService(
        IServerScriptStore store,
        IServiceProvider services,
        IMetadataProvider metadata,
        IAutomationIdentity identity,
        IPermissionService permissions,
        IEmailQueue? emailQueue = null,
        ILogger<ServerScriptExecutionService>? logger = null,
        IScriptingSwitch? scriptingSwitch = null,
        Framework.Cluster.IClusterBus? cluster = null)
    {
        _store = store;
        _services = services;
        _metadata = metadata;
        _identity = identity;
        _permissions = permissions;
        _emailQueue = emailQueue;
        _switch = scriptingSwitch;
        _logger = logger ?? NullLogger<ServerScriptExecutionService>.Instance;
        _cluster = cluster;
        // Multi-node: a script saved on another node clears the Client Script list and the compiled copy here at once.
        cluster?.Subscribe(Framework.Cluster.ClusterTopics.Scripts, payload =>
        {
            var sep = payload.IndexOf(':');
            if (sep > 0) InvalidateLocal(payload[..sep], payload[(sep + 1)..]);
        });
        if (cluster is not null) cluster.Reconnected += () => _formScripts.Clear();
    }

    private readonly Framework.Cluster.IClusterBus? _cluster;

    /// <summary>False when server scripts are switched off (<c>Scripting:Enabled = false</c>).</summary>
    public bool IsEnabled => _switch?.IsEnabled ?? true;

    /// <summary>Api scripts: administrators always; otherwise the user needs one of the script's allowed roles.</summary>
    public static bool CanInvoke(ServerScriptDef script, UserContext user) =>
        user.IsAdmin || script.AllowedRoles.Any(role => user.Roles.Contains(role));

    // ---------------------------------------------------------------- entry points

    public async Task RunDocEventAsync(DocHookStage stage, HookContext context, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        if (!Enum.TryParse<DocEventStage>(stage.ToString(), out var scriptStage))
            return;

        var scripts = await _store.ListDocEventScriptsAsync(context.Doc.TenantId, context.Meta.Name, scriptStage, ct);
        if (scripts.Count == 0) return;

        foreach (var script in scripts)
        {
            // Snapshot before the script runs, to verify afterwards that it only changed fields the user may write.
            var before = CopyData(context.Doc.Data);

            IDictionary<string, object?>? previousData = context.Previous?.Data;
            if (previousData is null && context.Doc.Data.TryGetValue("_old_doc", out var oldRaw) && oldRaw is IDictionary<string, object?> oldDict)
                previousData = oldDict;

            var invocation = new Invocation(
                context.User,
                new ScriptDoc(context.Doc.Data, context.Meta.Name, context.Doc.Name),
                // OldDoc is a copy: writes to it must never reach the stored previous version.
                previousData is null ? null : new ScriptDoc(CopyData(previousData), context.Meta.Name, context.Doc.Name),
                Values: null);

            await ExecuteScriptAsync(script, invocation, ServerScriptType.DocEvent, context.Doc.Name, ct);
            EnsureOnlyWritableFieldsChanged(script, context, before);
        }
    }

    public async Task<ScriptApiResult> ExecuteApiAsync(UserContext user, string apiMethod, JsonElement? body, CancellationToken ct = default)
    {
        if (!IsEnabled) return new ScriptApiResult(false, null);

        var script = await _store.GetApiScriptAsync(user.TenantId, apiMethod, ct);
        if (script is null) return new ScriptApiResult(false, null);

        if (!CanInvoke(script, user))
            throw new PermissionDeniedException($"You are not allowed to call API method '{apiMethod}'.");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (body is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                values[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText()
                };
            }
        }

        var result = await ExecuteScriptAsync(script, new Invocation(user, null, null, values), ServerScriptType.Api, null, ct);
        return new ScriptApiResult(true, result);
    }

    public async Task ExecuteScheduledAsync(string tenantId, string scriptId, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var script = await _store.GetAsync(tenantId, scriptId, ct);
        if (script is null || !script.IsActive || script.ScriptType != ServerScriptType.Scheduled)
            return;

        // No implicit administrator: a scheduled script runs only as an explicitly chosen, active user.
        if (string.IsNullOrWhiteSpace(script.RunAsUserId))
        {
            const string error = "Scheduled script has no Run As user. Set Run As User before it can run.";
            await RecordRunSafeAsync(script, "Scheduled", null, "Failed", error, 0);
            throw new InvalidOperationException($"Scheduled script '{script.Name}': {error}");
        }

        var runAsUser = await _identity.ForUserAsync(tenantId, script.RunAsUserId, ct);
        await ExecuteScriptAsync(script, new Invocation(runAsUser, null, null, null), ServerScriptType.Scheduled, null, ct);
    }

    // ---------------------------------------------------------------- client scripts (forms)

    /// <summary>
    /// Runs the active Client Scripts of a DocType for one form event, in name order, as <paramref name="user"/>.
    /// Scripts work on a copy of the form data; the outcome lists changed values and UI changes for the form to apply.
    /// A failing script does not stop the others; its error is returned in <see cref="FormScriptOutcome.Failures"/>.
    /// </summary>
    public async Task<FormScriptOutcome> RunFormEventAsync(UserContext user, string docType, FormScriptEvent formEvent, string? changedField,
        IDictionary<string, object?> data, bool isNew, string? docName = null, CancellationToken ct = default)
    {
        if (!IsEnabled) return FormScriptOutcome.Empty;
        var scripts = (await ListFormScriptsCachedAsync(user.TenantId, docType, ct))
            .Where(s => s.FormEvent == formEvent
                        && (formEvent != FormScriptEvent.Change || string.IsNullOrEmpty(s.FormField) || s.FormField == "*"
                            || string.Equals(s.FormField, changedField, StringComparison.Ordinal)))
            .ToList();
        if (scripts.Count == 0) return FormScriptOutcome.Empty;

        var working = CopyData(data);
        var form = new ScriptForm(formEvent, changedField, isNew, working);
        var failures = new List<FormScriptFailure>();
        var validationErrors = new List<FieldError>();

        foreach (var script in scripts)
        {
            try
            {
                var doc = new ScriptDoc(working, docType, docName ?? "");
                await ExecuteScriptAsync(script, new Invocation(user, doc, null, null, form), ServerScriptType.Form, docName, ct);
            }
            catch (ValidationFailedException ex)
            {
                // ScriptValidationException in a client script = a field error in the form (mainly for Validate).
                validationErrors.AddRange(ex.Errors);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures.Add(new FormScriptFailure(script.Name, ex.Message));
                _logger.LogWarning("Client script {ScriptId} ({Name}) failed on {Event}: {Error}", script.Id, script.Name, formEvent, ex.Message);
            }
        }

        // Only values the user could type in this form are passed back: an existing, writable, non-derived field that
        // is not a child table, normalized by its field type. Everything else is reported as a failure and dropped.
        var changed = new Dictionary<string, object?>(StringComparer.Ordinal);
        _metadata.Current.TryGet(docType, out var meta);
        foreach (var (key, value) in working)
        {
            if (data.TryGetValue(key, out var before) && ValueEquals(before, value)) continue;
            if (changed.Count >= MaxChangedValues) break;
            if (meta is null || StandardFields.All.Contains(key) || !meta.ByName.TryGetValue(key, out var field)
                || field.IsDerived || field.Def.Type == "Table" || !_permissions.CanWriteField(user, meta, field))
            {
                failures.Add(new FormScriptFailure("form", $"Value of '{key}' cannot be set from a client script; ignored."));
                continue;
            }
            var normalized = field.Type.Normalize(value);
            if (!normalized.Ok)
            {
                failures.Add(new FormScriptFailure("form", $"Value for '{key}' is not valid: {normalized.Error}"));
                continue;
            }
            changed[key] = normalized.Value;
        }

        validationErrors.AddRange(form.Errors.Select(e => new FieldError(e.Field, e.Message)));
        return new FormScriptOutcome(changed, form.HiddenFields, form.RequiredFields, form.ReadOnlyFields, form.FieldOptions,
            form.Descriptions, form.Messages, validationErrors, failures);
    }

    private async Task<IReadOnlyList<ServerScriptDef>> ListFormScriptsCachedAsync(string tenantId, string docType, CancellationToken ct)
    {
        var key = $"{tenantId}:{docType}";
        if (_formScripts.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < FormScriptListCache)
            return cached.Scripts;
        var scripts = await _store.ListFormScriptsAsync(tenantId, docType, ct);
        _formScripts[key] = (DateTimeOffset.UtcNow, scripts);
        return scripts;
    }

    // ---------------------------------------------------------------- execution

    private sealed record Invocation(
        UserContext User,
        ScriptDoc? Doc,
        ScriptDoc? OldDoc,
        IDictionary<string, object?>? Values,
        IScriptForm? Form = null);

    private async Task<object?> ExecuteScriptAsync(
        ServerScriptDef script, Invocation invocation, ServerScriptType trigger, string? docName, CancellationToken ct)
    {
        var limits = trigger switch
        {
            ServerScriptType.Api => ApiLimits,
            ServerScriptType.Scheduled => ScheduledLimits,
            ServerScriptType.Form => FormLimits,
            _ => DocEventLimits,
        };

        var sw = Stopwatch.StartNew();
        var status = "Success";
        string? error = null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limits.Timeout);

        try
        {
            var compiled = GetOrCompile(script);
            var user = invocation.User;
            var ctx = new ScriptContext
            {
                Doc = invocation.Doc,
                OldDoc = invocation.OldDoc,
                User = new ScriptUser
                {
                    Id = user.Id,
                    Email = user.Attr("email"),
                    Department = user.Attr("department"),
                    Roles = user.Roles.ToList()
                },
                // Client Scripts run on every load/change/save: they may read documents but not write them or send email.
                Docs = trigger == ServerScriptType.Form
                    ? new ReadOnlyScriptDocs(new InternalScriptDocs(() => Docs, user, cts.Token))
                    : new InternalScriptDocs(() => Docs, user, cts.Token),
                Log = new ScriptLog(),
                Email = trigger == ServerScriptType.Form
                    ? new UnavailableScriptEmail()
                    : new InternalScriptEmail(_emailQueue, user.TenantId, cts.Token),
                Form = invocation.Form,
                Values = invocation.Values ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                __budget = new ScriptBudget(limits.MaxStatements, limits.Timeout, cts.Token),
                CancellationToken = cts.Token
            };

            // The budget stops the script at the next tick after the deadline. Code that never ticks (a long-running
            // call into the BCL) is not interruptible in-process, so the caller stops waiting shortly after the deadline.
            var run = Task.Run(() => compiled.Runner(ctx), CancellationToken.None);
            try
            {
                await run.WaitAsync(limits.Timeout + HardTimeoutGrace, ct);
            }
            catch (TimeoutException)
            {
                cts.Cancel();
                throw new TimeoutException($"Script execution timed out after {limits.Timeout.TotalMilliseconds:N0} ms.");
            }

            return ctx.Result;
        }
        catch (ScriptBudgetExceededException ex)
        {
            status = "BudgetExceeded";
            error = ex.Message;
            throw;
        }
        catch (TimeoutException ex)
        {
            status = "Timeout";
            error = ex.Message;
            throw;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            status = "Timeout";
            error = $"Script execution timed out after {limits.Timeout.TotalMilliseconds:N0} ms.";
            throw new TimeoutException(error);
        }
        catch (ScriptSecurityException ex)
        {
            status = "SecurityViolation";
            error = ex.Message;
            throw;
        }
        catch (ScriptValidationException ex)
        {
            status = "Failed";
            error = ex.Message;
            throw new ValidationFailedException([new FieldError(ex.Field ?? "", ex.Message)]);
        }
        catch (ValidationFailedException)
        {
            status = "Failed";
            error = "Validation rule failed.";
            throw;
        }
        catch (Exception ex)
        {
            status = "Failed";
            error = ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            await RecordRunSafeAsync(script, trigger.ToString(), docName, status, error, (int)sw.ElapsedMilliseconds);
        }
    }

    private async Task RecordRunSafeAsync(ServerScriptDef script, string trigger, string? docName, string status, string? error, int durationMs)
    {
        // Client Scripts run on every change: record only failures, at most once a minute per script.
        if (trigger == nameof(ServerScriptType.Form))
        {
            if (status == "Success") return;
            var key = $"{script.TenantId}:{script.Id}";
            var now = DateTimeOffset.UtcNow;
            if (_lastFormFailureRecorded.TryGetValue(key, out var last) && now - last < FormFailureRecordInterval) return;
            _lastFormFailureRecorded[key] = now;
        }
        try
        {
            await _store.RecordRunAsync(new ServerScriptRun
            {
                Id = Guid.NewGuid(),
                TenantId = script.TenantId,
                ScriptId = script.Id,
                Version = script.Version,
                TriggerType = trigger,
                DocName = docName,
                Status = status,
                DurationMs = durationMs,
                Error = error,
                ExecutedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record server script run for {ScriptId}", script.Id);
        }
    }

    // ---------------------------------------------------------------- field-level permission check

    /// <summary>
    /// A DocEvent script runs as the user who triggered the event, so it may only change fields that user may
    /// write (field PermLevel). The check compares the document before and after the script, so it covers every
    /// way of writing: <c>Doc["x"] = ...</c>, <c>Doc.Data["x"] = ...</c> and edits to child-table rows
    /// (a child row change counts as a change to the table field).
    /// </summary>
    private void EnsureOnlyWritableFieldsChanged(ServerScriptDef script, HookContext context, Dictionary<string, object?> before)
    {
        var current = context.Doc.Data;
        var forbidden = new List<string>();

        foreach (var key in before.Keys.Union(current.Keys, StringComparer.Ordinal))
        {
            var hadBefore = before.TryGetValue(key, out var oldValue);
            var hasNow = current.TryGetValue(key, out var newValue);
            if (hadBefore == hasNow && ValueEquals(oldValue, newValue)) continue;

            if (StandardFields.All.Contains(key)
                || !context.Meta.ByName.TryGetValue(key, out var field)
                || !_permissions.CanWriteField(context.User, context.Meta, field))
            {
                forbidden.Add(key);
            }
        }

        if (forbidden.Count == 0) return;

        // Undo the script's changes before failing, so no caller can persist them by catching the exception.
        current.Clear();
        foreach (var (key, value) in before) current[key] = value;

        throw new PermissionDeniedException(
            $"Server script '{script.Name}' tried to modify field(s) the current user is not allowed to change: {string.Join(", ", forbidden)}");
    }

    private static Dictionary<string, object?> CopyData(IDictionary<string, object?> source)
    {
        var comparer = source is Dictionary<string, object?> d ? d.Comparer : StringComparer.Ordinal;
        var copy = new Dictionary<string, object?>(comparer);
        foreach (var (key, value) in source) copy[key] = CopyValue(value);
        return copy;
    }

    private static object? CopyValue(object? value) => value switch
    {
        IDictionary<string, object?> dict => CopyData(dict),
        List<Dictionary<string, object?>> rows => rows.Select(CopyData).ToList(),
        IEnumerable<IDictionary<string, object?>> rows => rows.Select(r => (object?)CopyData(r)).ToList(),
        _ => value
    };

    private static bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (Equals(a, b)) return true;
        try
        {
            return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- compiled script cache

    private CompiledScript GetOrCompile(ServerScriptDef def)
    {
        var key = $"{def.TenantId}:{def.Id}";

        if (_cache.TryGetValue(key, out var cached))
        {
            if (cached.Version == def.Version && cached.Code == def.CurrentCode)
            {
                return cached.Script;
            }

            // Invalidate and unload previous ALC
            _cache.TryRemove(key, out _);
            cached.Script.Dispose();
        }

        var compiled = ScriptCompiler.Compile(def.Id, def.Version, def.CurrentCode);
        _cache[key] = (compiled, def.Version, def.CurrentCode);
        return compiled;
    }

    /// <summary>Clears the cached script here and on the other nodes (call after saving or deleting a script).</summary>
    public void Invalidate(string tenantId, string scriptId)
    {
        InvalidateLocal(tenantId, scriptId);
        _cluster?.Publish(Framework.Cluster.ClusterTopics.Scripts, $"{tenantId}:{scriptId}");
    }

    private void InvalidateLocal(string tenantId, string scriptId)
    {
        foreach (var formKey in _formScripts.Keys.Where(k => k.StartsWith(tenantId + ":", StringComparison.Ordinal)).ToList())
            _formScripts.TryRemove(formKey, out _);
        var key = $"{tenantId}:{scriptId}";
        if (_cache.TryRemove(key, out var cached))
        {
            cached.Script.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var entry in _cache.Values)
        {
            entry.Script.Dispose();
        }
        _cache.Clear();
    }

    // ---------------------------------------------------------------- facades exposed to scripts

    /// <summary>Document access for scripts. Always runs as the script's user, so permissions and row scope apply.</summary>
    private sealed class InternalScriptDocs(Func<DocumentService> getDocs, UserContext user, CancellationToken executionToken) : IScriptDocs
    {
        public async Task<ScriptDoc?> GetAsync(string docType, string name, CancellationToken ct = default)
        {
            using var linked = Link(ct);
            var doc = await getDocs().GetAsync(user, docType, name, linked.Token);
            return doc is not null ? new ScriptDoc(doc.Data, doc.DocType, doc.Name) : null;
        }

        public async Task<ScriptDoc> InsertAsync(string docType, IDictionary<string, object?> data, CancellationToken ct = default)
        {
            using var linked = Link(ct);
            var inserted = await getDocs().InsertAsync(user, docType, data, linked.Token);
            return new ScriptDoc(inserted.Data, inserted.DocType, inserted.Name);
        }

        public async Task<ScriptDoc> UpdateAsync(string docType, string name, IDictionary<string, object?> data, CancellationToken ct = default)
        {
            using var linked = Link(ct);
            var docs = getDocs();
            var existing = await docs.GetAsync(user, docType, name, linked.Token)
                ?? throw new KeyNotFoundException($"Document '{docType}/{name}' not found.");
            var updated = await docs.UpdateAsync(user, docType, name, existing.Version, data, linked.Token);
            return new ScriptDoc(updated.Data, updated.DocType, updated.Name);
        }

        public async Task DeleteAsync(string docType, string name, CancellationToken ct = default)
        {
            using var linked = Link(ct);
            var docs = getDocs();
            var existing = await docs.GetAsync(user, docType, name, linked.Token)
                ?? throw new KeyNotFoundException($"Document '{docType}/{name}' not found.");
            await docs.DeleteAsync(user, docType, name, existing.Version, linked.Token);
        }

        public async Task<ScriptDoc> RunActionAsync(string docType, string name, string action, string? comment = null, CancellationToken ct = default)
        {
            using var linked = Link(ct);
            var docs = getDocs();
            var existing = await docs.GetAsync(user, docType, name, linked.Token)
                ?? throw new KeyNotFoundException($"Document '{docType}/{name}' not found.");
            var res = await docs.RunActionAsync(user, docType, name, action, existing.Version, comment, linked.Token);
            return new ScriptDoc(res.Data, res.DocType, res.Name);
        }

        /// <summary>Stops a script that keeps calling the facade after its deadline.</summary>
        private CancellationTokenSource Link(CancellationToken ct)
        {
            executionToken.ThrowIfCancellationRequested();
            return CancellationTokenSource.CreateLinkedTokenSource(ct, executionToken);
        }
    }

    /// <summary>Documents facade for Client Scripts: reading is allowed, changing documents is not.</summary>
    private sealed class ReadOnlyScriptDocs(IScriptDocs inner) : IScriptDocs
    {
        private const string Message = "Client scripts can read documents but not change them. Use a server script (DocEvent) instead.";
        public Task<ScriptDoc?> GetAsync(string docType, string name, CancellationToken ct = default) => inner.GetAsync(docType, name, ct);
        public Task<ScriptDoc> InsertAsync(string docType, IDictionary<string, object?> data, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<ScriptDoc> UpdateAsync(string docType, string name, IDictionary<string, object?> data, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task DeleteAsync(string docType, string name, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<ScriptDoc> RunActionAsync(string docType, string name, string action, string? comment = null, CancellationToken ct = default) => throw new InvalidOperationException(Message);
    }

    private sealed class UnavailableScriptEmail : IScriptEmail
    {
        public Task SendAsync(string to, string subject, string bodyText, string? bodyHtml = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Client scripts cannot send email. Use a server script (DocEvent) instead.");
    }

    private sealed class InternalScriptEmail(IEmailQueue? queue, string tenantId, CancellationToken executionToken) : IScriptEmail
    {
        public async Task SendAsync(string to, string subject, string bodyText, string? bodyHtml = null, CancellationToken ct = default)
        {
            executionToken.ThrowIfCancellationRequested();
            if (queue is null) return;
            var email = new EmailMessage
            {
                To = [to],
                Subject = subject,
                BodyText = bodyText,
                BodyHtml = bodyHtml ?? ""
            };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, executionToken);
            await queue.QueueAsync(tenantId, email, ct: linked.Token);
        }
    }
}

public sealed record FormScriptFailure(string Script, string Message);

/// <summary>What the Client Scripts of one form event changed. Empty when no script ran.</summary>
public sealed record FormScriptOutcome(
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyDictionary<string, bool> Hidden,
    IReadOnlyDictionary<string, bool> Required,
    IReadOnlyDictionary<string, bool> ReadOnly,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Options,
    IReadOnlyDictionary<string, string?> Descriptions,
    IReadOnlyList<FormScriptMessage> Messages,
    IReadOnlyList<FieldError> Errors,
    IReadOnlyList<FormScriptFailure> Failures)
{
    public static readonly FormScriptOutcome Empty = new(
        new Dictionary<string, object?>(), new Dictionary<string, bool>(), new Dictionary<string, bool>(), new Dictionary<string, bool>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, string?>(), [], [], []);
}
