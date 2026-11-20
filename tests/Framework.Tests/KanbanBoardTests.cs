using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// List board view (P1-1): columns follow the workflow states, a card's column follows its state, and the moves a
/// user can drop are exactly the workflow transitions allowed by role and condition. The server re-checks every move.
/// </summary>
sealed partial class Tests
{
    private static ServiceProvider BuildBoardProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-board-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "BoardTask.doctype.json"), """
        {
          "name": "BoardTask", "version": 1, "title": "Board Task", "module": "CORE", "workflow": "BoardTaskFlow",
          "naming": { "pattern": "BT-{#####}" },
          "listView": { "titleField": "subject", "searchFields": ["name", "subject"] },
          "fields": [
            { "name": "subject", "type": "Data", "label": "Subject", "requiredWhen": "true", "inListView": true },
            { "name": "amount", "type": "Int", "label": "Amount", "inListView": true },
            { "name": "assignee", "type": "Data", "label": "Assignee", "inListView": true }
          ],
          "permissions": [
            { "role": "Staff", "actions": ["read", "create", "write"], "scope": "all" },
            { "role": "Lead", "actions": ["read", "create", "write"], "scope": "all" }
          ]
        }
        """);
        File.WriteAllText(Path.Combine(dir, "BoardTaskFlow.workflow.json"), """
        {
          "name": "BoardTaskFlow", "docType": "BoardTask", "version": 1, "initialState": "Open",
          "states": [
            { "name": "Open", "docStatus": 0, "tone": "neutral" },
            { "name": "In Review", "docStatus": 0, "tone": "pending" },
            { "name": "Done", "docStatus": 1, "tone": "positive" },
            { "name": "Dropped", "docStatus": 2, "tone": "negative" }
          ],
          "transitions": [
            { "from": "Open", "to": "In Review", "action": "Send to review", "allowedRoles": ["Staff", "Lead"], "condition": "assignee == user.id || has_role('Lead')" },
            { "from": "In Review", "to": "Done", "action": "Approve", "allowedRoles": ["Lead"], "condition": "amount <= 1000" },
            { "from": "In Review", "to": "Done", "action": "Approve with exception", "allowedRoles": ["Lead"], "requireComment": true },
            { "from": "In Review", "to": "Open", "action": "Send back", "allowedRoles": ["Lead"] },
            { "from": "Open", "to": "Dropped", "action": "Drop", "allowedRoles": ["Lead"], "requireComment": true }
          ]
        }
        """);
        File.WriteAllText(Path.Combine(dir, "BoardNote.doctype.json"), """
        { "name": "BoardNote", "version": 1, "title": "Board Note", "module": "CORE",
          "document": { "allowSubmit": true, "allowCancel": true, "allowAmend": true },
          "fields": [ { "name": "title", "type": "Data", "label": "Title" } ],
          "permissions": [ { "role": "Staff", "actions": ["read", "create", "write", "submit", "cancel"], "scope": "all" } ] }
        """);
        File.WriteAllText(Path.Combine(dir, "PlainNote.doctype.json"), """
        { "name": "PlainNote", "version": 1, "title": "Plain Note", "module": "CORE",
          "fields": [ { "name": "title", "type": "Data", "label": "Title" } ],
          "permissions": [ { "role": "Staff", "actions": ["read", "create", "write"], "scope": "all" } ] }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddFrogFramework(o => o.LoadFrom(dir));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Board_columns_card_placement_and_moves_follow_workflow_roles_and_conditions()
    {
        await using var provider = BuildBoardProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var meta = provider.GetRequiredService<IMetadataProvider>().Current.Get("BoardTask");
        var ana = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
        var budi = new UserContext("budi", "Budi", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
        var lead = new UserContext("lia", "Lia", "acme", new HashSet<string> { "Lead" }, new Dictionary<string, string>());

        Assert.True(WorkflowBoard.Supports(meta), "workflow DocType offers the board");
        Assert.True(WorkflowBoard.Columns(meta).Select(c => c.Key).SequenceEqual(["Open", "In Review", "Done", "Dropped"]), "columns in workflow order");
        Assert.Equal("pending", WorkflowBoard.Columns(meta)[1].Tone, "column tone from the state");

        var small = await docs.InsertAsync(ana, "BoardTask", new Dictionary<string, object?> { ["subject"] = "Small", ["amount"] = 500, ["assignee"] = "ana" });
        var big = await docs.InsertAsync(ana, "BoardTask", new Dictionary<string, object?> { ["subject"] = "Big", ["amount"] = 5000, ["assignee"] = "ana" });

        // Card placement from the list rows.
        var list = await docs.ListAsync(ana, "BoardTask", new ListRequest(), allowAllReadableFields: true);
        Assert.True(list.Items.All(r => WorkflowBoard.ColumnOf(meta, r) == "Open"), "new documents sit in the initial state");
        Assert.True(list.Items.All(r => r.ContainsKey("version") && r.ContainsKey("_title")), "list rows carry version and title for the cards");
        Assert.Equal("Open", WorkflowBoard.ColumnOf(meta, null, DocStatus.Draft), "stateless draft → initial state");
        Assert.Equal("Done", WorkflowBoard.ColumnOf(meta, null, DocStatus.Submitted), "stateless submitted → first submitted state");

        // Condition guard: only the assignee (or a Lead) can send to review.
        var budiMoves = WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(budi, "BoardTask", small.Name), "In Review");
        Assert.Equal(0, budiMoves.Count, "Budi is not the assignee: no move to In Review");
        Assert.Equal(0, WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(ana, "BoardTask", small.Name), "Done").Count, "no direct Open → Done move");
        await Assert.Throws<PermissionDeniedException>(() =>
            docs.RunActionAsync(budi, "BoardTask", small.Name, "Send to review", small.Version, null), "server rejects a forged drop");

        var anaMoves = WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(ana, "BoardTask", small.Name), "In Review");
        Assert.Equal(1, anaMoves.Count, "assignee can move to In Review");
        small = await docs.RunActionAsync(ana, "BoardTask", small.Name, anaMoves[0].Action, small.Version, null);
        big = await docs.RunActionAsync(ana, "BoardTask", big.Name, "Send to review", big.Version, null);
        Assert.Equal("In Review", WorkflowBoard.ColumnOf(meta, small.WorkflowState, small.DocStatus), "card moved column");

        // Role guard: staff cannot approve; the lead gets two actions into Done for the small task, one for the big task.
        Assert.Equal(0, WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(ana, "BoardTask", small.Name), "Done").Count, "staff cannot move to Done");
        var smallDone = WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(lead, "BoardTask", small.Name), "Done");
        Assert.True(smallDone.Select(a => a.Action).SequenceEqual(["Approve", "Approve with exception"]), "two actions lead to Done: the board asks which");
        var bigDone = WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(lead, "BoardTask", big.Name), "Done");
        Assert.True(bigDone.Count == 1 && bigDone[0].RequireComment, "amount above the limit: only the exception path, which needs a comment");
        await Assert.Throws<ValidationFailedException>(() =>
            docs.RunActionAsync(lead, "BoardTask", big.Name, "Approve with exception", big.Version, null), "comment is required by the server too");

        // Stale card: a move with the version the board rendered fails after someone else moved the document.
        var stale = small.Version;
        small = await docs.RunActionAsync(lead, "BoardTask", small.Name, "Send back", small.Version, null);
        await Assert.Throws<ConcurrencyException>(() =>
            docs.RunActionAsync(lead, "BoardTask", small.Name, "Approve", stale, null), "stale card version is rejected");

        big = await docs.RunActionAsync(lead, "BoardTask", big.Name, "Approve with exception", big.Version, "Budget approved by finance");
        Assert.Equal("Done", big.WorkflowState, "moved to Done with comment");
        Assert.Equal(DocStatus.Submitted, big.DocStatus, "Done is a submitted state");
        Assert.Equal(0, WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(lead, "BoardTask", big.Name), "Open").Count, "no transitions out of Done");
    }

    [Test]
    public async Task Board_without_workflow_uses_docstatus_columns_and_ignores_amend()
    {
        await using var provider = BuildBoardProvider();
        var snapshot = provider.GetRequiredService<IMetadataProvider>().Current;
        var docs = provider.GetRequiredService<DocumentService>();
        var note = snapshot.Get("BoardNote");
        var staff = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
        Assert.True(WorkflowBoard.Supports(note), "submittable DocType offers the board");
        Assert.True(!WorkflowBoard.Supports(snapshot.Get("PlainNote")), "a DocType without workflow or submit has no board");
        Assert.True(WorkflowBoard.Columns(note).Select(c => c.Key).SequenceEqual(["Draft", "Submitted", "Cancelled"]), "DocStatus columns");

        var d = await docs.InsertAsync(staff, "BoardNote", new Dictionary<string, object?> { ["title"] = "Note" });
        var toSubmitted = WorkflowBoard.MovesTo(await docs.AvailableActionsAsync(staff, "BoardNote", d.Name), "Submitted");
        Assert.True(toSubmitted.Count == 1 && toSubmitted[0].Action == DocActions.Submit, "Draft → Submitted runs Submit");
        d = await docs.RunActionAsync(staff, "BoardNote", d.Name, DocActions.Submit, d.Version, null);
        var row = (await docs.ListAsync(staff, "BoardNote", new ListRequest(), allowAllReadableFields: true)).Items.Single();
        Assert.Equal("Submitted", WorkflowBoard.ColumnOf(note, row), "row placed by docstatus");
        d = await docs.RunActionAsync(staff, "BoardNote", d.Name, DocActions.Cancel, d.Version, "wrong note");
        Assert.Equal(0, WorkflowBoard.MovesTo([new ActionView(DocActions.Amend, "Draft", false, "neutral")], "Draft").Count,
            "amend creates a revision, it is not a move back to Draft");
    }
}
