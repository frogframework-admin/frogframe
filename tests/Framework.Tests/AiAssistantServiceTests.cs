using Framework.Security;
using Framework.Web.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;

sealed partial class Tests
{
    private AiAssistantService AssistantWith(IChatClient client, int requestsPerHour = 30, int conversationTurns = 12)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:RequestsPerUserPerHour"] = requestsPerHour.ToString(),
            ["AI:MaxConversationTurns"] = conversationTurns.ToString(),
        }).Build();

        return new AiAssistantService(
            new FixedChatClientProvider(client),
            new AiOn(),
            configuration,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new AiRequestRateLimiter(TimeProvider.System),
            NullLogger<AiAssistantService>.Instance);
    }

    [Test]
    public async Task AiAssistantService_attaches_authenticated_page_context_and_preserves_chat_history()
    {
        var chat = new ScriptedChatClient("This document is in Draft.");
        var assistant = AssistantWith(chat);
        var user = new UserContext(
            "employee-1",
            "Taylor Employee",
            "acme",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Employee" },
            new Dictionary<string, string> { ["department"] = "Finance" });

        var first = await assistant.AskAsync(user, "Why is this draft?", "/app/JournalEntry/JV-1?tab=ledger");
        Assert.Equal("This document is in Draft.", first.Answer, "assistant returns the model answer");
        Assert.Equal(1, first.TurnsUsed, "first user message consumes one turn");
        Assert.Equal(11, first.TurnsRemaining, "conversation has a bounded turn budget");

        var sentQuestion = chat.Calls[0].Last(m => m.Role == ChatRole.User).Text;
        Assert.True(sentQuestion.Contains("\"docType\":\"JournalEntry\""), "context detects the active DocType");
        Assert.True(sentQuestion.Contains("\"documentName\":\"JV-1\""), "context detects the active document");
        Assert.True(sentQuestion.Contains("\"currentUrl\":\"/app/JournalEntry/JV-1\""), "context excludes query-string data");
        Assert.True(sentQuestion.Contains("\"roles\":[\"Employee\"]"), "roles come from the authenticated user");
        Assert.True(sentQuestion.Contains("\"department\":\"Finance\""), "department comes from authenticated attributes");

        var followUpClient = new ScriptedChatClient("The report reads submitted entries only.");
        var followUpAssistant = AssistantWith(followUpClient);
        await followUpAssistant.AskAsync(user, "Why is this report empty?", "/reports/trial-balance",
        [
            new AiAssistantTurn("user", "Why is this draft?"),
            new AiAssistantTurn("assistant", first.Answer)
        ]);
        var followUpMessages = followUpClient.Calls[0];
        Assert.True(followUpMessages.Any(m => m.Role == ChatRole.Assistant && m.Text == first.Answer),
            "prior assistant turns are preserved in the conversation");
        var reportContext = followUpMessages.Last(m => m.Role == ChatRole.User).Text;
        Assert.True(reportContext.Contains("\"reportName\":\"trial-balance\""), "context detects the active report");
    }

    [Test]
    public async Task AiAssistantService_enforces_per_user_rate_limits_and_conversation_turn_budget()
    {
        var user = new UserContext("limited-user", "Employee", "acme",
            new HashSet<string> { "Employee" }, new Dictionary<string, string>());
        var rateLimited = AssistantWith(new ScriptedChatClient("First answer."), requestsPerHour: 1);
        await rateLimited.AskAsync(user, "First question", "/");
        await Assert.Throws<InvalidOperationException>(
            () => rateLimited.AskAsync(user, "Second question", "/"),
            "second request exceeds the hourly per-user limit");

        var budgeted = AssistantWith(new ScriptedChatClient("No answer should be generated."), conversationTurns: 1);
        await Assert.Throws<InvalidOperationException>(
            () => budgeted.AskAsync(user, "One more question", "/",
            [
                new AiAssistantTurn("user", "First question"),
                new AiAssistantTurn("assistant", "First answer")
            ]),
            "completed turns beyond the conversation budget are rejected");
    }

    [Test]
    public async Task AiAssistantService_handles_new_document_route_and_long_assistant_history()
    {
        // DI resolves AiAssistantService and AiRequestRateLimiter cleanly
        var resolved = sp.GetRequiredService<AiAssistantService>();
        Assert.True(resolved.ConversationTurnLimit >= 1, "DI-registered AiAssistantService has valid turn limit");

        var user = new UserContext("employee-2", "Employee", "acme",
            new HashSet<string> { "Employee" }, new Dictionary<string, string>());

        // /app/{DocType}/new should detect DocType but leave documentName null
        var newCtx = AiAssistantService.BuildPageContext(user, "/app/JournalEntry/new");
        Assert.Equal("JournalEntry", newCtx.DocType, "new document route detects DocType");
        Assert.Equal<string?>(null, newCtx.DocumentName, "'new' segment is not treated as a document name");

        // Long assistant replies (> MaxPromptChars = 4000) in prior turns should not break follow-up questions
        var longReply = new string('x', 5000);
        var chat = new ScriptedChatClient("Follow-up answer.");
        var assistant = AssistantWith(chat);
        var reply = await assistant.AskAsync(user, "Summarize that", "/app/JournalEntry",
        [
            new AiAssistantTurn("user", "Explain everything"),
            new AiAssistantTurn("assistant", longReply)
        ]);
        Assert.Equal("Follow-up answer.", reply.Answer, "assistant handles long prior assistant reply");
    }
}
