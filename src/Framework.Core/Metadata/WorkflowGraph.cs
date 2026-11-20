namespace Framework.Metadata;

/// <summary>
/// Graph helpers for the visual workflow editor: design warnings shown on the canvas and the edits the canvas makes
/// (connect two states, add a state at a position, auto layout). Pure functions on <see cref="WorkflowDef"/>.
/// </summary>
public static class WorkflowGraph
{
    public enum WarningKind { Unreachable, StuckDraft, NoRoles, Duplicate }

    /// <summary>A design problem. <paramref name="State"/> or <paramref name="Transition"/> (index) points at the culprit.</summary>
    public sealed record Warning(WarningKind Kind, string Message, string? State = null, int? Transition = null);

    /// <summary>Grid the canvas snaps positions to.</summary>
    public const double Grid = 10;

    public static double Snap(double v) => Math.Round(v / Grid) * Grid;

    /// <summary>States reachable from the initial state by following transitions.</summary>
    public static IReadOnlySet<string> Reachable(WorkflowDef wf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(wf.InitialState)) return seen;
        var queue = new Queue<string>([wf.InitialState]);
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            if (!seen.Add(s)) continue;
            foreach (var t in wf.Transitions.Where(t => t.From == s)) queue.Enqueue(t.To);
        }
        return seen;
    }

    /// <summary>
    /// Warnings that do not block publishing but usually mean a mistake:
    /// <list type="bullet">
    /// <item>a state no document can reach;</item>
    /// <item>an editable (draft) state that is not the start of any action, so documents get stuck there;</item>
    /// <item>an action no role may run;</item>
    /// <item>two actions with the same name from one state (only the first can run).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<Warning> Warnings(WorkflowDef wf)
    {
        var list = new List<Warning>();
        var names = wf.States.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.Ordinal);
        var reachable = Reachable(wf);
        foreach (var s in wf.States.Where(s => names.Contains(s.Name)))
        {
            if (!reachable.Contains(s.Name))
                list.Add(new(WarningKind.Unreachable, $"No action leads to \"{s.Name}\" from the initial state.", State: s.Name));
            else if (s.DocStatus == 0 && wf.Transitions.All(t => t.From != s.Name))
                list.Add(new(WarningKind.StuckDraft, $"\"{s.Name}\" is editable but has no action out of it: documents stay there.", State: s.Name));
        }
        for (var i = 0; i < wf.Transitions.Count; i++)
        {
            var t = wf.Transitions[i];
            if (t.AllowedRoles.Count == 0)
                list.Add(new(WarningKind.NoRoles, $"Nobody can run \"{t.Action}\" ({t.From} → {t.To}): choose at least one role.", Transition: i));
            if (wf.Transitions.Take(i).Any(o => o.From == t.From && string.Equals(o.Action, t.Action, StringComparison.Ordinal)))
                list.Add(new(WarningKind.Duplicate, $"\"{t.Action}\" from \"{t.From}\" is defined twice; only the first one can run.", Transition: i));
        }
        return list;
    }

    /// <summary>A state name not used yet, based on <paramref name="baseName"/> ("New State", "New State 2", …).</summary>
    public static string UniqueStateName(WorkflowDef wf, string baseName = "New State")
    {
        var name = baseName;
        for (var i = 2; wf.States.Any(s => s.Name == name); i++) name = $"{baseName} {i}";
        return name;
    }

    /// <summary>Adds a state at a canvas position (snapped to the grid) and returns it.</summary>
    public static WorkflowStateDef AddState(WorkflowDef wf, double x, double y)
    {
        var state = new WorkflowStateDef { Name = UniqueStateName(wf), DocStatus = 1, Tone = "pending", X = Snap(x), Y = Snap(y) };
        wf.States.Add(state);
        return state;
    }

    /// <summary>
    /// Adds a transition drawn on the canvas from <paramref name="from"/> to <paramref name="to"/>. The action is named
    /// after the target ("Move to X", unique among the actions of <paramref name="from"/>) and starts with the roles
    /// of the other actions out of <paramref name="from"/>, so it is usable right away. Returns its index, or -1 when a
    /// state does not exist.
    /// </summary>
    public static int Connect(WorkflowDef wf, string from, string to)
    {
        if (wf.States.All(s => s.Name != from) || wf.States.All(s => s.Name != to)) return -1;
        var baseAction = $"Move to {to}";
        var action = baseAction;
        for (var i = 2; wf.Transitions.Any(t => t.From == from && t.Action == action); i++) action = $"{baseAction} ({i})";
        var roles = wf.Transitions.Where(t => t.From == from).SelectMany(t => t.AllowedRoles).Distinct(StringComparer.Ordinal).ToList();
        wf.Transitions.Add(new WorkflowTransitionDef { From = from, To = to, Action = action, AllowedRoles = roles });
        return wf.Transitions.Count - 1;
    }

    /// <summary>Removes the saved positions so the diagram is laid out automatically again.</summary>
    public static void ClearLayout(WorkflowDef wf)
    {
        foreach (var s in wf.States) { s.X = null; s.Y = null; }
    }
}
