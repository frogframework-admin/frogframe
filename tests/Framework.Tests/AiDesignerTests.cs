using System.Text.Json;
using Framework.Dashboards;
using Framework.Metadata;
using Framework.Security;
using Framework.Web.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// AI designer: the agent loop, server-side validation and repair, safety rules. Uses a scripted fake
/// <see cref="IChatClient"/>, so no Azure OpenAI call is made.
/// </summary>
sealed partial class Tests
{
    private sealed class ScriptedChatClient(params string[] replies) : IChatClient
    {
        private readonly Queue<string> _replies = new(replies);
        public List<List<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            var text = _replies.Count > 0 ? _replies.Dequeue() : "{}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixedChatClientProvider(IChatClient client) : IAiChatClientProvider
    {
        public IChatClient GetChatClient() => client;
    }

    private sealed class AiOn : IAiSwitch
    {
        public bool IsEnabled => true;
        public string? DisabledReason => null;
    }

    private AiDesignerService AiWith(IChatClient client, int requestsPerHour = 30) => new(
        new FixedChatClientProvider(client),
        new AiOn(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:MaxAttempts"] = "3",
            ["AI:RequestsPerUserPerHour"] = requestsPerHour.ToString(),
        }).Build(),
        sp.GetRequiredService<MetadataRegistry>(),
        sp.GetRequiredService<RoleCatalog>(),
        sp.GetRequiredService<IFieldTypeRegistry>(),
        sp.GetRequiredService<AiDraftValidator>(),
        TimeProvider.System);

    private static string AnswerJson<T>(T answer) => JsonSerializer.Serialize(answer, MetadataJson.Options);

    private static DocTypeDef AiLeaveRequest(string titleFieldType) => new()
    {
        Name = "AiLeaveRequest",
        Title = "AI Leave Request",
        Module = "HR",
        Naming = new NamingDef { Pattern = "ALR-{YYYY}-{#####}" },
        Document = new DocumentDef { AllowAmend = false, AllowDelete = true },
        Fields =
        [
            new FieldDef { Name = "title", Type = titleFieldType, Label = "Title", RequiredWhen = "true", InListView = true },
            new FieldDef { Name = "start_date", Type = "Date", Label = "Start Date", RequiredWhen = "true" },
            new FieldDef { Name = "end_date", Type = "Date", Label = "End Date", RequiredWhen = "true" },
        ],
        Permissions = [new PermissionDef { Role = "Employee", Actions = ["read", "create", "write", "delete"], Scope = "owner" }],
        ListView = new ListViewDef { TitleField = "title", SearchFields = ["name", "title"] },
    };

    [Test]
    public async Task AiDesigner_is_off_by_default_and_restricted_to_system_managers()
    {
        var ai = sp.GetRequiredService<AiDesignerService>();
        Assert.True(!ai.IsEnabled, "AI is off without AI:Enabled");
        await Assert.Throws<AiUnavailableException>(() => ai.DesignQueryAsync(U("admin"), QueryPurpose.Widget, "anything"), "no call when switched off");

        var withFake = AiWith(new ScriptedChatClient());
        await Assert.Throws<PermissionDeniedException>(() => withFake.DesignQueryAsync(U("andi"), QueryPurpose.Widget, "anything"), "non System Manager rejected");
    }

    [Test]
    public async Task AiDesigner_doctype_draft_is_repaired_with_compiler_errors_and_not_published()
    {
        var admin = U("admin");
        Assert.Equal("acme", admin.TenantId, "test assumes the acme tenant (modules HR, roles Employee)");

        var bad = new DocTypeDesignAnswer { Explanation = "first try", DocType = AiLeaveRequest("Strng") };
        var good = new DocTypeDesignAnswer { Explanation = "Leave request with dates.", DocType = AiLeaveRequest("Data") };
        var chat = new ScriptedChatClient(AnswerJson(bad), AnswerJson(good));

        var result = await AiWith(chat).DesignDocTypeAsync(admin, "Leave request with start and end date", module: "HR");

        Assert.True(result.Ok, "second answer valid: " + string.Join(" | ", result.Errors));
        Assert.Equal(2, result.Usage.Attempts, "one repair round");
        Assert.Equal("Data", result.DocType!.Fields[0].Type, "repaired draft returned");
        var repairPrompt = chat.Calls[1].Last(m => m.Role == ChatRole.User).Text;
        Assert.True(repairPrompt.Contains("Server validation found these errors") && repairPrompt.Contains("Strng"), "compiler errors sent back to the model");
        Assert.True(!sp.GetRequiredService<MetadataRegistry>().Current.TryGet("AiLeaveRequest", out _), "nothing is published by the AI designer");
    }

    [Test]
    public async Task AiDesigner_doctype_returns_last_draft_with_errors_after_max_attempts()
    {
        var bad = AnswerJson(new DocTypeDesignAnswer { DocType = AiLeaveRequest("Strng") });
        var chat = new ScriptedChatClient(bad, bad, bad);

        var result = await AiWith(chat).DesignDocTypeAsync(U("admin"), "Leave request", module: "HR");

        Assert.True(!result.Ok, "still invalid");
        Assert.Equal(3, result.Usage.Attempts, "stops after MaxAttempts");
        Assert.True(result.DocType is not null && result.Errors.Count > 0, "draft and errors returned for manual fixing");
    }

