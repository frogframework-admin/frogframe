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

public sealed class ServerScriptExecutionService : IDisposable
{
    private readonly IServerScriptStore _store;
    private readonly IServiceProvider _services;
    private readonly IMetadataProvider _metadata;
    private readonly IAutomationIdentity _identity;
    private readonly IPermissionService _permissions;
    private readonly IEmailQueue? _emailQueue;
    private readonly ILogger<ServerScriptExecutionService> _logger;
    private DocumentService? _docs;
    private DocumentService Docs => _docs ??= (DocumentService)(_services.GetService(typeof(DocumentService))
        ?? throw new InvalidOperationException("DocumentService is not registered in service provider."));

    private readonly ConcurrentDictionary<string, (CompiledScript Script, int Version, string Code)> _cache = new();
    private bool _disposed;

    public ServerScriptExecutionService(
        IServerScriptStore store,
        IServiceProvider services,
        IMetadataProvider metadata,
        IAutomationIdentity identity,
        IPermissionService permissions,
        IEmailQueue? emailQueue = null,
        ILogger<ServerScriptExecutionService>? logger = null)
    {
        _store = store;
        _services = services;
        _metadata = metadata;
        _identity = identity;
        _permissions = permissions;
        _emailQueue = emailQueue;
        _logger = logger ?? NullLogger<ServerScriptExecutionService>.Instance;
    }

    public async Task RunDocEventAsync(DocHookStage stage, HookContext context, CancellationToken ct = default)
    {
        if (!Enum.TryParse<DocEventStage>(stage.ToString(), out var scriptStage))
            return;

        var scripts = await _store.ListDocEventScriptsAsync(context.Doc.TenantId, context.Meta.Name, scriptStage, ct);
        if (scripts.Count == 0) return;

        foreach (var script in scripts)
        {
            var userContext = context.User;
            var scriptDoc = new ScriptDoc(context.Doc.Data, context.Meta.Name, context.Doc.Name);

            ScriptDoc? oldDoc = null;
            if (context.Doc.Data.TryGetValue("_old_doc", out var oldRaw) && oldRaw is IDictionary<string, object?> oldDict)
            {
                oldDoc = new ScriptDoc(oldDict, context.Meta.Name, context.Doc.Name);
            }

            var scriptLog = new ScriptLog();
            var scriptUser = new ScriptUser
            {
                Id = userContext.Id,
                Email = userContext.Attr("email"),
                Department = userContext.Attr("department"),
                Roles = userContext.Roles.ToList()
            };

            var scriptDocs = new InternalScriptDocs(() => Docs, userContext);
            var scriptEmail = new InternalScriptEmail(_emailQueue, userContext.TenantId);

            var ctx = new ScriptContext
            {
                Doc = scriptDoc,
                OldDoc = oldDoc,
                User = scriptUser,
                Docs = scriptDocs,
                Log = scriptLog,
                Email = scriptEmail,
                CancellationToken = ct
            };

            await ExecuteScriptAsync(script, ctx, "DocEvent", context.Doc.Name, ct);
        }
    }

    public async Task<object?> ExecuteApiAsync(UserContext user, string apiMethod, JsonElement? body, CancellationToken ct = default)
    {
        var script = await _store.GetApiScriptAsync(user.TenantId, apiMethod, ct);
        if (script is null) return null;

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

        var scriptLog = new ScriptLog();
        var scriptUser = new ScriptUser
        {
            Id = user.Id,
            Email = user.Attr("email"),
            Department = user.Attr("department"),
            Roles = user.Roles.ToList()
        };

        var scriptDocs = new InternalScriptDocs(() => Docs, user);
        var scriptEmail = new InternalScriptEmail(_emailQueue, user.TenantId);

        var ctx = new ScriptContext
        {
            User = scriptUser,
            Docs = scriptDocs,
            Log = scriptLog,
            Email = scriptEmail,
            Values = values,
            CancellationToken = ct
        };

        await ExecuteScriptAsync(script, ctx, "Api", null, ct);
        return ctx.Result;
    }

    public async Task ExecuteScheduledAsync(string tenantId, string scriptId, CancellationToken ct = default)
    {
        var script = await _store.GetAsync(tenantId, scriptId, ct);
        if (script is null || !script.IsActive || script.ScriptType != ServerScriptType.Scheduled)
            return;

        var userId = string.IsNullOrWhiteSpace(script.RunAsUserId) ? "Administrator" : script.RunAsUserId;
        var runAsUser = await _identity.ForUserAsync(tenantId, userId, ct);

        var scriptLog = new ScriptLog();
        var scriptUser = new ScriptUser
        {
            Id = runAsUser.Id,
            Email = runAsUser.Attr("email"),
            Department = runAsUser.Attr("department"),
            Roles = runAsUser.Roles.ToList()
        };

        var scriptDocs = new InternalScriptDocs(() => Docs, runAsUser);
        var scriptEmail = new InternalScriptEmail(_emailQueue, tenantId);

        var ctx = new ScriptContext
        {
            User = scriptUser,
            Docs = scriptDocs,
            Log = scriptLog,
            Email = scriptEmail,
            CancellationToken = ct
        };

        await ExecuteScriptAsync(script, ctx, "Scheduled", null, ct);
    }

