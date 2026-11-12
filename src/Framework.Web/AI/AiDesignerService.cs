using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Framework.Dashboards;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Framework.Web.AI;

/// <summary>
/// AI designer for DocTypes, workflows and reporting queries, built on Microsoft Agent Framework.
/// <para>
/// Flow per request: the agent (Azure OpenAI through <see cref="IAiChatClientProvider"/>) may call read-only
/// <see cref="AiDesignerTools"/> and must answer with a JSON object of the requested type. The server then validates
/// the answer with the platform compilers (<see cref="AiDraftValidator"/>); errors are sent back for repair, up to
/// <see cref="AiOptions.MaxAttempts"/> attempts. Nothing is saved or published here: the pages show the draft and
/// the administrator publishes it through the normal editors.
/// </para>
/// <para>Only System Managers may use it. Only metadata is sent to the model; document data never is.</para>
/// </summary>
public sealed class AiDesignerService(
    IAiChatClientProvider chatClients,
    IAiSwitch aiSwitch,
    IConfiguration? configuration,
    MetadataRegistry registry,
    RoleCatalog roleCatalog,
    IFieldTypeRegistry fieldTypes,
    AiDraftValidator validator,
    TimeProvider clock,
    ILogger<AiDesignerService>? logger = null)
{
    private const int MaxCatalogChars = 12_000;

    private static readonly JsonSerializerOptions AnswerJson = new(MetadataJson.Options)
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recent = new();

    public bool IsEnabled => aiSwitch.IsEnabled;
    public string? DisabledReason => aiSwitch.DisabledReason;

    public static bool CanUse(UserContext user) => user.Roles.Contains(UserContext.SystemManager);

    // ================================================================== DocType

    /// <param name="existingDocType">Null to design a new DocType; otherwise the DocType to modify.</param>
    /// <param name="module">Module chosen on the page (overrides the model's choice for a new DocType).</param>
    public async Task<AiDocTypeResult> DesignDocTypeAsync(UserContext user, string request, string? existingDocType = null,
        string? module = null, CancellationToken ct = default)
    {
        DocTypeDef? current = null;
        if (existingDocType is not null)
        {
            if (!registry.Current.TryGet(existingDocType, out var meta)) throw new InvalidOperationException($"DocType '{existingDocType}' does not exist.");
            current = meta.Def;
        }

        var message = new StringBuilder();
        if (current is null)
        {
            message.AppendLine("Create a NEW DocType.");
            message.AppendLine(string.IsNullOrWhiteSpace(module)
                ? "Module: choose the best match from list_modules."
                : $"Module: {module}");
        }
        else
        {
            message.AppendLine($"Modify the existing DocType '{current.Name}' (existingName = {current.Name}). Its current definition:");
            message.AppendLine("```json").AppendLine(MetadataJson.Serialize(current)).AppendLine("```");
            message.AppendLine("Return the complete modified DocType. Do not return a workflow (workflow = null) and do not create new child DocTypes (childDocTypes = []); Table fields may only use existing child DocTypes.");
        }
        message.AppendLine().AppendLine("Request:").AppendLine(request);

        var tools = new AiDesignerTools(user, registry, roleCatalog, fieldTypes, validator, null);

        var run = await RunAsync<DocTypeDesignAnswer>(user, "doctype", request, AiDesignerPrompts.DocTypeDesigner, tools.MetadataTools(), message.ToString(),
            answer =>
            {
                Normalize(answer, current, module);
                return Task.FromResult(validator.ValidateDocTypes(user, answer.DocType, answer.ChildDocTypes, answer.Workflow, current?.Name));
            }, ct);

        var a = run.Answer;
        return new AiDocTypeResult(run.Errors.Count == 0 && a is not null, a?.DocType, a?.ChildDocTypes ?? [], a?.Workflow,
            a?.Explanation ?? "", run.Errors, run.Usage);
    }

    private static void Normalize(DocTypeDesignAnswer a, DocTypeDef? current, string? module)
    {
        a.DocType ??= new DocTypeDef();
        a.ChildDocTypes ??= [];
        a.ChildDocTypes.RemoveAll(c => c is null);
        foreach (var d in a.ChildDocTypes.Append(a.DocType)) FillNullLists(d);
        foreach (var c in a.ChildDocTypes) { c.IsChild = true; c.Version = 1; c.Workflow = null; }
        if (a.Workflow is not null) FillNullLists(a.Workflow);

        if (current is null)
        {
            a.DocType.Version = 1;
            a.DocType.Workflow = null; // attached when the workflow is published
            if (!string.IsNullOrWhiteSpace(module) && !a.DocType.IsChild) a.DocType.Module = module;
            if (a.Workflow is not null) { a.Workflow.DocType = a.DocType.Name; a.Workflow.Version = 1; }
        }
        else
        {
            a.DocType.Name = current.Name;
            a.DocType.Version = current.Version + 1;
            a.DocType.Workflow = current.Workflow;
            a.Workflow = null;
            a.ChildDocTypes = []; // the editor publishes one DocType; missing child tables surface as compiler errors

        }
    }

    /// <summary>Model output may contain explicit nulls for lists; the compiler expects empty lists.</summary>
    private static void FillNullLists(DocTypeDef d)
    {
        d.Fields ??= [];
        d.Fields.RemoveAll(f => f is null);
        d.Permissions ??= [];
        d.Rules ??= [];
        d.Hooks ??= [];
        d.Naming ??= new NamingDef();
        d.Document ??= new DocumentDef();
        d.ListView ??= new ListViewDef();
        d.Storage ??= new StorageDef();
        d.Api ??= new ApiDef();
        foreach (var f in d.Fields) f.LinkFilters ??= [];
        foreach (var p in d.Permissions) p.Actions ??= [];
    }

    private static void FillNullLists(WorkflowDef w)
    {
        w.States ??= [];
        w.States.RemoveAll(s => s is null);
        w.Transitions ??= [];
        w.Transitions.RemoveAll(t => t is null);
        foreach (var t in w.Transitions) t.AllowedRoles ??= [];
    }

    // ================================================================== Workflow

    /// <param name="current">The workflow being edited (null for a new one).</param>
    /// <param name="docType">DocType the workflow is for, if known.</param>
    public async Task<AiWorkflowResult> DesignWorkflowAsync(UserContext user, string request, WorkflowDef? current, string? docType,
        CancellationToken ct = default)
    {
        var isNew = current is null || registry.Workflows.All(w => w.Name != current.Name);
        var targetDocType = string.IsNullOrWhiteSpace(current?.DocType) ? docType : current!.DocType;

        var message = new StringBuilder();
        if (isNew)
        {
            message.AppendLine("Create a NEW workflow.");
            if (current is not null) message.AppendLine($"Use the name '{current.Name}'.");
        }
        else
        {
            message.AppendLine($"Modify the existing workflow '{current!.Name}' (keep this name). Its current definition:");
            message.AppendLine("```json").AppendLine(MetadataJson.Serialize(current)).AppendLine("```");
        }
        if (!string.IsNullOrWhiteSpace(targetDocType)) message.AppendLine($"DocType: {targetDocType} (read its fields with get_doctype).");
        message.AppendLine().AppendLine("Request:").AppendLine(request);

        var tools = new AiDesignerTools(user, registry, roleCatalog, fieldTypes, validator, null);

        var run = await RunAsync<WorkflowDesignAnswer>(user, "workflow", request, AiDesignerPrompts.WorkflowDesigner, tools.MetadataTools(), message.ToString(),
            answer =>
            {
                var wf = answer.Workflow ??= new WorkflowDef();
                FillNullLists(wf);
                if (current is not null) wf.Name = current.Name;
                wf.Version = isNew ? 1 : registry.Workflows.First(w => w.Name == current!.Name).Version + 1;
                wf.DocType = string.IsNullOrWhiteSpace(targetDocType) ? (string.IsNullOrWhiteSpace(wf.DocType) ? null : wf.DocType) : targetDocType;
                return Task.FromResult(validator.ValidateWorkflow(user, wf, isNew));
            }, ct);

        var a = run.Answer;
        return new AiWorkflowResult(run.Errors.Count == 0 && a is not null, a?.Workflow, a?.Explanation ?? "", run.Errors, run.Usage);
    }

    // ================================================================== Query

    public async Task<AiQueryResult> DesignQueryAsync(UserContext user, QueryPurpose purpose, string request, string? currentSql = null,
        string? currentHeaderSql = null, CancellationToken ct = default)
    {
        var message = new StringBuilder();
        message.AppendLine(purpose switch
        {
            QueryPurpose.Widget => "Purpose: dashboard widget (also return visual).",
            QueryPurpose.QueryApi => "Purpose: saved query API (JSON/CSV endpoint). visual = null. Bindings allowed: default, user:id, user:department.",
            _ => "Purpose: report template (Word/Excel). visual = null. Bindings allowed: default, user:id, user:department.",
        });
        if (!string.IsNullOrWhiteSpace(currentSql))
        {
            message.AppendLine("Current query (modify it if the request asks for a change):").AppendLine("```sql").AppendLine(currentSql).AppendLine("```");
            if (!string.IsNullOrWhiteSpace(currentHeaderSql))
                message.AppendLine("Current header query:").AppendLine("```sql").AppendLine(currentHeaderSql).AppendLine("```");
        }

        var catalog = ReportingCatalogText();
        if (catalog is not null) message.AppendLine("Reporting views (name: column type, ...):").AppendLine(catalog);
        else message.AppendLine("Call list_reporting_views to see the available views.");

        message.AppendLine().AppendLine("Request:").AppendLine(request);

        var tools = new AiDesignerTools(user, registry, roleCatalog, fieldTypes, validator, purpose);
        AiDraftValidator.QueryCheck? lastCheck = null;

        var run = await RunAsync<QueryDesignAnswer>(user, $"query:{purpose}", request, AiDesignerPrompts.QueryDesigner, tools.QueryTools(), message.ToString(),
            async answer =>
            {
                answer.Sql = CleanSql(answer.Sql);
                answer.HeaderSql = purpose == QueryPurpose.ReportTemplate && !string.IsNullOrWhiteSpace(answer.HeaderSql) ? CleanSql(answer.HeaderSql) : null;
                if (purpose != QueryPurpose.Widget) answer.Visual = null;
                answer.Parameters ??= [];
                foreach (var p in answer.Parameters) p.Name = p.Name.TrimStart('@').Trim();

                lastCheck = await validator.ValidateQueryAsync(user, purpose, answer.Sql, answer.HeaderSql, answer.Parameters, answer.Visual, ct);
                return lastCheck.Errors;
            }, ct);

        var a = run.Answer;
        return new AiQueryResult(run.Errors.Count == 0 && a is not null, a?.Sql ?? "", a?.HeaderSql,
            lastCheck?.Parameters ?? a?.Parameters ?? [], a?.Visual, lastCheck?.Columns ?? [], a?.Explanation ?? "", run.Errors, run.Usage);
    }

    private static string CleanSql(string? sql)
    {
        var s = (sql ?? "").Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
            s = string.Join('\n', s.Split('\n').Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))).Trim();
        return s.TrimEnd(';').TrimEnd();
    }

    private string? ReportingCatalogText()
    {
        var sb = new StringBuilder();
        foreach (var v in ReportingCatalog.Describe(registry.Current))
        {
            sb.Append(v.Name).Append(": ").AppendJoin(", ", v.Columns.Select(c => $"{c.Column} {c.Type}")).AppendLine();
            if (sb.Length > MaxCatalogChars) return null; // too large: the model uses the tool instead
        }
        return sb.ToString();
    }

    // ================================================================== agent loop

    private sealed record RunOutcome<T>(T? Answer, List<string> Errors, AiUsage Usage);

    private async Task<RunOutcome<TAnswer>> RunAsync<TAnswer>(UserContext user, string kind, string request, string instructions,
        IList<AITool> tools, string userMessage, Func<TAnswer, Task<List<string>>> validate, CancellationToken ct) where TAnswer : class
    {
        if (!CanUse(user)) throw new PermissionDeniedException("The AI designer is restricted to System Managers.");
        var options = AiOptions.From(configuration);
        if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("Describe what you want to build.");
        if (request.Length > options.MaxPromptChars) throw new ArgumentException($"The description is too long (maximum {options.MaxPromptChars} characters).");
        CheckRateLimit(user, options);

        var chatClient = chatClients.GetChatClient(); // throws AiUnavailableException when switched off
        AIAgent agent = chatClient.AsAIAgent(instructions: instructions, name: "FrogFrameDesigner", tools: tools);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 600)));

        var messages = new List<ChatMessage> { new(ChatRole.User, userMessage) };
        var stopwatch = Stopwatch.StartNew();
        long inputTokens = 0, outputTokens = 0;
        TAnswer? last = null;
        List<string> errors = ["The AI did not return an answer."];
        var attempts = 0;
        var maxAttempts = Math.Clamp(options.MaxAttempts, 1, 5);

        try
        {
            while (attempts < maxAttempts)
            {
                attempts++;
                var response = await agent.RunAsync<TAnswer>(messages, serializerOptions: AnswerJson, cancellationToken: timeout.Token);
                inputTokens += response.Usage?.InputTokenCount ?? 0;
                outputTokens += response.Usage?.OutputTokenCount ?? 0;

                TAnswer? answer = null;
                try { answer = response.Result; }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
                {
                    errors = [$"The answer was not valid JSON for the requested format: {ex.Message}"];
                }

                if (answer is not null)
                {
                    last = answer;
                    errors = await validate(answer);
                }
                if (errors.Count == 0) break;

                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
                messages.Add(new ChatMessage(ChatRole.User,
                    "Server validation found these errors. Fix all of them and answer again with the complete draft:\n- "
                    + string.Join("\n- ", errors.Take(30))));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            errors = [$"The AI did not finish within {options.TimeoutSeconds} seconds. Try a smaller request."];
        }
        catch (AiUnavailableException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException and not PermissionDeniedException)
        {
            logger?.LogWarning(ex, "AI designer request failed ({Kind})", kind);
            errors = [$"The AI service returned an error: {ex.Message}"];
        }

        var usage = new AiUsage(attempts, inputTokens == 0 ? null : inputTokens, outputTokens == 0 ? null : outputTokens, stopwatch.Elapsed);
        logger?.LogInformation(
            "AI designer {Kind} by {UserId} (tenant {TenantId}): ok={Ok} attempts={Attempts} inputTokens={InputTokens} outputTokens={OutputTokens} ms={Elapsed} request={Request}",
            kind, user.Id, user.TenantId, errors.Count == 0, attempts, usage.InputTokens, usage.OutputTokens, (long)usage.Elapsed.TotalMilliseconds,
            Truncate(request, 500));

        return new RunOutcome<TAnswer>(last, errors, usage);
    }

    private void CheckRateLimit(UserContext user, AiOptions options)
    {
        var limit = Math.Max(1, options.RequestsPerUserPerHour);
        var now = clock.GetUtcNow();
        var queue = _recent.GetOrAdd($"{user.TenantId}|{user.Id}", _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1)) queue.Dequeue();
            if (queue.Count >= limit) throw new InvalidOperationException($"AI request limit reached ({limit} per hour). Try again later.");
            queue.Enqueue(now);
        }
    }

    private static string Truncate(string s, int max) =>
        (s.Length <= max ? s : s[..max] + "…").Replace('\r', ' ').Replace('\n', ' ');
}