    [Test]
    public async Task AiDesigner_modifies_existing_workflow_without_publishing()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var current = reg.Workflows.First(w => w.Name == "PurchaseRequestApproval");
        var changed = AiDraftValidator.CloneWorkflow(current);
        changed.Transitions[0].Action = "Kirim";
        changed.Name = "SomethingElse"; // the service keeps the edited workflow's name

        var chat = new ScriptedChatClient(AnswerJson(new WorkflowDesignAnswer { Explanation = "Renamed the submit action.", Workflow = changed }));
        var result = await AiWith(chat).DesignWorkflowAsync(U("admin"), "Rename Ajukan to Kirim", current, current.DocType);

        Assert.True(result.Ok, "valid: " + string.Join(" | ", result.Errors));
        Assert.Equal("PurchaseRequestApproval", result.Workflow!.Name, "name kept");
        Assert.Equal(current.Version + 1, result.Workflow.Version, "next version");
        Assert.Equal("Kirim", result.Workflow.Transitions[0].Action, "change applied to the draft");
        Assert.Equal("Ajukan", reg.Workflows.First(w => w.Name == "PurchaseRequestApproval").Transitions[0].Action, "active workflow unchanged");
    }

    [Test]
    public async Task AiDesigner_query_for_widget_is_validated()
    {
        var answer = new QueryDesignAnswer
        {
            Explanation = "Total per department.",
            Sql = "SELECT department, SUM(total) AS total_amount FROM reporting.purchase_request WHERE created >= @Start GROUP BY department ORDER BY total_amount DESC;",
            Parameters = [new WidgetParameter { Name = "@Start", Label = "Start", Type = ParamTypes.Date, DefaultValue = "2026-01-01", Binding = ParamBindings.GlobalStart }],
            Visual = new VisualConfig { Type = ChartTypes.Bar, XColumn = "department", YColumns = ["total_amount"] },
        };
        var chat = new ScriptedChatClient(AnswerJson(answer));

        var result = await AiWith(chat).DesignQueryAsync(U("admin"), QueryPurpose.Widget, "Total PR per department");

        Assert.True(result.Ok, "valid: " + string.Join(" | ", result.Errors));
        Assert.True(!result.Sql.EndsWith(';'), "trailing semicolon removed");
        Assert.Equal("Start", result.Parameters.Single().Name, "@ prefix removed from parameter name");
        Assert.Equal(ParamBindings.GlobalStart, result.Parameters.Single().Binding, "binding kept for widgets");
    }

    [Test]
    public async Task AiDesigner_query_rejects_non_reporting_tables_and_wrong_bindings()
    {
        var outsideReporting = new QueryDesignAnswer { Sql = "SELECT user_name FROM public.app_users" };
        var wrongBinding = new QueryDesignAnswer
        {
            Sql = "SELECT name FROM reporting.purchase_request WHERE created >= @Start",
            Parameters = [new WidgetParameter { Name = "Start", Type = ParamTypes.Date, DefaultValue = "2026-01-01", Binding = ParamBindings.GlobalStart }],
        };
        var chat = new ScriptedChatClient(AnswerJson(outsideReporting), AnswerJson(wrongBinding), AnswerJson(wrongBinding));

        var result = await AiWith(chat).DesignQueryAsync(U("admin"), QueryPurpose.QueryApi, "list users");

        Assert.True(!result.Ok, "not accepted");
        var firstRepair = chat.Calls[1].Last(m => m.Role == ChatRole.User).Text;
        Assert.True(firstRepair.Contains("public.app_users"), "non-reporting table reported: " + firstRepair);
        Assert.True(result.Errors.Any(e => e.Contains("global:start")), "dashboard binding rejected for query APIs: " + string.Join(" | ", result.Errors));
    }

    [Test]
    public async Task AiDesigner_rate_limit_per_user()
    {
        var answer = AnswerJson(new QueryDesignAnswer { Sql = "SELECT COUNT(*) AS documents FROM reporting.purchase_request", Visual = new VisualConfig { Type = ChartTypes.Number, YColumns = ["documents"] } });
        var ai = AiWith(new ScriptedChatClient(answer, answer), requestsPerHour: 1);

        await ai.DesignQueryAsync(U("admin"), QueryPurpose.Widget, "count PRs");
        await Assert.Throws<InvalidOperationException>(() => ai.DesignQueryAsync(U("admin"), QueryPurpose.Widget, "count PRs again"), "second request in the hour rejected");
    }

    [Test]
    public Task AiDesigner_tools_are_read_only()
    {
        var tools = new AiDesignerTools(U("admin"), sp.GetRequiredService<MetadataRegistry>(), sp.GetRequiredService<RoleCatalog>(),
            sp.GetRequiredService<IFieldTypeRegistry>(), sp.GetRequiredService<AiDraftValidator>(), QueryPurpose.Widget);

        var names = tools.MetadataTools().Concat(tools.QueryTools()).Select(t => t.Name).ToList();
        Assert.True(names.Contains("validate_doctype_draft") && names.Contains("validate_sql") && names.Contains("list_reporting_views"), "expected tools present");
        Assert.True(!names.Any(n => n.Contains("publish") || n.Contains("save") || n.Contains("delete") || n.Contains("execute") || n.Contains("run")),
            "no tool can publish, save, delete or execute: " + string.Join(", ", names));
        return Task.CompletedTask;
    }
}
