using Framework.Documents;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Visual workflow editor (P3-1): canvas edits (connect, add state, snap, auto layout), design warnings, state
/// positions in the workflow JSON, and publishing a layout-only change as a safe change that does not affect documents.
/// </summary>
sealed partial class Tests
{
    private static WorkflowDef SampleFlow() => new()
    {
        Name = "SampleFlow", Version = 1, InitialState = "Draft",
        States =
        [
            new() { Name = "Draft", DocStatus = 0 },
            new() { Name = "Review", DocStatus = 0, Tone = "pending" },
            new() { Name = "Done", DocStatus = 1, Tone = "positive" },
            new() { Name = "Orphan", DocStatus = 1 },
        ],
        Transitions =
        [
            new() { From = "Draft", To = "Review", Action = "Send", AllowedRoles = ["Staff"] },
            new() { From = "Draft", To = "Review", Action = "Send", AllowedRoles = ["Lead"] },
            new() { From = "Review", To = "Done", Action = "Approve", AllowedRoles = [] },
        ],
    };

    [Test]
    public Task WorkflowGraph_canvas_edits_and_design_warnings()
    {
        var wf = SampleFlow();
        Assert.True(WorkflowGraph.Reachable(wf).SetEquals(["Draft", "Review", "Done"]), "reachable from the initial state");
        var w = WorkflowGraph.Warnings(wf);
        Assert.True(w.Any(x => x.Kind == WorkflowGraph.WarningKind.Unreachable && x.State == "Orphan"), "unreachable state");
        Assert.True(w.Any(x => x.Kind == WorkflowGraph.WarningKind.NoRoles && x.Transition == 2), "action without roles");
        Assert.True(w.Any(x => x.Kind == WorkflowGraph.WarningKind.Duplicate && x.Transition == 1), "duplicate action from one state");
        Assert.True(w.All(x => x.Kind != WorkflowGraph.WarningKind.StuckDraft), "Review has an action out");

        // Connect: name after the target, unique per source state, roles taken from the source's other actions.
        var i = WorkflowGraph.Connect(wf, "Review", "Draft");
        Assert.Equal("Move to Draft", wf.Transitions[i].Action, "action name");
        Assert.Equal(0, wf.Transitions[i].AllowedRoles.Count, "Review's only other action has no roles yet");
        var j = WorkflowGraph.Connect(wf, "Draft", "Done");
        Assert.True(wf.Transitions[j].AllowedRoles.OrderBy(r => r).SequenceEqual(["Lead", "Staff"]), "roles from Draft's other actions");
        var k = WorkflowGraph.Connect(wf, "Draft", "Done");
        Assert.Equal("Move to Done (2)", wf.Transitions[k].Action, "second arrow gets a unique name");
        Assert.Equal(-1, WorkflowGraph.Connect(wf, "Draft", "Nope"), "unknown state");

        // Add a state at a canvas position: snapped to the grid, unique name.
        var s1 = WorkflowGraph.AddState(wf, 123.4, 57.6);
        var s2 = WorkflowGraph.AddState(wf, 0, 0);
        Assert.True(s1 is { Name: "New State", X: 120, Y: 60 } && s2.Name == "New State 2", $"{s1.Name} {s1.X},{s1.Y} / {s2.Name}");

        // A stuck draft: editable state with no action out.
        wf.States.Add(new() { Name = "Parked", DocStatus = 0 });
        WorkflowGraph.Connect(wf, "Draft", "Parked");
        Assert.True(WorkflowGraph.Warnings(wf).Any(x => x.Kind == WorkflowGraph.WarningKind.StuckDraft && x.State == "Parked"), "stuck draft");

        // Auto layout forgets positions; JSON keeps x/y only when set.
        Assert.True(MetadataJson.Serialize(wf).Contains("\"x\": 120"), "positions in JSON");
        var back = MetadataJson.ParseWorkflow(MetadataJson.Serialize(wf));
        Assert.True(back.States.Single(s => s.Name == "New State") is { X: 120, Y: 60 }, "round trip");
        WorkflowGraph.ClearLayout(wf);
        Assert.True(!MetadataJson.Serialize(wf).Contains("\"x\""), "no x/y written once cleared");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Workflow_layout_change_is_safe_and_publishes_without_affecting_documents()
    {
        // Layout-only diff.
        var before = SampleFlow();
        var after = MetadataJson.ParseWorkflow(MetadataJson.Serialize(before));
        after.States[0].X = 40; after.States[0].Y = 80;
        after.States[1].X = 260; after.States[1].Y = 80;
        var changes = WorkflowDiff.Compare(before, after);
        Assert.True(changes.Count == 1 && changes[0] is { Risk: ChangeRisk.Safe, Path: "layout" }, string.Join("; ", changes.Select(c => c.Description)));
        Assert.True(changes[0].Description.Contains("2 state(s) moved"), changes[0].Description);

        // Publish through the registry, on the board test DocType with a live workflow.
        await using var provider = BuildBoardProvider();
        var registry = provider.GetRequiredService<MetadataRegistry>();
        var docs = provider.GetRequiredService<DocumentService>();
        var ana = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
        var task = await docs.InsertAsync(ana, "BoardTask", new Dictionary<string, object?> { ["subject"] = "Layout", ["amount"] = 1, ["assignee"] = "ana" });
        var actionsBefore = (await docs.AvailableActionsAsync(ana, "BoardTask", task.Name)).Select(a => a.Action).ToList();

        var active = registry.Workflows.Single(w => w.Name == "BoardTaskFlow");
        var draft = MetadataJson.ParseWorkflow(MetadataJson.Serialize(active));
        draft.Version = active.Version + 1;
        for (var n = 0; n < draft.States.Count; n++) { draft.States[n].X = 40 + n * 200; draft.States[n].Y = 60; }
        var preview = registry.PreviewWorkflow(draft, attach: false);
        Assert.True(preview.Valid && preview.Risk == ChangeRisk.Safe && preview.Changes.All(c => c.Path == "layout"), "only a safe layout change");
        var published = registry.PublishWorkflow(draft, "admin", acknowledgeRisk: false, attach: false);
        Assert.Equal(active.Version + 1, published.Version, "new version");
        Assert.True(registry.Workflows.Single(w => w.Name == "BoardTaskFlow").States.All(s => s.X is not null && s.Y == 60), "positions kept in the published workflow");

        var actionsAfter = (await docs.AvailableActionsAsync(ana, "BoardTask", task.Name)).Select(a => a.Action).ToList();
        Assert.True(actionsBefore.SequenceEqual(actionsAfter), "documents behave the same");
        var moved = await docs.RunActionAsync(ana, "BoardTask", task.Name, "Send to review", task.Version, null);
        Assert.Equal("In Review", moved.WorkflowState, "transitions still run");
    }
}