    public async Task ExecuteScriptAsync(ServerScriptDef script, ScriptContext ctx, string triggerType, string? docName, CancellationToken ct)
    {
        var compiled = GetOrCompile(script);
        var sw = Stopwatch.StartNew();
        var status = "Success";
        string? error = null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5)); // 5-second execution timeout

        try
        {
            var executionCtx = new ScriptContext
            {
                Doc = ctx.Doc,
                OldDoc = ctx.OldDoc,
                User = ctx.User,
                Docs = ctx.Docs,
                Log = ctx.Log,
                Email = ctx.Email,
                Values = ctx.Values,
                Result = ctx.Result,
                __budget = ctx.__budget,
                CancellationToken = cts.Token
            };

            await compiled.Runner(executionCtx);
            ctx.Result = executionCtx.Result;
        }
        catch (ScriptBudgetExceededException ex)
        {
            status = "BudgetExceeded";
            error = ex.Message;
            throw;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            status = "Timeout";
            error = "Script execution timed out after 5,000 ms.";
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
            try
            {
                var run = new ServerScriptRun
                {
                    Id = Guid.NewGuid(),
                    TenantId = script.TenantId,
                    ScriptId = script.Id,
                    Version = script.Version,
                    TriggerType = triggerType,
                    DocName = docName,
                    Status = status,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    Error = error,
                    ExecutedAt = DateTimeOffset.UtcNow
                };
                await _store.RecordRunAsync(run, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record server script run for {ScriptId}", script.Id);
            }
        }
    }

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

    public void Invalidate(string tenantId, string scriptId)
    {
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

    private sealed class InternalScriptDocs(Func<DocumentService> getDocs, UserContext user) : IScriptDocs
    {
        public async Task<ScriptDoc?> GetAsync(string docType, string name, CancellationToken ct = default)
        {
            var doc = await getDocs().GetAsync(user, docType, name, ct);
            return doc is not null ? new ScriptDoc(doc.Data, doc.DocType, doc.Name) : null;
        }

        public async Task<ScriptDoc> InsertAsync(string docType, IDictionary<string, object?> data, CancellationToken ct = default)
        {
            var inserted = await getDocs().InsertAsync(user, docType, data, ct);
            return new ScriptDoc(inserted.Data, inserted.DocType, inserted.Name);
        }

        public async Task<ScriptDoc> UpdateAsync(string docType, string name, IDictionary<string, object?> data, CancellationToken ct = default)
        {
            var docs = getDocs();
            var existing = await docs.GetAsync(user, docType, name, ct)
                ?? throw new KeyNotFoundException($"Document '{docType}/{name}' not found.");
            var updated = await docs.UpdateAsync(user, docType, name, existing.Version, data, ct);
            return new ScriptDoc(updated.Data, updated.DocType, updated.Name);
        }

        public async Task DeleteAsync(string docType, string name, CancellationToken ct = default)
        {
            var docs = getDocs();
            var existing = await docs.GetAsync(user, docType, name, ct)
                ?? throw new KeyNotFoundException($"Document '{docType}/{name}' not found.");
            await docs.DeleteAsync(user, docType, name, existing.Version, ct);
        }

        public async Task<ScriptDoc> RunActionAsync(string docType, string name, string action, string? comment = null, CancellationToken ct = default)
        {
            var docs = getDocs();
            var existing = await docs.GetAsync(user, docType, name, ct)
                ?? throw new KeyNotFoundException($"Document '{docType}/{name}' not found.");
            var res = await docs.RunActionAsync(user, docType, name, action, existing.Version, comment, ct);
            return new ScriptDoc(res.Data, res.DocType, res.Name);
        }
    }

    private sealed class InternalScriptEmail(IEmailQueue? queue, string tenantId) : IScriptEmail
    {
        public async Task SendAsync(string to, string subject, string bodyText, string? bodyHtml = null, CancellationToken ct = default)
        {
            if (queue is null) return;
            var email = new EmailMessage
            {
                To = [to],
                Subject = subject,
                BodyText = bodyText,
                BodyHtml = bodyHtml
            };
            await queue.QueueAsync(tenantId, email, ct: ct);
        }
    }
}
